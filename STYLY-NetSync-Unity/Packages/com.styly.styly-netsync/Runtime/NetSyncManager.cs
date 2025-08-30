// NetSyncManager.cs
using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;

namespace Styly.NetSync
{
    [DefaultExecutionOrder(-1000)]
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
        [SerializeField] private string _roomId = "default_room";

        // [Header("Discovery Settings")]
        private bool _enableDiscovery = true;
        private int _beaconPort = 9999;
        private float _discoveryTimeout = 5f;

        [Header("Avatar Settings")]
        [SerializeField] private GameObject _localAvatarPrefab;
        [SerializeField] private GameObject _remoteAvatarPrefab;

        // [Header("Transform Sync Settings"), Range(1, 120)]
        private float _sendRate = 10f;

        // [Header("Debug Settings")]
        private bool _enableDebugLogs = true;
        private bool _logTransformDetail = false;
        private bool _logNetworkTraffic = false;

        [Header("Events")]
        public UnityEvent<int> OnAvatarConnected;
        public UnityEvent<int> OnClientDisconnected;
        public UnityEvent<int, string, string[]> OnRPCReceived;
        public UnityEvent<string, string, string> OnGlobalVariableChanged;
        public UnityEvent<int, string, string, string> OnClientVariableChanged;
        public UnityEvent OnReady;
        #endregion ------------------------------------------------------------------------

        #region === Singleton & Public API ===
        private static NetSyncManager _instance;
        public static NetSyncManager Instance => _instance;

        /// <summary>
        /// Get the version of STYLY NetSync.
        /// </summary>
        /// <returns></returns>
        public string GetVersion()
        {
            return Util.GetVersion();
        }

        // Public RPC methods for external access
        public void Rpc(string functionName, string[] args)
        {
            if (_rpcManager != null)
            {
                _rpcManager.Send(_roomId, functionName, args);
            }
        }

        public void Rpc(string roomId, string functionName, string[] args)
        {
            if (_rpcManager != null)
            {
                _rpcManager.Send(roomId, functionName, args);
            }
        }

        /// <summary>
        /// Configure the RPC rate limit. Set rpcLimit to 0 or less to disable rate limiting.
        /// </summary>
        /// <param name="rpcLimit">Maximum number of RPCs allowed per window (0 or less disables)</param>
        /// <param name="windowSeconds">Time window in seconds</param>
        /// <param name="warnCooldown">Minimum seconds between warning messages</param>
        public void ConfigureRpcLimit(int rpcLimit, double windowSeconds = 1.0, double warnCooldown = 0.5)
        {
            if (_rpcManager != null)
            {
                _rpcManager.ConfigureRpcLimit(rpcLimit, windowSeconds, warnCooldown);
            }
        }


        // Network Variables API
        public bool SetGlobalVariable(string name, string value)
        {
            return _networkVariableManager != null ? _networkVariableManager.SetGlobalVariable(name, value, _roomId) : false;
        }

        public string GetGlobalVariable(string name, string defaultValue = null)
        {
            return _networkVariableManager != null ? _networkVariableManager.GetGlobalVariable(name, defaultValue) : defaultValue;
        }

        public bool SetClientVariable(string name, string value)
        {
            if (_clientNo <= 0)
            {
                _pendingSelfClientNV.Add((name, value)); // late-binding until handshake
                return true; // accepted
            }
            return _networkVariableManager != null ? _networkVariableManager.SetClientVariable(_clientNo, name, value, _roomId) : false;
        }

        public bool SetClientVariable(int targetClientNo, string name, string value)
        {
            return _networkVariableManager != null ? _networkVariableManager.SetClientVariable(targetClientNo, name, value, _roomId) : false;
        }

        public string GetClientVariable(int clientNo, string name, string defaultValue = null)
        {
            return _networkVariableManager != null ? _networkVariableManager.GetClientVariable(clientNo, name, defaultValue) : defaultValue;
        }

        /// <summary>
        /// Gets all variables for a specific client (for debugging)
        /// </summary>
        public Dictionary<string, string> GetAllClientVariables(int clientNo)
        {
            return _networkVariableManager != null ? _networkVariableManager.GetAllClientVariables(clientNo) : new Dictionary<string, string>();
        }

        /// <summary>
        /// Gets all global variables (for debugging/inspector)
        /// </summary>
        public Dictionary<string, string> GetAllGlobalVariables()
        {
            return _networkVariableManager != null ? _networkVariableManager.GetAllGlobalVariables() : new Dictionary<string, string>();
        }

        /// <summary>
        /// Checks if a client is in stealth mode (no visible avatar)
        /// </summary>
        /// <param name="clientNo">The client number to check</param>
        /// <returns>True if the client is in stealth mode, false otherwise</returns>
        public bool IsClientStealthMode(int clientNo)
        {
            return _messageProcessor != null ? _messageProcessor.IsClientStealthMode(clientNo) : false;
        }

