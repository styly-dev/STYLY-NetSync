// PythonServerFixture.cs - Launches the LOCAL Python server (same branch code) for
// PlayMode integration tests. Uses `uv run --directory` so the checkout's editable
// src-layout runs (never a released PyPI build), on ephemeral loopback ports with
// UDP discovery disabled so a developer's manually-running server is untouched.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Styly.NetSync.Tests
{
    public sealed class PythonServerFixture : IDisposable
    {
        public int ControlPort { get; private set; }
        public int TransformPort { get; private set; }
        public int PubPort { get; private set; }
        public int RestApiPort { get; private set; }
        public string ServerAddress => "127.0.0.1";
        public string RestBaseUrl => $"http://127.0.0.1:{RestApiPort}";

        private Process _process;
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly object _logLock = new();

        // <repo>/STYLY-NetSync-Unity/Assets -> up two -> <repo>
        public static string RepoRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        public static string ServerDir => Path.Combine(RepoRoot, "STYLY-NetSync-Server");

        private static bool? _uvAvailable;

        /// <summary>True if `uv` is runnable on PATH (cached for the session).</summary>
        public static bool IsUvAvailable()
        {
            if (_uvAvailable.HasValue) return _uvAvailable.Value;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "uv",
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi);
                proc.WaitForExit(10000);
                _uvAvailable = proc.HasExited && proc.ExitCode == 0;
            }
            catch (Exception)
            {
                _uvAvailable = false;
            }
            return _uvAvailable.Value;
        }

        /// <summary>
        /// Launch the local server and block until its REST health endpoint answers
        /// 200 (which the server only exposes after all ZMQ sockets are bound).
        /// </summary>
        public void Start(float readyTimeoutSeconds = 120f)
        {
            var pyproject = Path.Combine(ServerDir, "pyproject.toml");
            if (!File.Exists(pyproject))
            {
                throw new FileNotFoundException(
                    $"Server project not found at {ServerDir}. Expected {pyproject}.");
            }

            // One start attempt; retry once if the process dies inside the window
            // (guards against the port-0 pick being taken before the server binds).
            if (!TryStartOnce(readyTimeoutSeconds))
            {
                Stop();
                if (!TryStartOnce(readyTimeoutSeconds))
                {
                    throw new Exception(
                        "Python server failed to become ready.\n" + DumpLogs());
                }
            }
        }

        private bool TryStartOnce(float readyTimeoutSeconds)
        {
            PickFreePorts();
            lock (_logLock) { _stdout.Clear(); _stderr.Clear(); }

            var args =
                $"run --directory \"{ServerDir}\" styly-netsync-server " +
                $"--control-port {ControlPort} --transform-port {TransformPort} " +
                $"--pub-port {PubPort} --rest-api-port {RestApiPort} --no-server-discovery";

            var psi = new ProcessStartInfo
            {
                FileName = "uv",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ServerDir,
            };

            _process = new Process { StartInfo = psi };
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) { lock (_logLock) { _stdout.AppendLine(e.Data); } }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) { lock (_logLock) { _stderr.AppendLine(e.Data); } }
            };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(readyTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited) { return false; }
                if (HttpGet(RestBaseUrl + "/").status == 200) { return true; }
                System.Threading.Thread.Sleep(250);
            }
            return false;
        }

        private void PickFreePorts()
        {
            // Reserve four distinct loopback ports, then release them for the server.
            var listeners = new TcpListener[4];
            var ports = new int[4];
            for (int i = 0; i < 4; i++)
            {
                listeners[i] = new TcpListener(IPAddress.Loopback, 0);
                listeners[i].Start();
                ports[i] = ((IPEndPoint)listeners[i].LocalEndpoint).Port;
            }
            foreach (var l in listeners) { l.Stop(); }

            ControlPort = ports[0];
            TransformPort = ports[1];
            PubPort = ports[2];
            RestApiPort = ports[3];
        }

        public void Stop()
        {
            if (_process == null) { return; }
            try
            {
                if (!_process.HasExited)
                {
                    // Kill the whole process tree: `uv` spawns a python child that
                    // owns the sockets. Process.Kill(bool) is not available under
                    // Unity's API profile, so use taskkill /T on Windows.
                    KillProcessTree(_process.Id);
                    _process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PythonServerFixture] Stop() error: {ex.Message}");
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        private static void KillProcessTree(int pid)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {pid} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var killer = Process.Start(psi);
            killer.WaitForExit(5000);
#else
            // Walk the whole descendant tree: `uv` may interpose a shim so the
            // python server that owns the sockets can be a grandchild, which a
            // plain `pkill -P {pid}` (direct children only) would orphan.
            var descendants = new List<int>();
            CollectDescendantPids(pid, descendants);
            foreach (var child in descendants)
            {
                try { Process.GetProcessById(child).Kill(); } catch { /* best effort */ }
            }
            try { Process.GetProcessById(pid).Kill(); } catch { }
#endif
        }

#if !(UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN)
        private static void CollectDescendantPids(int pid, List<int> result)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pgrep",
                    Arguments = $"-P {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var pgrep = Process.Start(psi);
                var output = pgrep.StandardOutput.ReadToEnd();
                pgrep.WaitForExit(5000);
                foreach (var line in output.Split('\n'))
                {
                    if (int.TryParse(line.Trim(), out var child))
                    {
                        result.Add(child);
                        CollectDescendantPids(child, result);
                    }
                }
            }
            catch { /* best effort */ }
        }
