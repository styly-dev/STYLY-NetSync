// NetSyncManager.cs
using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;

namespace Styly.NetSync
{
    public class NetSyncManager : MonoBehaviour
    {
        #region === Inspector ===
        [Header("Network Info")]
        [SerializeField, ReadOnly] private string _deviceId;
        [SerializeField, ReadOnly] private int _clientNo = 0;
        
        [Header("Connection Settings")]
        [SerializeField, Tooltip("Server IP address or hostname (e.g. 192.168.1.100, localhost). Leave empty to auto-discover server on local network")] private string _serverAddress = "localhost";
        private int _dealerPort = 5555;
        private int _subPort = 5556;
        [SerializeField] private string _groupId = "default_group";

        // [Header("Discovery Settings")]
        private bool _enableDiscovery = true;
        private int _beaconPort = 9999;
        private float _discoveryTimeout = 5f;

        [Header("Player Settings")]
        [SerializeField] private GameObject _localPlayerPrefab;
        [SerializeField] private GameObject _remotePlayerPrefab;

        // [Header("Transform Sync Settings"), Range(1, 120)]
        private float _sendRate = 10f;

        // [Header("Debug Settings")]
        private bool _enableDebugLogs = true;
        private bool _logTransformDetail = false;
        private bool _logNetworkTraffic = false;

        [Header("Events")]
        public UnityEvent<int> OnClientConnected;
        public UnityEvent<int> OnClientDisconnected;
        public UnityEvent<int, string, string[]> OnRPCReceived;
        public UnityEvent<string, string, string> OnGlobalVariableChanged;
        public UnityEvent<int, string, string, string> OnClientVariableChanged;
        #endregion ------------------------------------------------------------------------

        #region === Singleton & Public API ===
        private static NetSyncManager _instance;
        public static NetSyncManager Instance => _instance;

        // Public RPC methods for external access
        public void RpcBroadcast(string functionName, string[] args)
        {
            _rpcManager?.SendBroadcast(_groupId, functionName, args);
        }

        public void RpcBroadcast(string groupId, string functionName, string[] args)
        {
            _rpcManager?.SendBroadcast(groupId, functionName, args);
        }

        public void RpcServer(string functionName, string[] args)
        {
            _rpcManager?.SendToServer(_groupId, functionName, args);
        }

        public void RpcServer(string groupId, string functionName, string[] args)
        {
            _rpcManager?.SendToServer(groupId, functionName, args);
        }

        public void RpcClient(int targetClientNo, string functionName, string[] args)
        {
            _rpcManager?.SendToClient(_groupId, targetClientNo, functionName, args);
        }

        // Network Variables API
        public bool SetGlobalVariable(string name, string value)
        {
            return _networkVariableManager?.SetGlobalVariable(name, value, _groupId) ?? false;
        }

        public string GetGlobalVariable(string name)
        {
            return _networkVariableManager?.GetGlobalVariable(name);
        }

        public bool SetClientVariable(string name, string value)
        {
            return _networkVariableManager?.SetClientVariable(_clientNo, name, value, _groupId) ?? false;
        }

        public bool SetClientVariable(int targetClientNo, string name, string value)
        {
            return _networkVariableManager?.SetClientVariable(targetClientNo, name, value, _groupId) ?? false;
        }

        public string GetClientVariable(int clientNo, string name)
        {
            return _networkVariableManager?.GetClientVariable(clientNo, name);
        }
        
        /// <summary>
        /// Gets all variables for a specific client (for debugging)
        /// </summary>
        public Dictionary<string, string> GetAllClientVariables(int clientNo)
        {
            return _networkVariableManager?.GetAllClientVariables(clientNo) ?? new Dictionary<string, string>();
        }

        /// <summary>
        /// Gets all global variables (for debugging/inspector)
        /// </summary>
        public Dictionary<string, string> GetAllGlobalVariables()
        {
            return _networkVariableManager?.GetAllGlobalVariables() ?? new Dictionary<string, string>();
        }
        