        #endregion ------------------------------------------------------------------------

        #region === Runtime Fields ===
        private static bool _netMqInit;

        // Managers
        private ConnectionManager _connectionManager;
        private AvatarManager _avatarManager;
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
        private readonly List<(string name, string value)> _pendingSelfClientNV = new List<(string name, string value)>();
        private bool _hasInvokedReady = false;
        private bool _shouldCheckReady = false;
        // Battery monitoring fields
        private float _batteryUpdateInterval = 60.0f; // Update every 60 seconds
        private float _lastBatteryUpdate = 0.0f; // Last time we updated battery level
        #endregion ------------------------------------------------------------------------

        #region === Public Properties ===
        public string DeviceId => _deviceId;
        public int ClientNo => _clientNo;
        public string RoomId => _roomId;
        internal ConnectionManager ConnectionManager => _connectionManager;
        internal AvatarManager AvatarManager => _avatarManager;
        internal RPCManager RPCManager => _rpcManager;
        internal TransformSyncManager TransformSyncManager => _transformSyncManager;
        internal MessageProcessor MessageProcessor => _messageProcessor;
        public bool HasServerConnection => _connectionManager?.IsConnected == true && !_connectionManager.IsConnectionError;
        public bool HasHandshake => _clientNo > 0;
        public bool HasNetworkVariablesSync => _networkVariableManager?.HasReceivedInitialSync == true;
        public bool IsReady => HasServerConnection && HasHandshake && HasNetworkVariablesSync;

        public GameObject GetLocalAvatarPrefab() => _localAvatarPrefab;
        public GameObject GetRemoteAvatarPrefab() => _remoteAvatarPrefab;
        #endregion ------------------------------------------------------------------------

        #region === Unity Callbacks ===
        private void Awake()
        {
            // Log package version
            DebugLog($"STYLY-NetSync Version: {GetVersion()}");

            Application.runInBackground = true;

            if (!_netMqInit) { AsyncIO.ForceDotNet.Force(); _netMqInit = true; }

            // Generate device ID - use Android device ID if available, otherwise generate GUID
            _deviceId = GenerateDeviceId();
            _instance = this;

            // Detect stealth mode based on local avatar prefab
            _isStealthMode = (_localAvatarPrefab == null);

            InitializeManagers();
            DebugLog($"Device ID: {_deviceId}");
            if (_isStealthMode)
            {
                DebugLog("Stealth mode enabled (no local avatar prefab)");
            }
        }

        private void OnEnable()
        {
            _avatarManager.InitializeLocalAvatar(_localAvatarPrefab, _deviceId, this);
            StartNetworking();
        }

        private void OnDisable()
        {
            StopNetworking();
            _avatarManager.CleanupRemoteAvatars();
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
            // Check ready state on main thread
            if (_shouldCheckReady)
            {
                _shouldCheckReady = false;
                CheckAndFireReady();
            }

            HandleDiscovery();
            HandleReconnection();
            ProcessMessages();
            SendTransformUpdates();

            // Process Network Variables debounced updates
            _networkVariableManager?.Tick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, _roomId);

            // Flush pending RPCs
            _rpcManager?.FlushPendingIfReady(_roomId);
            
            // Update battery level periodically (must be in main thread)
            UpdateBatteryLevel();

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
            _messageProcessor.SetNetSyncManager(this);
            _messageProcessor.OnLocalClientNoAssigned += OnLocalClientNoAssigned;
            _connectionManager = new ConnectionManager(this, _messageProcessor, _enableDebugLogs, _logNetworkTraffic);
            _avatarManager = new AvatarManager(_enableDebugLogs);
            _rpcManager = new RPCManager(_connectionManager, _deviceId, this);
            _transformSyncManager = new TransformSyncManager(_connectionManager, _deviceId, _sendRate);
            _discoveryManager = new ServerDiscoveryManager(_enableDebugLogs);
            _networkVariableManager = new NetworkVariableManager(_connectionManager, _deviceId, this);

            // Setup events
            _connectionManager.OnConnectionError += HandleConnectionError;
            _connectionManager.OnConnectionEstablished += OnConnectionEstablished;
            _avatarManager.OnClientDisconnected.AddListener(OnRemoteAvatarDisconnected);
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
            _shouldCheckReady = true;
            
