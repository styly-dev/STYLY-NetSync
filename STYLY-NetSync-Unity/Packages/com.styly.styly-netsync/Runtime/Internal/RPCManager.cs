// RPCManager.cs - Handles RPC (Remote Procedure Call) system
using System.Collections.Concurrent;
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

        public RPCManager(ConnectionManager connectionManager, string deviceId, NetSyncManager netSyncManager)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            _netSyncManager = netSyncManager;
        }

        public void SendBroadcast(string roomId, string functionName, string[] args)
        {
            if (_connectionManager.DealerSocket == null) { return; }

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

        public void SendToServer(string roomId, string functionName, string[] args)
        {
            if (_connectionManager.DealerSocket == null) { return; }

            var rpcMsg = new RPCMessage
            {
                functionName = functionName,
                argumentsJson = JsonConvert.SerializeObject(args),
                senderClientNo = _netSyncManager.ClientNo
            };
            var binary = BinarySerializer.SerializeRPCRequest(rpcMsg);

            var msg = new NetMQMessage();
            msg.Append(roomId);
            msg.Append(binary);

            _connectionManager.DealerSocket.TrySendMultipartMessage(msg);
        }

        public void SendToClient(string roomId, int targetClientNo, string functionName, string[] args)
        {
            if (_connectionManager.DealerSocket == null) { return; }

            var rpcMsg = new RPCClientMessage
            {
                functionName = functionName,
                argumentsJson = JsonConvert.SerializeObject(args),
                senderClientNo = _netSyncManager.ClientNo,
                targetClientNo = targetClientNo
            };
            var binary = BinarySerializer.SerializeRPCClientMessage(rpcMsg);

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