        /// <summary>
        /// Checks if a client is in stealth mode (no visible avatar)
        /// </summary>
        /// <param name="clientNo">The client number to check</param>
        /// <returns>True if the client is in stealth mode, false otherwise</returns>
        public bool IsClientStealthMode(int clientNo)
        {
            return _messageProcessor?.IsClientStealthMode(clientNo) ?? false;
        }
        
        #endregion ------------------------------------------------------------------------

        #region === Runtime Fields ===
        private static bool _netMqInit;

        // Managers
        private ConnectionManager _connectionManager;
        private PlayerManager _playerManager;
        private RPCManager _rpcManager;
        private TransformSyncManager _transformSyncManager;
        private MessageProcessor _messageProcessor;
        private ServerDiscoveryManager _discoveryManager;
        private NetworkVariableManager _networkVariableManager;

        // State
        private bool _isDiscovering;
        private bool _isStealthMode;
        private string _discoveredServer;
        private int _discoveredDealerPort;
        private int _discoveredSubPort;
        private float _discoveryStartTime;
        private const float ReconnectDelay = 10f;
        private float _reconnectAt;
        #endregion ------------------------------------------------------------------------

        #region === Public Properties ===
        public string DeviceId => _deviceId;
        public int ClientNo => _clientNo;
        public string GroupId => _groupId;
        public ConnectionManager ConnectionManager => _connectionManager;
        public PlayerManager PlayerManager => _playerManager;
        public RPCManager RPCManager => _rpcManager;
        public TransformSyncManager TransformSyncManager => _transformSyncManager;
        public MessageProcessor MessageProcessor => _messageProcessor;
        
        public GameObject GetRemotePlayerPrefab() => _remotePlayerPrefab;
        #endregion ------------------------------------------------------------------------

        #region === Unity Callbacks ===
        private void Awake()
        {
            Application.runInBackground = true;

            if (!_netMqInit) { AsyncIO.ForceDotNet.Force(); _netMqInit = true; }

            // Generate device ID - use Android device ID if available, otherwise generate GUID
            _deviceId = GenerateDeviceId();
            _instance = this;

            // Detect stealth mode based on local player prefab
            _isStealthMode = (_localPlayerPrefab == null);
            
            InitializeManagers();
            DebugLog($"Device ID: {_deviceId}");
            if (_isStealthMode)
            {
                DebugLog("Stealth mode enabled (no local avatar prefab)");
            }
        }

        private void OnEnable()
        {
            _playerManager.InitializeLocalPlayer(_localPlayerPrefab, _deviceId, this);
            StartNetworking();
        }

        private void OnDisable()
        {
            StopNetworking();
            _playerManager.CleanupRemotePlayers();
            _instance = null;
        }

        private void OnApplicationQuit() => StopNetworking();

        private void OnApplicationPause(bool paused)
        {
            if (paused) 
            { 
                DebugLog("Application paused - stopping network");
                StopNetworking(); 
            }
            else
            {
                DebugLog("Application resumed - restarting network");
                StartNetworking();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                DebugLog("Application lost focus");
            }
            else
            {
                DebugLog("Application gained focus");
                if (!_connectionManager.IsConnected && !_isDiscovering)
                {
                    StartNetworking();
                }
            }
        }

        private void Update()
        {
            HandleDiscovery();
            HandleReconnection();
            ProcessMessages();
            SendTransformUpdates();
            LogStatistics();
        }
        #endregion ------------------------------------------------------------------------

