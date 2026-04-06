// NetworkVariableManager.cs - Handles Network Variables system
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace Styly.NetSync
{
    /// <summary>
    /// Tagged union for network variable values (string or byte[]).
    /// </summary>
    internal readonly struct NVValue : IEquatable<NVValue>
    {
        public readonly byte TypeTag; // VAR_TYPE_STRING=0 or VAR_TYPE_BYTES=1
        private readonly byte[] _rawBytes;
        public byte[] RawBytes => _rawBytes ?? Array.Empty<byte>();

        public NVValue(byte typeTag, byte[] rawBytes)
        {
            TypeTag = typeTag;
            _rawBytes = rawBytes ?? Array.Empty<byte>();
        }

        public static NVValue FromString(string value)
        {
            return new NVValue(BinarySerializer.VAR_TYPE_STRING,
                value != null ? Encoding.UTF8.GetBytes(value) : Array.Empty<byte>());
        }

        public static NVValue FromBytes(byte[] value)
        {
            return new NVValue(BinarySerializer.VAR_TYPE_BYTES, value ?? Array.Empty<byte>());
        }

        public string AsString => TypeTag == BinarySerializer.VAR_TYPE_STRING ? Encoding.UTF8.GetString(RawBytes) : null;

        public bool Equals(NVValue other)
        {
            return TypeTag == other.TypeTag && RawBytes.SequenceEqual(other.RawBytes);
        }

        public override bool Equals(object obj) => obj is NVValue other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = TypeTag;
                for (int i = 0; i < Math.Min(RawBytes.Length, 16); i++)
                    hash = hash * 31 + RawBytes[i];
                return hash;
            }
        }

        public static bool operator ==(NVValue left, NVValue right) => left.Equals(right);
        public static bool operator !=(NVValue left, NVValue right) => !left.Equals(right);
    }

    internal class NetworkVariableManager
    {
        private readonly IConnectionManager _connectionManager;
        private readonly string _deviceId;
        private readonly NetSyncManager _netSyncManager;

        // Reusable serialization resources to reduce allocations per send
        private readonly ReusableBufferWriter _buf;
        private const int INITIAL_BUFFER_CAPACITY = 4096;

        // Network Variables storage
        private readonly Dictionary<string, NVValue> _globalVariables = new();
        private readonly Dictionary<int, Dictionary<string, NVValue>> _clientVariables = new();

        // Network Variables limits (must match server)
        private const int MAX_GLOBAL_VARS = 100;
        private const int MAX_CLIENT_VARS = 100;
        private const int MAX_VAR_NAME_LENGTH = 64;
        private const int MAX_VAR_VALUE_LENGTH = 65536;

        // Debounce configuration
        private const double DEBOUNCE_INTERVAL = 0.1; // 100ms debounce

        // Send-side dedupe caches
        private readonly Dictionary<string, NVValue> _lastSentGlobal = new();
        private readonly Dictionary<(int, string), NVValue> _lastSentClient = new();

        // Leading-edge throttles (cooldowns) for NV sends
        private readonly Dictionary<string, double> _nextAllowedGlobal = new();
        private readonly Dictionary<(int clientNo, string name), double> _nextAllowedClient = new();

        // Debounce buffers for pending sends
        private readonly Dictionary<string, NVValue> _pendingGlobal = new();
        private readonly Dictionary<string, double> _dueGlobal = new();
        private readonly Dictionary<(int, string), NVValue> _pendingClient = new();
        private readonly Dictionary<(int, string), double> _dueClient = new();

        // Events for string-typed variables (using C# events, NOT SendMessage)
        public event Action<string, string, string> OnGlobalVariableChanged;
        public event Action<int, string, string, string> OnClientVariableChanged;

        // Events for byte[]-typed variables
        public event Action<string, byte[], byte[]> OnGlobalBytesVariableChanged;
        public event Action<int, string, byte[], byte[]> OnClientBytesVariableChanged;

        // Flag to track if initial network variables have been received
        private bool _hasReceivedInitialSync = false;
        private DateTime _connectionEstablishedTime = DateTime.MinValue; // Track when connection was established
        // Conservative timeout for initial network variable sync to handle empty rooms
        private const float INITIAL_SYNC_TIMEOUT = 2.0f;

        public bool HasReceivedInitialSync => _hasReceivedInitialSync;

        /// <summary>
        /// Mark initial sync as complete (used in offline mode)
        /// </summary>
        public void MarkInitialSyncComplete()
        {
            _hasReceivedInitialSync = true;
        }

        /// <summary>
        /// Reset the initial sync flag (called when connection is lost)
        /// </summary>
        public void ResetInitialSyncFlag()
        {
            _hasReceivedInitialSync = false;
            _connectionEstablishedTime = DateTime.MinValue;
        }

        /// <summary>
        /// Called when connection is established to start tracking sync timeout
        /// </summary>
        public void OnConnectionEstablished()
        {
            _connectionEstablishedTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Check if we should consider initial sync complete based on timeout
        /// This handles cases where server has no variables to send
        /// </summary>
        public void CheckInitialSyncTimeout()
        {
            if (!_hasReceivedInitialSync && _connectionEstablishedTime != DateTime.MinValue)
            {
                if ((DateTime.UtcNow - _connectionEstablishedTime).TotalSeconds >= INITIAL_SYNC_TIMEOUT)
                {
                    _hasReceivedInitialSync = true;
                    Debug.Log($"[NetworkVariableManager] Initial sync timeout after {INITIAL_SYNC_TIMEOUT}s - ready without variables");
                }
            }
        }

        public NetworkVariableManager(IConnectionManager connectionManager, string deviceId, NetSyncManager netSyncManager)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            _netSyncManager = netSyncManager;
            _buf = new ReusableBufferWriter(INITIAL_BUFFER_CAPACITY);
        }

        // Global Variables API (string)
        public bool SetGlobalVariable(string name, string value, string roomId)
        {
            if (!ValidateVariableName(name) || !ValidateStringValue(value))
                return false;

            return SetGlobalVariableInternal(name, NVValue.FromString(value), roomId);
        }

        // Global Variables API (byte[])
        public bool SetGlobalVariable(string name, byte[] value, string roomId)
        {
            if (!ValidateVariableName(name) || !ValidateBytesValue(value))
                return false;

            return SetGlobalVariableInternal(name, NVValue.FromBytes(value), roomId);
        }

        private bool SetGlobalVariableInternal(string name, NVValue nvValue, string roomId)
        {
            if (_globalVariables.Count >= MAX_GLOBAL_VARS && !_globalVariables.ContainsKey(name))
            {
                Debug.LogWarning($"Global variable limit ({MAX_GLOBAL_VARS}) reached");
                return false;
            }

            // Offline mode: write locally and fire event without server round-trip
            if (_netSyncManager != null && _netSyncManager.IsOfflineMode)
            {
                var hasOld = _globalVariables.TryGetValue(name, out var existing);
                if (!hasOld || existing != nvValue)
                {
                    _globalVariables[name] = nvValue;
                    FireGlobalEvent(name, hasOld ? existing : default, nvValue);
                }
                return true;
            }

            // Dedupe: same as the last actually sent value -> skip, but treat as success
            if (_lastSentGlobal.TryGetValue(name, out var lastSent) && lastSent == nvValue)
                return true;

            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            // Leading-edge attempt if cooldown elapsed (or not set)
            bool allowImmediate = !_nextAllowedGlobal.TryGetValue(name, out var next) || now >= next;
            if (allowImmediate)
            {
                // Try immediate send; only start cooldown if it *actually* sent
                if (TrySendGlobalNow(name, nvValue, roomId))
                {
                    _nextAllowedGlobal[name] = now + DEBOUNCE_INTERVAL;
                }

                // Always schedule trailing edge (latest-wins) and DO NOT extend it later
                _pendingGlobal[name] = nvValue;
                if (!_dueGlobal.ContainsKey(name))
                    _dueGlobal[name] = now + DEBOUNCE_INTERVAL;

                return true;
            }

            // Inside cooldown: update pending value but keep the original deadline
            _pendingGlobal[name] = nvValue;
            if (!_dueGlobal.ContainsKey(name))
                _dueGlobal[name] = next;

            return true;
        }

        // Internal method to send now (used by flush)
        private bool TrySendGlobalNow(string name, NVValue nvValue, string roomId)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var data = new Dictionary<string, object>
            {
                ["senderClientNo"] = _netSyncManager.ClientNo,
                ["variableName"] = name,
                ["variableValueType"] = nvValue.TypeTag,
                ["timestamp"] = timestamp
            };
            if (nvValue.TypeTag == BinarySerializer.VAR_TYPE_BYTES)
                data["variableValueBytes"] = nvValue.RawBytes;
            else
                data["variableValue"] = nvValue.AsString;

            try
            {
                var required = EstimateNVSetSize(name, nvValue.RawBytes.Length, hasTarget: false);
                _buf.EnsureCapacity(required);

                _buf.Stream.Position = 0;
                BinarySerializer.SerializeGlobalVarSetInto(_buf.Writer, data);
                _buf.Writer.Flush();
                var length = (int)_buf.Stream.Position;

                var payload = new byte[length];
                Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
                var sent = _connectionManager.TryEnqueueControl(roomId, payload);
                if (sent)
                {
                    _lastSentGlobal[name] = nvValue;
                }
                return sent;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to send global variable: {ex.Message}");
                return false;
            }
        }

        public string GetGlobalVariable(string name, string defaultValue = null)
        {
            if (!_hasReceivedInitialSync)
            {
                throw new InvalidOperationException("Cannot get global variables before OnReady event. Please wait for OnReady to be fired.");
            }
            if (_globalVariables.TryGetValue(name, out var nv) && nv.TypeTag == BinarySerializer.VAR_TYPE_STRING)
                return nv.AsString;
            return defaultValue;
        }

        public byte[] GetGlobalVariableBytes(string name, byte[] defaultValue = null)
        {
            if (!_hasReceivedInitialSync)
            {
                throw new InvalidOperationException("Cannot get global variables before OnReady event. Please wait for OnReady to be fired.");
            }
            if (_globalVariables.TryGetValue(name, out var nv) && nv.TypeTag == BinarySerializer.VAR_TYPE_BYTES)
                return nv.RawBytes;
            return defaultValue;
        }

        // Client Variables API (string)
        public bool SetClientVariable(string name, string value, int targetClientNo, string roomId)
        {
            if (!ValidateVariableName(name) || !ValidateStringValue(value))
                return false;

            return SetClientVariableInternal(name, NVValue.FromString(value), targetClientNo, roomId);
        }

        // Client Variables API (byte[])
        public bool SetClientVariable(string name, byte[] value, int targetClientNo, string roomId)
        {
            if (!ValidateVariableName(name) || !ValidateBytesValue(value))
                return false;

            return SetClientVariableInternal(name, NVValue.FromBytes(value), targetClientNo, roomId);
        }

        private bool SetClientVariableInternal(string name, NVValue nvValue, int targetClientNo, string roomId)
        {
            if (!_clientVariables.ContainsKey(targetClientNo))
                _clientVariables[targetClientNo] = new Dictionary<string, NVValue>();

            var clientVars = _clientVariables[targetClientNo];
            if (clientVars.Count >= MAX_CLIENT_VARS && !clientVars.ContainsKey(name))
            {
                Debug.LogWarning($"Client variable limit ({MAX_CLIENT_VARS}) reached for client {targetClientNo}");
                return false;
            }

            // Offline mode: write locally and fire event without server round-trip
            if (_netSyncManager != null && _netSyncManager.IsOfflineMode)
            {
                var hasOld = clientVars.TryGetValue(name, out var existing);
                if (!hasOld || existing != nvValue)
                {
                    clientVars[name] = nvValue;
                    FireClientEvent(targetClientNo, name, hasOld ? existing : default, nvValue);
                }
                return true;
            }

            var key = (targetClientNo, name);

            // Dedupe: same as the last actually sent value -> skip, but treat as success
            if (_lastSentClient.TryGetValue(key, out var lastSent) && lastSent == nvValue)
                return true;

            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            // Leading-edge attempt if cooldown elapsed
            bool allowImmediate = !_nextAllowedClient.TryGetValue(key, out var next) || now >= next;
            if (allowImmediate)
            {
                if (TrySendClientNow(targetClientNo, name, nvValue, roomId))
                {
                    _nextAllowedClient[key] = now + DEBOUNCE_INTERVAL;
                }

                // Schedule trailing and keep its deadline fixed
                _pendingClient[key] = nvValue;
                if (!_dueClient.ContainsKey(key))
                    _dueClient[key] = now + DEBOUNCE_INTERVAL;

                return true;
            }

            // Inside cooldown: update pending value but keep original due time
            _pendingClient[key] = nvValue;
            if (!_dueClient.ContainsKey(key))
                _dueClient[key] = next;

            return true;
        }

        // Internal method to send now (used by flush)
        private bool TrySendClientNow(int targetClientNo, string name, NVValue nvValue, string roomId)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var data = new Dictionary<string, object>
            {
                ["senderClientNo"] = _netSyncManager.ClientNo,
                ["targetClientNo"] = targetClientNo,
                ["variableName"] = name,
                ["variableValueType"] = nvValue.TypeTag,
                ["timestamp"] = timestamp
            };
            if (nvValue.TypeTag == BinarySerializer.VAR_TYPE_BYTES)
                data["variableValueBytes"] = nvValue.RawBytes;
            else
                data["variableValue"] = nvValue.AsString;

            try
            {
                var required = EstimateNVSetSize(name, nvValue.RawBytes.Length, hasTarget: true);
                _buf.EnsureCapacity(required);

                _buf.Stream.Position = 0;
                BinarySerializer.SerializeClientVarSetInto(_buf.Writer, data);
                _buf.Writer.Flush();
                var length = (int)_buf.Stream.Position;

                var payload = new byte[length];
                Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
                var sent = _connectionManager.TryEnqueueControl(roomId, payload);
                if (sent)
                {
                    var key = (targetClientNo, name);
                    _lastSentClient[key] = nvValue;
                }
                return sent;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to send client variable: {ex.Message}");
                return false;
            }
        }

        public string GetClientVariable(string name, int clientNo, string defaultValue = null)
        {
            if (!_hasReceivedInitialSync)
            {
                throw new InvalidOperationException("Cannot get client variables before OnReady event. Please wait for OnReady to be fired.");
            }
            if (_clientVariables.TryGetValue(clientNo, out var clientVars))
            {
                if (clientVars.TryGetValue(name, out var nv) && nv.TypeTag == BinarySerializer.VAR_TYPE_STRING)
                    return nv.AsString;
            }
            return defaultValue;
        }

        public byte[] GetClientVariableBytes(string name, int clientNo, byte[] defaultValue = null)
        {
            if (!_hasReceivedInitialSync)
            {
                throw new InvalidOperationException("Cannot get client variables before OnReady event. Please wait for OnReady to be fired.");
            }
            if (_clientVariables.TryGetValue(clientNo, out var clientVars))
            {
                if (clientVars.TryGetValue(name, out var nv) && nv.TypeTag == BinarySerializer.VAR_TYPE_BYTES)
                    return nv.RawBytes;
            }
            return defaultValue;
        }

        // Helper methods for type conversion
        private static object[] ConvertToObjectArray(object obj)
        {
            return obj switch
            {
                object[] arr => arr,
                JArray jArr => jArr.ToObject<object[]>(),
                _ => null
            };
        }

        private static Dictionary<string, object> ConvertToDictionary(object obj)
        {
            return obj switch
            {
                Dictionary<string, object> dict => dict,
                JObject jObj => jObj.ToObject<Dictionary<string, object>>(),
                _ => null
            };
        }

        // Message handling (called by MessageProcessor)
        public void HandleGlobalVariableSync(Dictionary<string, object> data)
        {
            // Mark that we've received initial sync
            // Note: If sync messages arrive exactly at timeout, this takes precedence over timeout mechanism
            if (!_hasReceivedInitialSync)
            {
                _hasReceivedInitialSync = true;
            }

            if (data.TryGetValue("variables", out var variablesObj))
            {
                object[] variables = ConvertToObjectArray(variablesObj);

                if (variables == null) return;

                foreach (var varObj in variables)
                {
                    Dictionary<string, object> variable = ConvertToDictionary(varObj);

                    if (variable != null)
                    {
                        var name = variable.TryGetValue("name", out var nameObj) ? (nameObj != null ? nameObj.ToString() : null) : null;
                        if (string.IsNullOrEmpty(name)) continue;

                        var nvValue = ExtractNVValue(variable);

                        var hasOld = _globalVariables.TryGetValue(name, out var existing);
                        // Skip if value is unchanged
                        if (hasOld && existing == nvValue)
                        {
                            continue;
                        }
                        _globalVariables[name] = nvValue;

                        // Trigger event only when changed
                        FireGlobalEvent(name, hasOld ? existing : default, nvValue);
                    }
                }
            }
        }

        public void HandleClientVariableSync(Dictionary<string, object> data)
        {
            // Mark that we've received initial sync
            // Note: If sync messages arrive exactly at timeout, this takes precedence over timeout mechanism
            if (!_hasReceivedInitialSync)
            {
                _hasReceivedInitialSync = true;
            }

            if (data.TryGetValue("clientVariables", out var clientVarsObj))
            {
                Dictionary<string, object> clientVariables = ConvertToDictionary(clientVarsObj);

                if (clientVariables == null)
                {
                    return;
                }

                foreach (var kvp in clientVariables)
                {
                    if (int.TryParse(kvp.Key, out var clientNo))
                    {
                        object[] variables = ConvertToObjectArray(kvp.Value);

                        if (variables == null) continue;

                        if (!_clientVariables.ContainsKey(clientNo))
                            _clientVariables[clientNo] = new Dictionary<string, NVValue>();

                        var clientVars = _clientVariables[clientNo];

                        foreach (var varObj in variables)
                        {
                            Dictionary<string, object> variable = ConvertToDictionary(varObj);

                            if (variable != null)
                            {
                                var name = variable.TryGetValue("name", out var nameObj) ? (nameObj != null ? nameObj.ToString() : null) : null;
                                if (string.IsNullOrEmpty(name)) continue;

                                var nvValue = ExtractNVValue(variable);

                                var hasOld = clientVars.TryGetValue(name, out var existing);
                                // Skip if value is unchanged
                                if (hasOld && existing == nvValue)
                                {
                                    continue;
                                }

                                clientVars[name] = nvValue;

                                // Trigger event only when changed
                                FireClientEvent(clientNo, name, hasOld ? existing : default, nvValue);
                            }
                        }
                    }
                }
            }
        }

        // Validation helpers
        private bool ValidateVariableName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MAX_VAR_NAME_LENGTH)
            {
                Debug.LogWarning($"Invalid variable name: must be 1-{MAX_VAR_NAME_LENGTH} characters");
                return false;
            }
            return true;
        }

        private bool ValidateStringValue(string value)
        {
            if (value == null)
            {
                Debug.LogWarning("Invalid variable value: must not be null");
                return false;
            }
            int byteLen = Encoding.UTF8.GetByteCount(value);
            if (byteLen > MAX_VAR_VALUE_LENGTH)
            {
                Debug.LogWarning($"Invalid variable value: {byteLen} bytes exceeds {MAX_VAR_VALUE_LENGTH} byte limit");
                return false;
            }
            return true;
        }

        private bool ValidateBytesValue(byte[] value)
        {
            if (value == null)
            {
                Debug.LogWarning("Invalid variable value: must not be null");
                return false;
            }
            if (value.Length > MAX_VAR_VALUE_LENGTH)
            {
                Debug.LogWarning($"Invalid variable value: {value.Length} bytes exceeds {MAX_VAR_VALUE_LENGTH} byte limit");
                return false;
            }
            return true;
        }

        // Extract NVValue from deserialized dictionary
        private static NVValue ExtractNVValue(Dictionary<string, object> variable)
        {
            byte valueType = BinarySerializer.VAR_TYPE_STRING;
            if (variable.TryGetValue("valueType", out var typeObj))
                valueType = Convert.ToByte(typeObj);

            if (valueType == BinarySerializer.VAR_TYPE_BYTES && variable.TryGetValue("valueBytes", out var bytesObj))
            {
                return new NVValue(BinarySerializer.VAR_TYPE_BYTES, (byte[])bytesObj);
            }

            // String type or fallback
            byte[] rawBytes;
            if (variable.TryGetValue("valueBytes", out var rawObj))
            {
                rawBytes = (byte[])rawObj;
            }
            else
            {
                var strValue = variable.TryGetValue("value", out var valueObj) ? (valueObj != null ? valueObj.ToString() : "") : "";
                rawBytes = Encoding.UTF8.GetBytes(strValue);
            }
            return new NVValue(BinarySerializer.VAR_TYPE_STRING, rawBytes);
        }

        // Fire appropriate event based on type tag
        private void FireGlobalEvent(string name, NVValue oldValue, NVValue newValue)
        {
            if (newValue.TypeTag == BinarySerializer.VAR_TYPE_BYTES)
            {
                OnGlobalBytesVariableChanged?.Invoke(name, oldValue.RawBytes, newValue.RawBytes);
            }
            else
            {
                OnGlobalVariableChanged?.Invoke(name, oldValue.AsString, newValue.AsString);
            }
        }

        private void FireClientEvent(int clientNo, string name, NVValue oldValue, NVValue newValue)
        {
            if (newValue.TypeTag == BinarySerializer.VAR_TYPE_BYTES)
            {
                OnClientBytesVariableChanged?.Invoke(clientNo, name, oldValue.RawBytes, newValue.RawBytes);
            }
            else
            {
                OnClientVariableChanged?.Invoke(clientNo, name, oldValue.AsString, newValue.AsString);
            }
        }

        // Get all global variables (for debugging/inspection)
        public Dictionary<string, string> GetAllGlobalVariables()
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in _globalVariables)
            {
                if (kvp.Value.TypeTag == BinarySerializer.VAR_TYPE_STRING)
                    result[kvp.Key] = kvp.Value.AsString;
            }
            return result;
        }

        // Get all client variables for a specific client (for debugging/inspection)
        public Dictionary<string, string> GetAllClientVariables(int clientNo)
        {
            if (_clientVariables.TryGetValue(clientNo, out var clientVars))
            {
                var result = new Dictionary<string, string>();
                foreach (var kvp in clientVars)
                {
                    if (kvp.Value.TypeTag == BinarySerializer.VAR_TYPE_STRING)
                        result[kvp.Key] = kvp.Value.AsString;
                }
                return result;
            }
            return new Dictionary<string, string>();
        }

        /// <summary>
        /// Process pending network variable updates.
        /// </summary>
        /// <param name="nowSeconds">Current time in seconds (Unix timestamp).</param>
        /// <param name="roomId">The room ID to send to.</param>
        /// <returns>True if all pending were flushed successfully, false if any backpressure occurred.</returns>
        public bool Tick(double nowSeconds, string roomId)
        {
            bool globalOk = FlushPendingGlobal(nowSeconds, roomId);
            bool clientOk = FlushPendingClient(nowSeconds, roomId);
            return globalOk && clientOk;
        }

        // Buffer growth handled by ReusableBufferWriter

        // Unified size estimation: 1 (msgType) + 2 (sender) + [2 (target)] + 1 + nameLen + 1 (typeTag) + 4 (uint32 valueLen) + valueLen + 8 (timestamp)
        private static int EstimateNVSetSize(string name, int valueBytesLength, bool hasTarget)
        {
            int nameLen = name != null ? Math.Min(Encoding.UTF8.GetByteCount(name), 64) : 0;
            int targetSize = hasTarget ? 2 : 0;
            return 1 + 2 + targetSize + 1 + nameLen + 1 + 4 + valueBytesLength + 8;
        }

        /// <summary>
        /// Flush pending global variable updates.
        /// </summary>
        /// <returns>True if all pending were flushed, false if any backpressure occurred.</returns>
        private bool FlushPendingGlobal(double nowSeconds, string roomId)
        {
            var toFlush = new List<(string name, NVValue value)>();
            bool allFlushed = true;

            foreach (var kvp in _dueGlobal)
            {
                if (kvp.Value <= nowSeconds && _pendingGlobal.TryGetValue(kvp.Key, out var value))
                {
                    toFlush.Add((kvp.Key, value));
                }
            }

            foreach (var (name, value) in toFlush)
            {
                // Skip if trailing value equals the last sent value
                if (_lastSentGlobal.TryGetValue(name, out var last) && last == value)
                {
                    _pendingGlobal.Remove(name);
                    _dueGlobal.Remove(name);
                    continue;
                }

                if (TrySendGlobalNow(name, value, roomId))
                {
                    _pendingGlobal.Remove(name);
                    _dueGlobal.Remove(name);

                    // Start a new cooldown window from the trailing edge
                    _nextAllowedGlobal[name] = nowSeconds + DEBOUNCE_INTERVAL;
                }
                else
                {
                    // Send failed - backpressure
                    allFlushed = false;
                }
                // If send fails, keep entries so a later tick can retry.
            }

            return allFlushed;
        }

        /// <summary>
        /// Flush pending client variable updates.
        /// </summary>
        /// <returns>True if all pending were flushed, false if any backpressure occurred.</returns>
        private bool FlushPendingClient(double nowSeconds, string roomId)
        {
            var toFlush = new List<((int targetClientNo, string name) key, NVValue value)>();
            bool allFlushed = true;

            foreach (var kvp in _dueClient)
            {
                if (kvp.Value <= nowSeconds && _pendingClient.TryGetValue(kvp.Key, out var value))
                {
                    toFlush.Add((kvp.Key, value));
                }
            }

            foreach (var (key, value) in toFlush)
            {
                // Dedupe against last actually sent
                if (_lastSentClient.TryGetValue(key, out var last) && last == value)
                {
                    _pendingClient.Remove(key);
                    _dueClient.Remove(key);
                    continue;
                }

                if (TrySendClientNow(key.Item1, key.Item2, value, roomId))
                {
                    _pendingClient.Remove(key);
                    _dueClient.Remove(key);

                    // Start a new cooldown window from the trailing edge
                    _nextAllowedClient[key] = nowSeconds + DEBOUNCE_INTERVAL;
                }
                else
                {
                    // Send failed - backpressure
                    allFlushed = false;
                }
                // If send fails, keep entries to retry on subsequent ticks.
            }

            return allFlushed;
        }

        /// <summary>
        /// Dispose pooled buffer resources used for serialization.
        /// </summary>
        public void Dispose()
        {
            _buf.Dispose();
        }
    }
}