#endif

        public string DumpLogs()
        {
            lock (_logLock)
            {
                return $"--- server stdout ---\n{_stdout}\n--- server stderr ---\n{_stderr}";
            }
        }

        #region === REST helpers (synchronous, loopback only) ===

        // Loopback-only requests answer in milliseconds; a short timeout bounds
        // the worst-case main-thread stall when called from PlayMode poll loops.
        private const int HttpTimeoutMs = 2000;

        public (bool ok, int status, string body) HttpGet(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = HttpTimeoutMs;
                req.ReadWriteTimeout = HttpTimeoutMs;
                using var resp = (HttpWebResponse)req.GetResponse();
                using var reader = new StreamReader(resp.GetResponseStream());
                return (true, (int)resp.StatusCode, reader.ReadToEnd());
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse r)
                {
                    using var reader = new StreamReader(r.GetResponseStream());
                    return (false, (int)r.StatusCode, reader.ReadToEnd());
                }
                return (false, 0, ex.Message);
            }
        }

        public (bool ok, int status, string body) HttpPostJson(string url, string json)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = HttpTimeoutMs;
                req.ReadWriteTimeout = HttpTimeoutMs;
                var bytes = Encoding.UTF8.GetBytes(json);
                using (var s = req.GetRequestStream()) { s.Write(bytes, 0, bytes.Length); }
                using var resp = (HttpWebResponse)req.GetResponse();
                using var reader = new StreamReader(resp.GetResponseStream());
                return (true, (int)resp.StatusCode, reader.ReadToEnd());
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse r)
                {
                    using var reader = new StreamReader(r.GetResponseStream());
                    return (false, (int)r.StatusCode, reader.ReadToEnd());
                }
                return (false, 0, ex.Message);
            }
        }

        /// <summary>GET a global variable; returns false on 404 (not yet present).</summary>
        public bool TryGetGlobalVariable(string room, string name, out string value)
        {
            value = null;
            var (ok, status, body) = HttpGet(
                $"{RestBaseUrl}/v1/rooms/{Uri.EscapeDataString(room)}/global-variables/{Uri.EscapeDataString(name)}");
            if (!ok || status != 200) { return false; }
            value = ExtractJsonString(body, "value");
            return value != null;
        }

        public void PostGlobalVariable(string room, string name, string value)
        {
            // Serialize the body properly so names/values containing quotes or
            // backslashes reach the server unmangled.
            var json = new JObject
            {
                ["variables"] = new JObject { [name] = value },
            }.ToString(Formatting.None);
            var (ok, status, body) = HttpPostJson(
                $"{RestBaseUrl}/v1/rooms/{Uri.EscapeDataString(room)}/global-variables", json);
            if (!ok) { throw new Exception($"POST global var failed ({status}): {body}"); }
        }

        public bool TryGetClientVariable(string room, string deviceId, string name, out string value)
        {
            value = null;
            var (ok, status, body) = HttpGet(
                $"{RestBaseUrl}/v1/rooms/{Uri.EscapeDataString(room)}/devices/{Uri.EscapeDataString(deviceId)}" +
                $"/client-variables/{Uri.EscapeDataString(name)}");
            if (!ok || status != 200) { return false; }
            value = ExtractJsonString(body, "value");
            return value != null;
        }

        // Parse the response as real JSON so escaped quotes/backslashes in valid
        // payloads are read correctly.
        private static string ExtractJsonString(string json, string key)
        {
            try
            {
                var token = JObject.Parse(json)[key];
                return token != null && token.Type == JTokenType.String
                    ? token.Value<string>()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        #endregion

        public void Dispose() => Stop();
    }
}