        #region === Initialization ===
        private string GenerateDeviceId()
        {
#if UNITY_EDITOR
            // Always use GUID in Unity Editor for development
            var guid = Guid.NewGuid().ToString();
            DebugLog($"Unity Editor: using generated GUID: {guid}");
            return guid;
#else
            // Try to get Unity's device unique identifier on actual devices
            string deviceId = SystemInfo.deviceUniqueIdentifier;
            
            // Check if the device ID is valid
            if (!string.IsNullOrEmpty(deviceId) && deviceId != SystemInfo.unsupportedIdentifier)
            {
                DebugLog($"Using SystemInfo.deviceUniqueIdentifier: {deviceId}");
                return deviceId;
            }
            
            // Fallback to GUID if SystemInfo.deviceUniqueIdentifier is not available
            var fallbackGuid = Guid.NewGuid().ToString();
            DebugLog($"SystemInfo.deviceUniqueIdentifier not available, using generated GUID: {fallbackGuid}");
            return fallbackGuid;
#endif
        }
        
        private void InitializeManagers()
        {
            // Initialize managers
            _messageProcessor = new MessageProcessor(_logNetworkTraffic);
            _messageProcessor.SetLocalDeviceId(_deviceId);
            _messageProcessor.OnLocalClientNoAssigned += OnLocalClientNoAssigned;
            _connectionManager = new ConnectionManager(this, _messageProcessor, _enableDebugLogs, _logNetworkTraffic);
            _playerManager = new PlayerManager(_enableDebugLogs);
            _rpcManager = new RPCManager(_connectionManager, _deviceId, this);
            _transformSyncManager = new TransformSyncManager(_connectionManager, _deviceId, _sendRate);
            _discoveryManager = new ServerDiscoveryManager(_enableDebugLogs);
            _networkVariableManager = new NetworkVariableManager(_connectionManager, _deviceId, this);

            // Setup events
            _connectionManager.OnConnectionError += HandleConnectionError;
            _connectionManager.OnConnectionEstablished += OnConnectionEstablished;
            _playerManager.OnClientDisconnected.AddListener(OnRemotePlayerDisconnected);
            _rpcManager.OnRPCReceived.AddListener(OnRPCReceivedHandler);
            
            // Setup network variable events
            if (_networkVariableManager != null)
            {
                _networkVariableManager.OnGlobalVariableChanged += OnGlobalVariableChangedHandler;
                _networkVariableManager.OnClientVariableChanged += OnClientVariableChangedHandler;
            }
            
            // Setup discovery event
            if (_discoveryManager != null)
            {
                _discoveryManager.OnServerDiscovered += OnServerDiscovered;
            }

            // RPC events are now handled through instance methods only
        }

        private void OnConnectionEstablished()
        {
            DebugLog("Connection established successfully");
        }

        private void OnRemotePlayerDisconnected(int clientNo)
        {
            OnClientDisconnected?.Invoke(clientNo);
        }

        private void OnRPCReceivedHandler(int senderClientNo, string functionName, string[] args)
        {
            OnRPCReceived?.Invoke(senderClientNo, functionName, args);
        }
        
        private void OnGlobalVariableChangedHandler(string name, string oldValue, string newValue)
        {
            OnGlobalVariableChanged?.Invoke(name, oldValue, newValue);
        }
        
        private void OnClientVariableChangedHandler(int clientNo, string name, string oldValue, string newValue)
        {
            OnClientVariableChanged?.Invoke(clientNo, name, oldValue, newValue);
        }
        
        private void OnLocalClientNoAssigned(int clientNo)
        {
            _clientNo = clientNo;
            DebugLog($"Local client number assigned: {clientNo}");
        }
        
        private void OnServerDiscovered(string serverAddress, int dealerPort, int subPort)
        {
            _discoveredServer = serverAddress;
            _discoveredDealerPort = dealerPort;
            _discoveredSubPort = subPort;
            
            // Update the server address for future connections
            // Remove tcp:// prefix (discovery always returns with tcp://)
            _serverAddress = serverAddress.Substring(6);
            _dealerPort = dealerPort;
            _subPort = subPort;
            
            DebugLog($"Server discovered: {serverAddress} (dealer:{dealerPort}, sub:{subPort})");
        }
        #endregion ------------------------------------------------------------------------

