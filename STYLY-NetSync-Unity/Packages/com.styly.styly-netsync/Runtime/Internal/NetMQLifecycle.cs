// NetMQLifecycle.cs
// Utility helpers for NetMQ initialization and cleanup to be used by runtime and tests.
// All comments and documentation in English per project guidelines.
using System;
using System.Threading;
using System.Threading.Tasks;
using NetMQ;

namespace Styly.NetSync
{
    internal static class NetMQLifecycle
    {
        /// <summary>
        /// Ensure NetMQ AsyncIO is initialized (safe to call multiple times).
        /// </summary>
        public static void EnsureInitialized()
        {
            try { AsyncIO.ForceDotNet.Force(); } catch { }
        }

        /// <summary>
        /// Attempt to cleanup NetMQ background threads. Does not wait for pending sends.
        /// Call this at the end of tests (e.g., [TearDown]) and on application shutdown.
        /// </summary>
        public static void Cleanup(bool wait = false, int timeoutMs = 500)
        {
            try
            {
                if (!wait)
                {
                    NetMQConfig.Cleanup(false);
                    return;
                }

                var cts = new CancellationTokenSource();
                var task = Task.Run(() => NetMQConfig.Cleanup(false), cts.Token);
                if (!task.Wait(timeoutMs))
                {
                    cts.Cancel();
                }
            }
            catch
            {
                // Swallow exceptions in cleanup; shutdown should proceed
            }
        }
    }
}

