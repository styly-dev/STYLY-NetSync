// RPCManager.cs - Handles RPC (Remote Procedure Call) system
using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NetMQ;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Events;

namespace Styly.NetSync
{
    internal class RPCManager
    {
        private readonly ConnectionManager _connectionManager;
        private readonly string _deviceId;
        private readonly NetSyncManager _netSyncManager;
        private readonly ConcurrentQueue<(int senderClientNo, string fn, string[] args)> _rpcQueue = new();

        // Reusable serialization resources to reduce GC
        private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
        private byte[] _buffer;
        private int _bufferCapacity;
        private MemoryStream _memoryStream;
        private BinaryWriter _writer;
        private NetMQMessage _reusableMessage;
        private const int INITIAL_BUFFER_CAPACITY = 512;

        public UnityEvent<int, string, string[]> OnRPCReceived { get; } = new();

        // ===== Client-side RPC rate limiter (single cap) =====
        private readonly object _rlLock = new();
        private readonly Queue<double> _hits = new();       // enqueue send timestamps (seconds)
        private double _windowSeconds = 1.0;                 // sliding window size
        private int _rpcLimit = 30;                          // max RPCs per window (<=0 disables)
        private double _lastWarnAt = -999.0;                 // last warning time
        private double _warnCooldown = 0.5;                  // min seconds between warnings
        // Outgoing RPC queue for pre-ready state
        private readonly ConcurrentQueue<(string fn, string[] args, double enqueuedAt)> _pendingOut = new ConcurrentQueue<(string fn, string[] args, double enqueuedAt)>();
        [SerializeField] private int _maxPendingRpc = 100;     // drop oldest beyond this
        [SerializeField] private double _rpcTtlSeconds = 5.0;  // drop when too old
        [SerializeField] private int _maxFlushPerFrame = 10;   // to avoid burst on first ready frame

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
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
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

        private bool TrySendNow(string roomId, string functionName, string[] args)
        {
            if (_connectionManager.DealerSocket == null) { return false; }

            // === Rate limit preflight (single global cap) ===
            if (!TryConsumeQuota(out var retryAfter, out var count))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                if (now - _lastWarnAt >= _warnCooldown)
                {
                    Debug.LogWarning($"[NetSync/RPC] Rate limited: dropped '{functionName}' (count={count}/{_rpcLimit} in {_windowSeconds:0.##}s). Retry in ~{retryAfter:0.##}s.");
                    _lastWarnAt = now;
                }
                return false;
            }

            var rpcMsg = new RPCMessage
            {
                functionName = functionName,
                argumentsJson = JsonConvert.SerializeObject(args),
                senderClientNo = _netSyncManager.ClientNo
            };
            // Estimate and ensure capacity
            var required = EstimateRpcSize(rpcMsg);
            EnsureBufferCapacity(required);

            // Serialize into pooled stream
            _memoryStream.Position = 0;
            BinarySerializer.SerializeRPCMessageInto(_writer, rpcMsg);
            _writer.Flush();

            var length = (int)_memoryStream.Position;

            // Build and send message reusing NetMQMessage instance
            _reusableMessage.Clear();
            _reusableMessage.Append(roomId);
            var payload = new byte[length];
            Buffer.BlockCopy(_buffer, 0, payload, 0, length);
            _reusableMessage.Append(payload);

            try
            {
                return _connectionManager.DealerSocket.TrySendMultipartMessage(_reusableMessage);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetSync/RPC] Failed to send RPC '{functionName}': {ex.Message}");
                return false;
            }
        }

        public RPCManager(ConnectionManager connectionManager, string deviceId, NetSyncManager netSyncManager)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            _netSyncManager = netSyncManager;
            _bufferCapacity = INITIAL_BUFFER_CAPACITY;
            _buffer = _bufferPool.Rent(_bufferCapacity);
            _memoryStream = new MemoryStream(_buffer, 0, _buffer.Length, true, true);
            _writer = new BinaryWriter(_memoryStream);
            _reusableMessage = new NetMQMessage();
        }

        public void Send(string roomId, string functionName, string[] args)
        {
            if (!_netSyncManager.IsReady)
            {
                // Queue for later when ready
                _pendingOut.Enqueue((functionName, args, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
                
                // Check if queue is too large
                while (_pendingOut.Count > _maxPendingRpc)
                {
                    if (_pendingOut.TryDequeue(out var dropped))
                    {
                        Debug.LogWarning($"[NetSync/RPC] Pending queue overflow: dropped '{dropped.fn}' (queue size exceeded {_maxPendingRpc})");
                    }
                }
                return;
            }

            TrySendNow(roomId, functionName, args);
        }

        public void FlushPendingIfReady(string roomId)
        {
            if (!_netSyncManager.IsReady) return;

            int sentThisFrame = 0;
            while (sentThisFrame < _maxFlushPerFrame && _pendingOut.TryPeek(out var item))
            {
                var (fn, args, enqAt) = item;

                // TTL check
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - enqAt > _rpcTtlSeconds)
                {
                    _pendingOut.TryDequeue(out _); // drop expired
                    Debug.LogWarning($"[NetSync/RPC] Dropped expired pending RPC '{fn}' (TTL {_rpcTtlSeconds}s exceeded)");
                    continue;
                }

                // Try to send with rate limit
                if (TrySendNow(roomId, fn, args))
                {
                    _pendingOut.TryDequeue(out _);
                    sentThisFrame++;
                    continue;
                }

                // If rate-limited, stop draining this frame (don't drop)
                break;
            }
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

        private void EnsureBufferCapacity(int required)
        {
            if (required <= _bufferCapacity) return;
            var newCapacity = _bufferCapacity * 2;
            if (newCapacity < required) newCapacity = required;

            var newBuffer = _bufferPool.Rent(newCapacity);
            _writer?.Dispose();
            _memoryStream?.Dispose();
            _bufferPool.Return(_buffer);
            _buffer = newBuffer;
            _bufferCapacity = newCapacity;
            _memoryStream = new MemoryStream(_buffer, 0, _buffer.Length, true, true);
            _writer = new BinaryWriter(_memoryStream);
        }

        private static int EstimateRpcSize(RPCMessage msg)
        {
            var nameLen = msg != null && msg.functionName != null ? Encoding.UTF8.GetByteCount(msg.functionName) : 0;
            if (nameLen > 255) nameLen = 255; // capped by serializer
            var argsLen = msg != null && msg.argumentsJson != null ? Encoding.UTF8.GetByteCount(msg.argumentsJson) : 0;
            return 1 + 2 + 1 + nameLen + 2 + argsLen;
        }
    }
}