        #region === Networking ===
        private void StartNetworking()
        {
            // If server address is empty and discovery is enabled, start discovery
            if (string.IsNullOrEmpty(_serverAddress) && _enableDiscovery && !_isDiscovering)
            {
                StartDiscovery();
                return;
            }

            // Add tcp:// prefix
            string fullAddress = $"tcp://{_serverAddress}";
            _connectionManager.Connect(fullAddress, _dealerPort, _subPort, _groupId);
        }

        private void StopNetworking()
        {
            _connectionManager.Disconnect();
            StopDiscovery();
        }

        private void StartDiscovery()
        {
            if (_discoveryManager == null) { return; }
            _connectionManager.StartDiscovery(_discoveryManager, _groupId);
            _isDiscovering = true;
            _discoveryStartTime = Time.time;
        }

        private void StopDiscovery()
        {
            if (_discoveryManager == null) { return; }
            _discoveryManager.StopDiscovery();
            _isDiscovering = false;
        }
        #endregion ------------------------------------------------------------------------

        #region === Update Logic ===
        private void HandleDiscovery()
        {
            // Handle discovery timeout
            if (_isDiscovering && Time.time - _discoveryStartTime > _discoveryTimeout)
            {
                DebugLog("Discovery timeout - falling back to localhost");
                StopDiscovery();
                _serverAddress = "localhost";
                StartNetworking();
            }

            // Process discovered server
            if (!string.IsNullOrEmpty(_discoveredServer) && _isDiscovering)
            {
                _isDiscovering = false;
                StopDiscovery();
                _connectionManager.ProcessDiscoveredServer(_discoveredServer, _discoveredDealerPort, _discoveredSubPort);
                _discoveredServer = null;
            }
        }

        private void HandleReconnection()
        {
            if (_connectionManager.IsConnectionError && Time.time >= _reconnectAt)
            {
                DebugLog($"Attempting reconnection to {_serverAddress}…");
                StartNetworking();
            }
        }

        private void ProcessMessages()
        {
            _messageProcessor.ProcessMessageQueue(_playerManager, _rpcManager, _deviceId, this, _networkVariableManager);
            _rpcManager.ProcessRPCQueue();
        }

        private void SendTransformUpdates()
        {
            if (!_connectionManager.IsConnectionError && _transformSyncManager.ShouldSendTransform(Time.time))
            {
                if (_isStealthMode)
                {
                    // Send stealth handshake instead of regular transform
                    if (!_transformSyncManager.SendStealthHandshake(_groupId))
                    {
                        HandleConnectionError("Send failed – disconnected?");
                    }
                }
                else
                {
                    // Normal transform sending
                    var localPlayer = _playerManager.LocalPlayerAvatar;
                    if (!_transformSyncManager.SendLocalTransform(localPlayer, _groupId))
                    {
                        HandleConnectionError("Send failed – disconnected?");
                    }
                }
                _transformSyncManager.UpdateLastSendTime(Time.time);
            }
        }

        private void LogStatistics()
        {
            // Statistics logging is currently disabled
            // Uncomment to enable periodic statistics logging
            /*
            if (_enableDebugLogs && Time.time - _lastLogTime >= 5f)
            {
                var sent = _transformSyncManager.MessagesSent;
                var recv = _messageProcessor.MessagesReceived;
                var peers = _playerManager.ConnectedPeers.Count;
                DebugLog($"Stats — Sent:{sent} Recv:{recv} Peers:{peers}");
                _lastLogTime = Time.time;
            }
            */
        }
        #endregion ------------------------------------------------------------------------

        #region === Utility ===
        private void HandleConnectionError(string reason)
        {
            if (_connectionManager.IsConnectionError) { return; }
            Debug.LogError($"[NetSyncManager] {reason}");
            _reconnectAt = Time.time + ReconnectDelay;
            StopNetworking();
        }

        private void DebugLog(string msg)
        {
            if (_enableDebugLogs) { Debug.Log($"[NetSyncManager] {msg}"); }
        }
        #endregion ------------------------------------------------------------------------
    }
}