            // Initialize battery level immediately on connection
            _lastBatteryUpdate = -_batteryUpdateInterval; // Force immediate update on next Update()
        }

        private void OnRemoteAvatarDisconnected(int clientNo)
        {
            if (OnClientDisconnected != null)
            {
                OnClientDisconnected.Invoke(clientNo);
            }
        }

        private void OnRPCReceivedHandler(int senderClientNo, string functionName, string[] args)
        {
            string argsStr = args != null && args.Length > 0 ? string.Join(", ", args) : "none";
            Debug.Log($"[NetSyncManager] RPC Received - Sender: Client#{senderClientNo}, Function: {functionName}, Args: [{argsStr}]");
            if (OnRPCReceived != null)
            {
                OnRPCReceived.Invoke(senderClientNo, functionName, args);
            }
        }

        private void OnGlobalVariableChangedHandler(string name, string oldValue, string newValue)
        {
            Debug.Log($"[NetSyncManager] Global Variable Changed - Name: {name}, Old: {oldValue ?? "null"}, New: {newValue ?? "null"}");
            if (OnGlobalVariableChanged != null)
            {
                OnGlobalVariableChanged.Invoke(name, oldValue, newValue);
            }
        }

        private void OnClientVariableChangedHandler(int clientNo, string name, string oldValue, string newValue)
        {
            Debug.Log($"[NetSyncManager] Client Variable Changed - Client#{clientNo}, Name: {name}, Old: {oldValue ?? "null"}, New: {newValue ?? "null"}");
            if (OnClientVariableChanged != null)
            {
                OnClientVariableChanged.Invoke(clientNo, name, oldValue, newValue);
            }
        }

        private void OnLocalClientNoAssigned(int clientNo)
        {
            _clientNo = clientNo;
            DebugLog($"Local client number assigned: {clientNo}");

            // Flush pending self client NV
            foreach (var (name, value) in _pendingSelfClientNV)
            {
                _networkVariableManager?.SetClientVariable(_clientNo, name, value, _roomId);
            }
            _pendingSelfClientNV.Clear();

            // Set flag to check ready state on main thread
            _shouldCheckReady = true;
        }

        private void CheckAndFireReady()
        {
            if (IsReady && !_hasInvokedReady)
            {
                _hasInvokedReady = true;
                DebugLog("NetSyncManager is now Ready (connected, handshaken, and network variables synced)");
                OnReady?.Invoke();

                // Don't flush immediately - let Update() handle it on next frame
                // This avoids potential socket state issues
            }
        }
        
        /// <summary>
        /// Triggers a ready state check. Called internally when network variables are first received.
        /// </summary>
        internal void TriggerReadyCheck()
        {
            _shouldCheckReady = true;
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
            _connectionManager.Connect(fullAddress, _dealerPort, _subPort, _roomId);
        }

        private void StopNetworking()
        {
            _connectionManager.Disconnect();
            StopDiscovery();
        }

        private void StartDiscovery()
        {
            if (_discoveryManager == null) { return; }
            _connectionManager.StartDiscovery(_discoveryManager, _roomId);
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
            _messageProcessor.ProcessMessageQueue(_avatarManager, _rpcManager, _deviceId, this, _networkVariableManager);
            _rpcManager.ProcessRPCQueue();
        }

        private void SendTransformUpdates()
        {
            if (_connectionManager.IsConnected && !_connectionManager.IsConnectionError && _transformSyncManager.ShouldSendTransform(Time.time))
            {
                if (_isStealthMode)
                {
                    // Send stealth handshake instead of regular transform
                    if (!_transformSyncManager.SendStealthHandshake(_roomId))
                    {
                        HandleConnectionError("Send failed – disconnected?");
                    }
                }
                else
                {
                    // Normal transform sending
                    var localAvatar = _avatarManager.LocalAvatar;
                    if (!_transformSyncManager.SendLocalTransform(localAvatar, _roomId))
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
            _clientNo = 0; // Reset client number
            _hasInvokedReady = false; // Reset ready state
            _shouldCheckReady = false; // Reset check flag
            _networkVariableManager?.ResetInitialSyncFlag(); // Reset network variable sync state
            StopNetworking();
        }

        private void DebugLog(string msg)
        {
            if (_enableDebugLogs) { Debug.Log($"[NetSyncManager] {msg}"); }
        }

        /// <summary>
        /// Updates battery level information every 60 seconds via client network variable
        /// </summary>
        private void UpdateBatteryLevel()
        {
            float currentTime = Time.time;
            if (currentTime - _lastBatteryUpdate >= _batteryUpdateInterval)
            {
                _lastBatteryUpdate = currentTime;
                
                // Get battery level from Unity SystemInfo
                float batteryLevel = SystemInfo.batteryLevel;
                string batteryLevelString;
                
                // Handle case where battery level is unavailable (-1)
                if (batteryLevel < 0)
                {
                    batteryLevelString = "N/A";
                }
                else
                {
                    // Convert to string with 2 decimal places
                    batteryLevelString = batteryLevel.ToString("F2");
                }
                
                // Set as client network variable so other clients can see this device's battery level
                SetClientVariable("BatteryLevel", batteryLevelString);
                
                if (_enableDebugLogs)
                {
                    DebugLog($"Battery level updated: {batteryLevelString}");
                }
            }
        }
        #endregion ------------------------------------------------------------------------
    }
}