// RPCManager.cs - Handles RPC (Remote Procedure Call) system
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NetMQ;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Events;

namespace Styly.NetSync
{
    public class RPCManager
    {
        private readonly ConnectionManager _connectionManager;
        private readonly string _deviceId;
        private readonly NetSyncManager _netSyncManager;
        private readonly ConcurrentQueue<(int senderClientNo, string fn, string[] args)> _rpcQueue = new();

        public UnityEvent<int, string, string[]> OnRPCReceived { get; } = new();

        // ===== Client-side RPC rate limiter (single cap) =====
        private readonly object _rlLock = new();
        private readonly Queue<double> _hits = new();       // enqueue send timestamps (seconds)
        private double _windowSeconds = 1.0;                 // sliding window size
        private int _rpcLimit = 30;                          // max RPCs per window (<=0 disables)
        private double _lastWarnAt = -999.0;                 // last warning time
        private double _warnCooldown = 0.5;                  // min seconds between warnings

        /// <summary>Override defaults at runtime (rpcLimit<=0 disables RL).</summary>
        public void ConfigureRpcLimit(int rpcLimit, double windowSeconds, double warnCooldown = 0.5)
        {
            lock (_rlLock)
            {
                _rpcLimit = Math.Max(0, rpcLimit);
                _windowSeconds = Math.Max(0.01, windowSeconds);
                _warnCooldown = Math.Max(0.0, warnCooldown);
            }
        }

        private void PurgeOld(Queue<double> q, double now)
        {
            while (q.Count > 0 && now - q.Peek() > _windowSeconds)
            {
                q.Dequeue();
            }
        }

        /// <summary>Consume one slot if available. Returns true if allowed to send.</summary>
        private bool TryConsumeQuota(out double retryAfterSec, out int currentCount)
        {
            var now = Time.realtimeSinceStartupAsDouble;
            lock (_rlLock)
            {
                // Disabled?
                if (_rpcLimit <= 0)
                {
                    retryAfterSec = 0;
                    currentCount = 0;
                    return true;
                }

                // Maintain sliding window
                PurgeOld(_hits, now);
                if (_hits.Count >= _rpcLimit)
                {
                    retryAfterSec = Math.Max(0.0, _windowSeconds - (now - _hits.Peek()));
                    currentCount = _hits.Count;
                    return false;
                }

                _hits.Enqueue(now);
                retryAfterSec = 0;
                currentCount = _hits.Count;
                return true;
            }
        }

        public RPCManager(ConnectionManager connectionManager, string deviceId, NetSyncManager netSyncManager)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            _netSyncManager = netSyncManager;
        }

        public void Send(string roomId, string functionName, string[] args)
        {
            if (_connectionManager.DealerSocket == null) { return; }

            // === Rate limit preflight (single global cap) ===
            if (!TryConsumeQuota(out var retryAfter, out var count))
            {
                var now = Time.realtimeSinceStartupAsDouble;
                if (now - _lastWarnAt >= _warnCooldown)
                {
                    Debug.LogWarning($"[NetSync/RPC] Rate limited: dropped '{functionName}' (count={count}/{_rpcLimit} in {_windowSeconds:0.##}s). Retry in ~{retryAfter:0.##}s.");
                    _lastWarnAt = now;
                }
                return;
            }

            var rpcMsg = new RPCMessage
            {
                functionName = functionName,
                argumentsJson = JsonConvert.SerializeObject(args),
                senderClientNo = _netSyncManager.ClientNo
            };
            var binary = BinarySerializer.SerializeRPCMessage(rpcMsg);

            var msg = new NetMQMessage();
            msg.Append(roomId);
            msg.Append(binary);

            _connectionManager.DealerSocket.TrySendMultipartMessage(msg);
        }


        public void ProcessRPCQueue()
        {
            while (_rpcQueue.TryDequeue(out var rpc))
            {
                if (OnRPCReceived != null)
                {
                    OnRPCReceived.Invoke(rpc.senderClientNo, rpc.fn, rpc.args);
                }
            }
        }

        public void EnqueueRPC(int senderClientNo, string functionName, string[] args)
        {
            _rpcQueue.Enqueue((senderClientNo, functionName, args));
        }
    }
}