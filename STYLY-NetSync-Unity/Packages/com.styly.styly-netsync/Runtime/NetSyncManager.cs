// NetSyncManager.cs
using System;
using System.Collections.Generic;
using Styly.NetSync.Internal;
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
        [SerializeField, Tooltip("Server IP address or hostname (e.g. 192.168.1.100, localhost). Leave empty to auto-discover server on local network")] private string _serverAddress = "";
        private int _dealerPort = 5555;
        private int _transformPort = 5557;
        private int _subPort = 5556;
        [SerializeField] private string _roomId = "default_room";

        [Header("Avatar Settings")]
        [SerializeField] private GameObject _localAvatarPrefab;
        [SerializeField] private GameObject _remoteAvatarPrefab;
        [SerializeField, Tooltip("Prefab shown at each remote user's physical position")] private GameObject _humanPresencePrefab;

        // [Header("Debug Settings")]
        private bool _enableDebugLogs = true;
        private bool _logTransformDetail = false;
        private bool _logNetworkTraffic = false;

        // The "Events" header and per-event tooltips are rendered by
        // NetSyncManagerEditor so UnityEventDrawer doesn't swallow them.
        // Initialize UnityEvents at declaration to ensure they are always non-null
        [Tooltip("Fired when a remote avatar connects. Parameter: clientNo (int) — the unique client number assigned to the connected avatar.")]
        public UnityEvent<int> OnAvatarConnected = new UnityEvent<int>();
        [Tooltip("Fired when a remote avatar disconnects. Parameter: clientNo (int) — the unique client number of the disconnected avatar.")]
        public UnityEvent<int> OnAvatarDisconnected = new UnityEvent<int>();
        [Tooltip("Fired when an RPC is received. Parameters: senderClientNo (int), functionName (string), args (string[]).")]
        public UnityEvent<int, string, string[]> OnRPCReceived = new UnityEvent<int, string, string[]>();
        [Tooltip("Fired when a global network variable changes. Parameters: name (string), oldValue (string), newValue (string).")]
        public UnityEvent<string, string, string> OnGlobalVariableChanged = new UnityEvent<string, string, string>();
        [Tooltip("Fired when a client-specific network variable changes. Parameters: clientNo (int), name (string), oldValue (string), newValue (string).")]
        public UnityEvent<int, string, string, string> OnClientVariableChanged = new UnityEvent<int, string, string, string>();
        [Tooltip("Fired when the client is fully connected and synchronized (connected, handshaked, and network variables synced).")]
        public UnityEvent OnReady = new UnityEvent();
        /// <summary>
        /// Event fired when server and client versions do not match.
        /// Parameters: (serverVersion, clientVersion) as strings like "0.7.5"
        /// </summary>
        [Tooltip("Fired when server and client versions do not match. Parameters: serverVersion (string), clientVersion (string) — e.g. \"0.7.5\".")]
        public UnityEvent<string, string> OnVersionMismatch = new UnityEvent<string, string>();

        // Advanced Options (drawn by NetSyncManagerEditor in a foldout)
        [SerializeField, Tooltip("Enable offline mode for testing without a server.")]
        private bool _offlineMode = false;
        [SerializeField, Range(0.5f, 60), Tooltip("Transform sync frequency in Hz (sends per second). Higher values provide smoother movement but increase network traffic.")]
        private float _transformSendRate = 10f;
        [Tooltip("UDP port used for server discovery.")]
        [SerializeField, Min(1)] private int _serverDiscoveryPort = 9999;
        [Tooltip("Enable synchronization of battery levels across devices.")]
        [SerializeField] private bool _syncBatteryLevel = true;
        [SerializeField, Tooltip("Clear this client's network variables once after the first connection.")]
        private bool _clearClientNetworkVariablesOnStart = false;
        private bool _enableDiscovery = true;
        private float _discoveryTimeout = 10f;

        internal const string PrefixForSystem = "@system:"; // Prefix for system-only message names

        #endregion ------------------------------------------------------------------------

        internal Transform _XrOriginTransform;
        internal Vector3 _physicalOffsetPosition;
        internal Vector3 _physicalOffsetRotation;

        #region === Singleton & Public API ===
        private static NetSyncManager _instance;
        public static NetSyncManager Instance => _instance;



        /// <summary>
        /// Get the version of STYLY NetSync.
        /// </summary>
        /// <returns></returns>
        public string GetVersion()
        {
            return Information.GetVersion();
        }

        // Public RPC methods for external access
        public void Rpc(string functionName, string[] args = null)
        {
            if (args == null) { args = Array.Empty<string>(); }

            if (_rpcManager != null)
            {
                _rpcManager.Send(_roomId, functionName, args);
            }
        }

        public void Rpc(string functionName, string[] args, int targetClientNo)
        {
            Rpc(functionName, args, new[] { targetClientNo });
        }

        public void Rpc(string functionName, string[] args, int[] targetClientNos)
        {
            if (args == null) { args = Array.Empty<string>(); }
            if (targetClientNos == null || targetClientNos.Length == 0)
            {
                Rpc(functionName, args);
                return;
            }

            if (_rpcManager != null)
            {
                _rpcManager.SendTo(_roomId, targetClientNos, functionName, args);
            }
        }

        internal void Rpc_SystemRPC(string functionName, string[] args = null)
        {
            functionName = PrefixForSystem + functionName;

            if (args == null) { args = Array.Empty<string>(); }

            if (_rpcManager != null)
            {
                _rpcManager.Send(_roomId, functionName, args);
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
            return _networkVariableManager != null ? _networkVariableManager.SetClientVariable(name, value, _clientNo, _roomId) : false;
        }

        public bool SetClientVariable(string name, string value, int targetClientNo)
        {
            return _networkVariableManager != null ? _networkVariableManager.SetClientVariable(name, value, targetClientNo, _roomId) : false;
        }

        /// <summary>
        /// Clear all Client Network Variables owned by this client on the server.
        /// If called before the local client number is assigned, the clear is sent
        /// once the handshake completes.
        /// Returns true when the request was sent or queued for the handshake.
        /// </summary>
        public bool ClearMyClientVariables()
        {
            if (_clientNo <= 0)
            {
                _pendingClearMyClientVariables = true;
                return true;
            }

            if (_networkVariableManager == null)
            {
                return false;
            }

            var sent = _networkVariableManager.ClearMyClientVariables();
            if (sent)
            {
                _pendingSelfClientNV.Clear();
                _pendingClearMyClientVariables = false;
            }
            return sent;
        }

        public string GetClientVariable(string name, string defaultValue = null)
        {
            return _networkVariableManager != null ? _networkVariableManager.GetClientVariable(name, _clientNo, defaultValue) : defaultValue;
        }

        public string GetClientVariable(string name, int clientNo, string defaultValue = null)
        {
            return _networkVariableManager != null ? _networkVariableManager.GetClientVariable(name, clientNo, defaultValue) : defaultValue;
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

        /// <summary>
        /// Gets the server-assigned client number for the specified device ID.
        /// Returns 0 when the mapping is not yet known (e.g. before the device has
        /// been observed in this room, or before the local handshake has completed),
        /// or when <paramref name="deviceId"/> is null or empty.
        /// When network traffic logging is enabled, unresolved lookups may also
        /// emit a warning from the underlying message processor.
        /// </summary>
        /// <param name="deviceId">The stable device identifier.</param>
        /// <returns>The client number, or 0 if no mapping is known.</returns>
        public int GetClientNoByDeviceId(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return 0;
            return _messageProcessor != null ? _messageProcessor.GetClientNo(deviceId) : 0;
        }

        /// <summary>
        /// Gets the stable device ID for the specified client number.
        /// Returns null when the mapping is not yet known (e.g. before the device
        /// has been observed in this room), or when <paramref name="clientNo"/> is
        /// not a positive value.
        /// </summary>
        /// <param name="clientNo">The server-assigned client number (&gt; 0).</param>
        /// <returns>The device ID, or null if no mapping is known.</returns>
        public string GetDeviceIdByClientNo(int clientNo)
        {
            if (clientNo <= 0) return null;
            return _messageProcessor != null ? _messageProcessor.GetDeviceIdFromClientNo(clientNo) : null;
        }

        /// <summary>
        /// Gets a list (int[]) of all currently connected client numbers.
        /// </summary>
        /// <param name="includeStealthClients">If true, includes clients in stealth mode (no visible avatar). Default is false.</param>
        /// <returns>An int[] containing the client numbers of all connected clients</returns>
        public int[] GetAliveClients(bool includeStealthClients = false)
        {
            var set = _avatarManager?.GetAliveClients(_messageProcessor, includeStealthClients, _clientNo);
            if (set == null || set.Count == 0) return Array.Empty<int>();
            var arr = new int[set.Count];
            set.CopyTo(arr);
            return arr;
        }

        internal void RegisterNetSyncObject(NetSyncObject obj)
        {
            if (_objectSyncManager != null)
            {
                _objectSyncManager.Register(obj);
            }
        }

        internal void UnregisterNetSyncObject(NetSyncObject obj)
        {
            if (_objectSyncManager != null)
            {
                _objectSyncManager.Unregister(obj);
            }
        }

        public void RequestObjectOwnership(byte operationType, uint objectId)
        {
            if (_objectSyncManager != null)
            {
                _objectSyncManager.SendOwnershipRequest(_roomId, operationType, objectId);
            }
        }

        #region === Moving Floors ===
        /// <summary>
        /// The moving floor id currently carrying the local avatar, or 0 when off floor.
        /// </summary>
        public uint LocalMovingFloorId
        {
            get
            {
                if (_movingFloorManager == null)
                {
                    return MovingFloorManager.UnassignedFloorId;
                }

                return _movingFloorManager.LocalFloorId;
            }
        }

        /// <summary>
        /// True when the local avatar is currently sending poses in a moving floor's local coordinates.
        /// </summary>
        public bool IsLocalAvatarOnMovingFloor
        {
            get
            {
                return _movingFloorManager != null && _movingFloorManager.HasLocalFloor;
            }
        }

        /// <summary>
        /// True when the local avatar is currently on the specified moving floor id.
        /// </summary>
        public bool IsLocalAvatarOnMovingFloorId(uint floorId)
        {
            if (_movingFloorManager == null || floorId == MovingFloorManager.UnassignedFloorId)
            {
                return false;
            }

            return _movingFloorManager.HasLocalFloor && _movingFloorManager.LocalFloorId == floorId;
        }

        /// <summary>
        /// Register a scene-stable moving floor used by protocol v5 moving-floor-local avatar poses.
        /// The same floor id must resolve to the corresponding local Transform on every client.
        /// </summary>
        public bool RegisterMovingFloor(uint floorId, Transform floor)
        {
            if (_movingFloorManager == null)
            {
                return false;
            }

            return _movingFloorManager.RegisterFloor(floorId, floor);
        }

        /// <summary>
        /// Remove a previously registered moving floor.
        /// </summary>
        public void UnregisterMovingFloor(uint floorId)
        {
            if (_movingFloorManager == null)
            {
                return;
            }

            bool wasOnFloorLocally =
                floorId != MovingFloorManager.UnassignedFloorId &&
                _movingFloorManager.LocalFloorId == floorId;
            _movingFloorManager.UnregisterFloor(floorId);
            if (wasOnFloorLocally)
            {
                _movingFloorManager.LeaveLocal();
                SetClientVariable(MovingFloorManager.ClientVariableName, "");
            }
        }

        /// <summary>
        /// Board the local avatar onto an already registered moving floor.
        /// While on the floor, the avatar pose is sent in that floor's local coordinates.
        /// </summary>
        public bool BoardLocalAvatarOnMovingFloor(uint floorId)
        {
            if (_movingFloorManager == null)
            {
                return false;
            }

            if (!_movingFloorManager.BoardLocal(floorId))
            {
                Debug.LogWarning(
                    $"[NetSync] BoardLocalAvatarOnMovingFloor failed. Register moving floor '{MovingFloorManager.FormatFloorId(floorId)}' before boarding.");
                return false;
            }

            SetClientVariable(MovingFloorManager.ClientVariableName, MovingFloorManager.EncodeFloorId(floorId));
            return true;
        }

        /// <summary>
        /// Register a moving floor Transform and board the local avatar onto it.
        /// </summary>
        public bool BoardLocalAvatarOnMovingFloor(uint floorId, Transform floor)
        {
            if (!RegisterMovingFloor(floorId, floor))
            {
                Debug.LogWarning("[NetSync] BoardLocalAvatarOnMovingFloor failed. floorId and floor must be valid.");
                return false;
            }

            return BoardLocalAvatarOnMovingFloor(floorId);
        }

        /// <summary>
        /// Leave the local avatar from its current moving floor and resume normal world-space pose sync.
        /// </summary>
        public void LeaveLocalAvatarFromMovingFloor()
        {
            if (_movingFloorManager == null)
            {
                return;
            }

            _movingFloorManager.LeaveLocal();
            SetClientVariable(MovingFloorManager.ClientVariableName, "");
        }
        #endregion

        /// <summary>
        /// Set the room ID at runtime and reconnect to the new room.
        /// This performs a hard reconnection, clearing all room-scoped state and
        /// re-establishing connection with the new room subscription.
        /// </summary>
        /// <param name="newRoomId">The new room ID to connect to</param>
        public void SetRoomId(string newRoomId)
        {
            // Validate input
            if (string.IsNullOrEmpty(newRoomId) || newRoomId == _roomId)
            {
                return; // No-op for same room or invalid input
            }

            // Prevent reentrancy
            if (_roomSwitching)
            {
                DebugLog($"Room switch already in progress, ignoring request to switch to: {newRoomId}");
                return;
            }

            DebugLog($"Switching from room '{_roomId}' to '{newRoomId}'");

            // Start room switching
            _roomSwitching = true;
            ClearPendingClientVariableOperations();
            _clientNo = 0;
            _hasInvokedReady = false;
            _shouldCheckReady = false;
            _shouldSendHandshake = false;

            // Update room ID
            _roomId = newRoomId;

            // Clear room-scoped state to prevent leaks across rooms
            _messageProcessor?.ClearRoomScopedState();
            _objectSyncManager?.ClearRoomScopedState();
            _timeEstimator.Reset();
            _networkVariableManager?.ResetInitialSyncFlag();
            _avatarManager?.CleanupRemoteAvatars();
            if (_humanPresenceManager != null) { _humanPresenceManager.CleanupAll(); }
            if (_movingFloorManager != null) { _movingFloorManager.ClearClientStates(true); }

            // Hard reconnect with new room
            _connectionManager?.Disconnect();

            // Start networking will establish new connection with updated _roomId
            StartNetworking();
        }

        #endregion ------------------------------------------------------------------------

        #region === Runtime Fields ===
        private static bool _netMqInit;

        // Managers
        private IConnectionManager _connectionManager;
        private AvatarManager _avatarManager;
        private RPCManager _rpcManager;
        private TransformSyncManager _transformSyncManager;
        private MessageProcessor _messageProcessor;
        private ServerDiscoveryManager _discoveryManager;
        private NetworkVariableManager _networkVariableManager;
        private HumanPresenceManager _humanPresenceManager;
        private ObjectSyncManager _objectSyncManager;
        private MovingFloorManager _movingFloorManager = new MovingFloorManager();
        private readonly NetSyncTimeEstimator _timeEstimator = new NetSyncTimeEstimator();

        // State
        private bool _isDiscovering;
        private bool _isStealthMode;
        private bool _roomSwitching; // Flag to prevent operations during room switching
        private string _discoveredServer;
        private int _discoveredDealerPort;
        private int _discoveredTransformPort;
        private int _discoveredSubPort;
        private float _discoveryStartTime;
        private const float ReconnectDelay = 10f;
        private const float DiscoveryRetryDelay = 5f; // Retry discovery every 5 seconds after failure
        private float _nextDiscoveryAttemptAt = 0f;
        private float _reconnectAt;
        private readonly List<(string name, string value)> _pendingSelfClientNV = new List<(string name, string value)>();
        private bool _pendingClearMyClientVariables;
        private bool _hasClearedClientNetworkVariablesOnStart;
        private bool _hasInvokedReady = false;
        private bool _shouldCheckReady = false;
        private bool _shouldSendHandshake = false;
        // Battery monitoring fields
        private float _batteryUpdateInterval = 60.0f; // Update every 60 seconds
        private float _lastBatteryUpdate = 0.0f; // Last time we updated battery level

        // Connection error handling fields (main-thread handoff for thread-safe error handling)
        /// <summary>
        /// Atomic flag for pending connection errors.
        /// Written by receive thread, consumed by main thread.
        /// Values: 0=no pending error, 1=error pending
        /// </summary>
        private int _pendingConnectionError;

        /// <summary>
        /// Exception instance from the last connection error.
        /// Written on receive thread, read on main thread. Volatile for visibility.
        /// </summary>
        private volatile Exception _pendingConnectionException;

        /// <summary>
        /// Re-entrancy guard for ProcessPendingConnectionErrorOnMainThread.
        /// Main-thread only, prevents concurrent error handling.
        /// </summary>
        private bool _isHandlingConnectionError;

        // Constants for atomic flag values
        private const int PENDING_ERROR_NONE = 0;
        private const int PENDING_ERROR_SET = 1;

        // Backpressure statistics and cooldown (P1-05)
        private long _transformBackpressureCount;
        private float _lastBackpressureWarnAt;
        private const float BackpressureWarnCooldown = 1f; // Log backpressure warning at most once per second

        // Async device ID resolution
        private DeviceIdResolver _deviceIdResolver;
        private bool _networkingPending;
        #endregion ------------------------------------------------------------------------

        #region === Public Properties ===
        public string DeviceId => _deviceId;
        public int ClientNo => _clientNo;
        public string RoomId => _roomId;

        /// <summary>
        /// Server address the client is configured to connect to, or the
        /// address resolved via auto-discovery when the inspector field is left empty.
        /// Empty string until auto-discovery completes.
        /// </summary>
        public string ServerAddress => _serverAddress;

        /// <summary>
        /// UDP port used for server discovery.
        /// </summary>
        public int ServerDiscoveryPort
        {
            get => _serverDiscoveryPort;
            set => _serverDiscoveryPort = value;
        }

        /// <summary>
        /// Transform sync frequency in Hz (sends per second). Valid range: 0.5-60.
        /// Higher values provide smoother movement but increase network traffic.
        /// </summary>
        public float TransformSendRate
        {
            get => _transformSendRate;
            set
            {
                _transformSendRate = Mathf.Clamp(value, 0.5f, 60f);
                if (_transformSyncManager != null)
                {
                    _transformSyncManager.SendRate = _transformSendRate;
                }
            }
        }
        internal IConnectionManager ConnectionManager => _connectionManager;
        internal AvatarManager AvatarManager => _avatarManager;
        internal RPCManager RPCManager => _rpcManager;
        internal TransformSyncManager TransformSyncManager => _transformSyncManager;
        internal MessageProcessor MessageProcessor => _messageProcessor;
        internal NetSyncTimeEstimator TimeEstimator => _timeEstimator;
        internal bool IsStealthMode => _isStealthMode;
        public bool IsOfflineMode => _offlineMode;
        public bool HasServerConnection => _connectionManager?.IsConnected == true && !_connectionManager.IsConnectionError;
        public bool HasHandshake => _clientNo > 0;
        public bool HasNetworkVariablesSync => _networkVariableManager?.HasReceivedInitialSync == true;
        public bool IsReady => HasServerConnection && HasHandshake && HasNetworkVariablesSync;

        public GameObject GetLocalAvatarPrefab() => _localAvatarPrefab;
        public GameObject GetRemoteAvatarPrefab() => _remoteAvatarPrefab;
        public GameObject GetHumanPresencePrefab() => _humanPresencePrefab;
        #endregion ------------------------------------------------------------------------

        #region === Unity Callbacks ===
        private void Awake()
        {
            // Log package version
            DebugLog($"STYLY-NetSync Version: {GetVersion()}");

            Application.runInBackground = true;

            if (!_netMqInit) { AsyncIO.ForceDotNet.Force(); _netMqInit = true; }

            _instance = this;

            // Detect stealth mode based on local avatar prefab
            _isStealthMode = (_localAvatarPrefab == null);

            // Resolve device ID (may complete asynchronously on Android when permission dialog is shown)
            _deviceIdResolver = new DeviceIdResolver(_enableDebugLogs);
            _deviceIdResolver.Resolve();

            if (_deviceIdResolver.IsResolved)
            {
                CompleteDeviceIdResolution();
                _networkingPending = false; // Prevent double-init by disabling the deferred Update() initialization path
            }
            // Otherwise, Update() will complete initialization when the resolver finishes
        }

        void Start()
        {
            // Resolve the rig transform used as the locomotion-delta reference.
            // The follow source is latched here at startup; runtime rig
            // switching (e.g. disabling XROrigin and enabling OVRCameraRig
            // mid-session) is not supported and will keep referencing the
            // initially resolved transform.
            Transform rigTransform = RigTransformResolver.TryResolve();

            if (rigTransform != null)
            {
                _XrOriginTransform = rigTransform;
                _physicalOffsetPosition = rigTransform.position;
                _physicalOffsetRotation = rigTransform.eulerAngles;
            }
        }

        private void OnEnable()
        {
            if (_deviceIdResolver != null && _deviceIdResolver.IsResolved)
            {
                _avatarManager.InitializeLocalAvatar(_localAvatarPrefab, _deviceId, this);
                StartNetworking();
            }
            else
            {
                // Device ID not yet resolved (async permission dialog on Android).
                // Networking will start once the resolver completes.
                _networkingPending = true;
            }
        }

        private void OnDisable()
        {
            _networkingPending = false;

            if (_connectionManager != null)
            {
                StopNetworking();
            }

            if (_avatarManager != null) { _avatarManager.CleanupRemoteAvatars(); }
            if (_humanPresenceManager != null) { _humanPresenceManager.CleanupAll(); }

            // Dispose pooled serialization resources to return buffers to ArrayPool
            DisposeManagers();

            _instance = null;
        }

        private void OnApplicationQuit()
        {
            if (_connectionManager != null)
            {
                StopNetworking();
            }
            // Be explicit on quit as well in case OnDisable order varies
            DisposeManagers();
        }

        private void OnApplicationPause(bool paused)
        {
            // This is a wourkaround to ignore initial pause for ANdroid devices
            if (Time.time < 1) { return; }

            // Managers not yet initialized (async device ID resolution in progress)
            if (_connectionManager == null) { return; }

            if (paused)
            {
                DebugLog("Application paused - stopping network");
                StopNetworking();
            }
            else
            {
                DebugLog("Application resumed - restarting network");

                // Reset handshake/ready state so the client waits for a fresh
                // handshake and NV initial sync from the server before reporting
                // IsReady.  Without this, stale state from the previous session
                // lets IsReady remain true while the new connection is still
                // being established.
                ClearPendingClientVariableOperations();
                _clientNo = 0;
                _hasInvokedReady = false;
                _shouldCheckReady = false;
                _shouldSendHandshake = false;
                _networkVariableManager?.ResetInitialSyncFlag();

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
                if (_connectionManager != null && !_connectionManager.IsConnected && !_isDiscovering)
                {
                    StartNetworking();
                }
            }
        }

        private void Update()
        {
            // Process deferred permission callbacks on main thread (safe for Unity APIs)
            if (_deviceIdResolver != null)
            {
                _deviceIdResolver.Tick();
            }

            // Complete deferred device ID resolution (async Android permission path)
            if (_networkingPending && _deviceIdResolver != null && _deviceIdResolver.IsResolved)
            {
                _networkingPending = false;
                CompleteDeviceIdResolution();
                _avatarManager.InitializeLocalAvatar(_localAvatarPrefab, _deviceId, this);
                StartNetworking();
            }

            // Skip main loop until managers are initialized
            if (_connectionManager == null) { return; }

            // Check ready state on main thread
            if (_shouldCheckReady)
            {
                _shouldCheckReady = false;
                CheckAndFireReady();
            }

            // Handle handshake sending on main thread (for room switching)
            if (_shouldSendHandshake)
            {
                _shouldSendHandshake = false;
                HandleRoomSwitchHandshake();
            }

            // Process discovery manager main thread queue (for PlayerPrefs operations)
            if (_discoveryManager != null)
            {
                _discoveryManager.Update();
            }

            HandleDiscovery();

            // Drain network-thread logs and callbacks on main thread
            if (_connectionManager != null)
            {
                _connectionManager.DrainMainThreadActions();
            }

            // Process pending connection errors BEFORE reconnection logic
            ProcessPendingConnectionErrorOnMainThread();

            HandleReconnection();
            ProcessMessages();

            // Skip transform/NV operations during room switching
            if (!_roomSwitching)
            {
                // P1-04: Control messages (NV/RPC) are sent before transform
                // This prioritizes important control messages over high-frequency transform updates
                // which helps maintain responsiveness under low bandwidth conditions

                TryFlushPendingClientVariableClear();

                // Process Network Variables debounced updates (control - high priority)
                _networkVariableManager?.Tick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, _roomId);

                // Flush pending RPCs (control - high priority)
                _rpcManager?.FlushPendingIfReady(_roomId);

                // Send transform updates (lower priority, can be dropped if backpressured)
                SendTransformUpdates();

                // Object sync: send owned object transforms and apply received transforms
                if (_objectSyncManager != null)
                {
                    _objectSyncManager.Tick(_roomId, Time.time, _clientNo, _transformSendRate);
                    _objectSyncManager.TickTransformAppliers(Time.deltaTime, NetSyncClock.NowSeconds());
                }
            }

            // Check for initial sync timeout (important for rooms with no variables)
            bool wasReady = HasNetworkVariablesSync;
            _networkVariableManager?.CheckInitialSyncTimeout();
            if (!wasReady && HasNetworkVariablesSync)
            {
                // Initial sync timeout triggered ready state
                _shouldCheckReady = true;
            }

            // Update battery level periodically (must be in main thread)
            UpdateBatteryLevel();

            LogStatistics();

            // Apply Human Presence transforms on main thread
            if (_humanPresenceManager != null)
            {
                _humanPresenceManager.Tick();
            }
        }
        #endregion ------------------------------------------------------------------------

        #region === Initialization ===
        /// <summary>
        /// Completes device ID resolution by reading the result from the resolver
        /// and initializing all managers. Called either synchronously from Awake()
        /// or deferred from Update() when the Android permission dialog was shown.
        /// </summary>
        private void CompleteDeviceIdResolution()
        {
            _deviceId = _deviceIdResolver.DeviceId;
            DebugLog($"Device ID: {_deviceId}");

            InitializeManagers();

            if (_isStealthMode)
            {
                DebugLog("Stealth mode enabled (no local avatar prefab)");
            }
        }

        private void InitializeManagers()
        {
            // Initialize managers
            _messageProcessor = new MessageProcessor(_logNetworkTraffic);
            _messageProcessor.SetLocalDeviceId(_deviceId);
            _messageProcessor.SetNetSyncManager(this);
            _messageProcessor.OnLocalClientNoAssigned += OnLocalClientNoAssigned;
            _messageProcessor.OnVersionMismatch += HandleVersionMismatch;
            if (_offlineMode)
                _connectionManager = new OfflineConnectionManager();
            else
                _connectionManager = new ConnectionManager(this, _messageProcessor, _enableDebugLogs, _logNetworkTraffic);
            _avatarManager = new AvatarManager(_enableDebugLogs);
            _rpcManager = new RPCManager(_connectionManager, _deviceId, this);
            _transformSyncManager = new TransformSyncManager(_connectionManager, _deviceId, _transformSendRate);
            _discoveryManager = new ServerDiscoveryManager(_enableDebugLogs);
            _discoveryManager.SetServerDiscoveryPort(ServerDiscoveryPort);
            _networkVariableManager = new NetworkVariableManager(_connectionManager, _deviceId, this);
            _humanPresenceManager = new HumanPresenceManager(this, _enableDebugLogs);
            _objectSyncManager = new ObjectSyncManager(_connectionManager, _messageProcessor, _timeEstimator, _enableDebugLogs);
            // Setup events
            _connectionManager.OnConnectionError += HandleConnectionError;
            _connectionManager.OnConnectionEstablished += OnConnectionEstablished;
            _avatarManager.OnAvatarDisconnected.AddListener(OnRemoteAvatarDisconnected);
            _rpcManager.OnRPCReceived.AddListener(OnRPCReceivedHandler);

            // Human Presence lifecycle follows avatar connect/disconnect events
            OnAvatarConnected.AddListener(_humanPresenceManager.HandleAvatarConnected);
            OnAvatarDisconnected.AddListener(_humanPresenceManager.HandleAvatarDisconnected);

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

            // Notify network variable manager about connection
            _networkVariableManager?.OnConnectionEstablished();

            if (_offlineMode)
            {
                OnLocalClientNoAssigned(1);
                _networkVariableManager?.MarkInitialSyncComplete();
                DebugLog("Offline mode: client number set to 1 via OnLocalClientNoAssigned, initial sync marked complete");
                return;
            }

            // If we're switching rooms, defer handshake to main thread
            if (_roomSwitching)
            {
                _shouldSendHandshake = true;
                DebugLog("Connection established during room switch - handshake deferred to main thread");
            }

            // Initialize battery level immediately on connection if sync is enabled
            if (_syncBatteryLevel)
            {
                _lastBatteryUpdate = -_batteryUpdateInterval; // Force immediate update on next Update()
            }
        }

        /// <summary>
        /// Handles handshake sending for room switching on the main thread.
        /// This method is called from Update() to avoid Unity threading issues.
        /// </summary>
        private void HandleRoomSwitchHandshake()
        {
            if (!_roomSwitching)
            {
                DebugLog("HandleRoomSwitchHandshake called but not in room switching mode");
                return;
            }

            // Send handshake to new room to trigger client number assignment
            SendOutcome outcome;
            string handshakeType;
            if (_isStealthMode)
            {
                outcome = _transformSyncManager != null
                    ? _transformSyncManager.SendStealthHandshake(_roomId)
                    : SendOutcome.Fatal("TransformSyncManager is null");
                handshakeType = "stealth";
            }
            else
            {
                var localAvatar = _avatarManager != null ? _avatarManager.LocalAvatar : null;
                if (localAvatar != null)
                {
                    outcome = _transformSyncManager != null
                        ? _transformSyncManager.SendLocalTransform(localAvatar, _roomId)
                        : SendOutcome.Fatal("TransformSyncManager is null");
                    handshakeType = "transform";
                }
                else
                {
                    // Fallback to stealth handshake if no local avatar
                    outcome = _transformSyncManager != null
                        ? _transformSyncManager.SendStealthHandshake(_roomId)
                        : SendOutcome.Fatal("TransformSyncManager is null");
                    handshakeType = "stealth (no local avatar)";
                }
            }

            // Handle outcome - backpressure on handshake is concerning but not fatal
            // The next Update() cycle will retry sending
            if (outcome.IsBackpressure)
            {
                _transformBackpressureCount++;
                MaybeLogBackpressureWarning("room switch handshake");
                DebugLog($"Room switch {handshakeType} handshake backpressure, will retry");
                // Don't end room switching yet - retry on next frame
                _shouldSendHandshake = true;
                return;
            }

            if (outcome.IsFatal)
            {
                DebugLog($"Room switch {handshakeType} handshake fatal: {outcome.Error}");
                HandleConnectionError($"Room switch handshake fatal: {outcome.Error}");
                _roomSwitching = false;
                return;
            }

            // End room switching on success
            DebugLog($"Sent {handshakeType} handshake for room {_roomId}: {outcome}");
            _roomSwitching = false;
            DebugLog("Room switching completed");
        }

        private void OnRemoteAvatarDisconnected(int clientNo)
        {
            if (_movingFloorManager != null)
            {
                _movingFloorManager.RemoveClient(clientNo);
            }

            OnAvatarDisconnected?.Invoke(clientNo);
        }

        private void OnRPCReceivedHandler(int senderClientNo, string functionName, string[] args)
        {
            string argsStr = args != null && args.Length > 0 ? string.Join(", ", args) : "none";

            // Route system RPCs to internal handler, others to public event
            if (functionName.StartsWith(PrefixForSystem))
            {
                functionName = functionName[PrefixForSystem.Length..]; // Remove prefix
                OnRPCReceivedHandler_SystemRPC(senderClientNo, functionName, args);
                Debug.Log($"[NetSyncManager] System RPC Received - Sender: Client#{senderClientNo}, Function: {functionName}, Args: [{argsStr}]");
            }
            else
            {
                OnRPCReceived?.Invoke(senderClientNo, functionName, args);
                Debug.Log($"[NetSyncManager] RPC Received - Sender: Client#{senderClientNo}, Function: {functionName}, Args: [{argsStr}]");
            }
        }

        private void OnRPCReceivedHandler_SystemRPC(int senderClientNo, string functionName, string[] args)
        {
            // Handle known system RPCs
            switch (functionName)
            {
                default:
                    DebugLog($"Unknown system RPC received: {functionName}");
                    break;
            }
        }

        private void OnGlobalVariableChangedHandler(string name, string oldValue, string newValue)
        {
            Debug.Log($"[NetSyncManager] Global Variable Changed - Name: {name}, Old: {oldValue ?? "null"}, New: {newValue ?? "null"}");
            OnGlobalVariableChanged?.Invoke(name, oldValue, newValue);
        }

        private void OnClientVariableChangedHandler(int clientNo, string name, string oldValue, string newValue)
        {
            Debug.Log($"[NetSyncManager] Client Variable Changed - Client#{clientNo}, Name: {name}, Old: {oldValue ?? "null"}, New: {newValue ?? "null"}");
            if (name == MovingFloorManager.ClientVariableName && _movingFloorManager != null)
            {
                _movingFloorManager.SetClientFloorId(clientNo, newValue);
            }

            OnClientVariableChanged?.Invoke(clientNo, name, oldValue, newValue);
        }

        private void OnLocalClientNoAssigned(int clientNo)
        {
            _clientNo = clientNo;
            DebugLog($"Local client number assigned: {clientNo}");

            if (_clearClientNetworkVariablesOnStart && !_hasClearedClientNetworkVariablesOnStart)
            {
                _pendingClearMyClientVariables = true;
            }

            if (_pendingClearMyClientVariables)
            {
                TryFlushPendingClientVariableClear();
            }

            // Flush pending self client NV
            if (!_pendingClearMyClientVariables)
            {
                foreach (var (name, value) in _pendingSelfClientNV)
                {
                    _networkVariableManager?.SetClientVariable(name, value, _clientNo, _roomId);
                }
                _pendingSelfClientNV.Clear();
            }

            // Set flag to check ready state on main thread
            _shouldCheckReady = true;
        }

        private void TryFlushPendingClientVariableClear()
        {
            if (!_pendingClearMyClientVariables || _clientNo <= 0 || _networkVariableManager == null)
            {
                return;
            }

            if (_networkVariableManager.ClearMyClientVariables())
            {
                _pendingSelfClientNV.Clear();
                _pendingClearMyClientVariables = false;
                if (_clearClientNetworkVariablesOnStart)
                {
                    _hasClearedClientNetworkVariablesOnStart = true;
                }
            }
        }

        private void ClearPendingClientVariableOperations()
        {
            _pendingSelfClientNV.Clear();
            _pendingClearMyClientVariables = false;

            if (_networkVariableManager != null)
            {
                _networkVariableManager.ClearPendingSends();
            }
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

        private void OnServerDiscovered(string serverAddress, int controlPort, int transformPort, int subPort)
        {
            _discoveredServer = serverAddress;
            _discoveredDealerPort = controlPort;
            _discoveredTransformPort = transformPort;
            _discoveredSubPort = subPort;

            // Update the server address for future connections
            // Remove tcp:// prefix (discovery always returns with tcp://)
            _serverAddress = serverAddress.Substring(6);
            _dealerPort = controlPort;
            _transformPort = transformPort;
            _subPort = subPort;

            DebugLog($"Server discovered: {serverAddress} (control:{controlPort}, transform:{transformPort}, sub:{subPort})");
        }
        #endregion ------------------------------------------------------------------------

        #region === Networking ===
        private void StartNetworking()
        {
            if (_offlineMode)
            {
                _connectionManager.Connect("", 0, 0, 0, _roomId);
                return;
            }

            // If server address is empty and discovery is enabled, start discovery
            if (string.IsNullOrEmpty(_serverAddress) && _enableDiscovery && !_isDiscovering)
            {
                StartDiscovery();
                return;
            }

            // Add tcp:// prefix
            string fullAddress = $"tcp://{_serverAddress}";
            _connectionManager.Connect(fullAddress, _dealerPort, _transformPort, _subPort, _roomId);
        }

        private void StopNetworking()
        {
            _connectionManager.Disconnect();
            StopDiscovery();
        }

        private void StartDiscovery()
        {
            if (_discoveryManager == null) { return; }
            _discoveryManager.SetServerDiscoveryPort(ServerDiscoveryPort);
            _connectionManager.StartDiscovery(_discoveryManager, _roomId);
            _isDiscovering = true;
            _discoveryStartTime = Time.time;
            // Clear any scheduled retry since we're attempting now
            _nextDiscoveryAttemptAt = 0f;
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
            // Process discovered server FIRST (before timeout check)
            // This ensures that if a server is found, we handle it immediately
            if (!string.IsNullOrEmpty(_discoveredServer) && _isDiscovering)
            {
                _isDiscovering = false;
                StopDiscovery();
                _connectionManager.ProcessDiscoveredServer(_discoveredServer, _discoveredDealerPort, _discoveredTransformPort, _discoveredSubPort);
                _discoveredServer = null;
                return; // Exit early - no need to check timeout or retry
            }

            // Handle discovery timeout: stop current attempt and schedule retry in 5 seconds
            if (_isDiscovering && Time.time - _discoveryStartTime > _discoveryTimeout)
            {
                DebugLog($"Discovery timeout - retrying in {DiscoveryRetryDelay} seconds");
                StopDiscovery();
                _nextDiscoveryAttemptAt = Time.time + DiscoveryRetryDelay;
            }

            // If not currently discovering and discovery is enabled with no fixed server, retry when due
            if (!_isDiscovering && string.IsNullOrEmpty(_serverAddress) && _enableDiscovery &&
                _nextDiscoveryAttemptAt > 0f && Time.time >= _nextDiscoveryAttemptAt)
            {
                StartDiscovery();
            }
        }

        private void HandleReconnection()
        {
            // Use _reconnectAt instead of IsConnectionError to avoid race condition
            // where ClearConnectionError() is called before this check runs
            if (_reconnectAt > 0 && Time.time >= _reconnectAt)
            {
                _reconnectAt = 0; // Reset to prevent repeated attempts
                DebugLog($"Attempting reconnection to {_serverAddress}...");
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
            if (!_connectionManager.IsConnected || _connectionManager.IsConnectionError)
                return;

            if (!_transformSyncManager.ShouldSendTransform(Time.time))
                return;

            SendOutcome outcome;
            if (_isStealthMode)
            {
                // Send stealth handshake instead of regular transform
                outcome = _transformSyncManager.SendStealthHandshake(_roomId);
            }
            else
            {
                // Normal transform sending
                var localAvatar = _avatarManager.LocalAvatar;
                outcome = _transformSyncManager.SendLocalTransform(localAvatar, _roomId);
            }

            // Update last send time regardless of outcome (prevents send spam on backpressure)
            _transformSyncManager.UpdateLastSendTime(Time.time);

            if (outcome.IsSent)
            {
                // Successfully sent
                return;
            }

            if (outcome.IsBackpressure)
            {
                // P1-03: Backpressure is NOT a disconnect - just log and continue
                // The system will naturally recover when the send queue clears
                _transformBackpressureCount++;
                MaybeLogBackpressureWarning("transform");
                return;
            }

            // Fatal error - this may indicate a real disconnect
            HandleConnectionError($"Transform send fatal: {outcome.Error}");
        }

        /// <summary>
        /// Logs a backpressure warning with cooldown to prevent log spam.
        /// </summary>
        private void MaybeLogBackpressureWarning(string messageType)
        {
            float now = Time.time;
            if (now - _lastBackpressureWarnAt >= BackpressureWarnCooldown)
            {
                _lastBackpressureWarnAt = now;
                DebugLog($"Backpressure on {messageType} send (total count: {_transformBackpressureCount}). " +
                         "This is normal under low bandwidth - messages will be sent when queue clears.");
            }
        }

        private void LogStatistics()
        {
            // Statistics logging is currently disabled
            // Uncomment to enable periodic statistics logging
        }
        #endregion ------------------------------------------------------------------------

        #region === Utility ===
        /// <summary>
        /// Processes pending connection errors on the main thread.
        /// This method ensures deterministic cleanup and reconnection scheduling
        /// without executing teardown on the receive thread.
        /// </summary>
        private void ProcessPendingConnectionErrorOnMainThread()
        {
            // Consume the pending flag atomically
            if (System.Threading.Interlocked.Exchange(ref _pendingConnectionError, PENDING_ERROR_NONE) == PENDING_ERROR_NONE)
            {
                return; // No pending error
            }

            // Re-entrancy guard: prevent concurrent execution
            if (_isHandlingConnectionError)
            {
                return; // Already handling an error
            }

            try
            {
                _isHandlingConnectionError = true;

                // Don't schedule reconnection if application is quitting
                if (!Application.isPlaying)
                {
                    DebugLog("Application not playing - skipping reconnection schedule");
                    return;
                }

                // Schedule reconnection deterministically
                _reconnectAt = Time.time + ReconnectDelay;

                // Log detailed context
                string exType = _pendingConnectionException?.GetType().Name ?? "Unknown";
                string exMsg = _pendingConnectionException?.Message ?? "No details";
                DebugLog($"ConnectionError detected. Scheduling reconnect. " +
                         $"now={Time.time:F2} reconnectAt={_reconnectAt:F2} " +
                         $"exType={exType} exMsg={exMsg}");

                // Reset client state
                ClearPendingClientVariableOperations();
                _clientNo = 0;
                _hasInvokedReady = false;
                _shouldCheckReady = false;
                _shouldSendHandshake = false;
                _networkVariableManager?.ResetInitialSyncFlag();

                // Execute teardown (idempotent)
                DebugLog("StopNetworking start. reason=ConnectionError");
                StopNetworking();
                DebugLog("StopNetworking completed.");

                // Cleanup avatars and presence
                _avatarManager?.CleanupRemoteAvatars();
                _humanPresenceManager?.CleanupAll();

                // Clear error state for next reconnect attempt
                _connectionManager?.ClearConnectionError();
            }
            finally
            {
                _isHandlingConnectionError = false;
            }
        }

        /// <summary>
        /// Handles version mismatch between server and client.
        /// Disconnects from the server to prevent protocol incompatibility issues.
        /// </summary>
        private void HandleVersionMismatch(int serverMajor, int serverMinor, int serverPatch,
            int clientMajor, int clientMinor, int clientPatch)
        {
            var serverVersion = $"{serverMajor}.{serverMinor}.{serverPatch}";
            var clientVersion = $"{clientMajor}.{clientMinor}.{clientPatch}";

            Debug.LogError($"[NetSyncManager] Version mismatch! Server: {serverVersion}, Client: {clientVersion}. Disconnecting.");

            // Invoke the public UnityEvent for external handlers before disconnecting
            if (OnVersionMismatch != null)
            {
                OnVersionMismatch.Invoke(serverVersion, clientVersion);
            }

            // Disconnect to prevent protocol incompatibility issues
            StopNetworking();
        }

        /// <summary>
        /// Handles connection errors from both receive thread (via OnConnectionError callback)
        /// and main thread (via send failures). Thread-safe: uses atomic handoff to main thread.
        /// </summary>
        private void HandleConnectionError(string reason)
        {
            // DO NOT execute teardown here - may be called from receive thread
            // Instead, mark pending for main thread processing

            // Copy exception context from ConnectionManager BEFORE setting flag
            // This ensures consistency between exception and timestamp
            if (_connectionManager != null)
            {
                // Read volatile fields - they are written in order (timestamp first, then exception)
                var exception = _connectionManager.LastException;

                _pendingConnectionException = exception;
            }

            // Now set the pending flag (atomic operation ensures main thread sees the exception data)
            System.Threading.Interlocked.Exchange(ref _pendingConnectionError, PENDING_ERROR_SET);

            // Log simple notification on receive thread (safe)
            Debug.LogError($"[NetSyncManager] Connection error detected: {reason}");
        }

        private void DebugLog(string msg)
        {
            if (_enableDebugLogs) { Debug.Log($"[NetSyncManager] {msg}"); }
        }

        /// <summary>
        /// Disposes disposable manager instances and clears references.
        /// Must be called on the Unity main thread.
        /// </summary>
        private void DisposeManagers()
        {
            // Note: These are not UnityEngine.Object, but we still guard with explicit null checks.
            _rpcManager?.Dispose();
            _rpcManager = null;
            _transformSyncManager?.Dispose();
            _transformSyncManager = null;
            _networkVariableManager?.Dispose();
            _networkVariableManager = null;
            _objectSyncManager?.Dispose();
            _objectSyncManager = null;
        }

        /// <summary>
        /// Updates battery level information every 60 seconds via client network variable
        /// </summary>
        private void UpdateBatteryLevel()
        {
            // Check if battery sync is enabled
            if (!_syncBatteryLevel)
            {
                return;
            }

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

        internal bool TryGetLocalMovingFloor(out Transform floor)
        {
            floor = null;
            if (_movingFloorManager == null)
            {
                return false;
            }

            return _movingFloorManager.TryGetLocalFloor(out floor);
        }

        internal bool TryGetMovingFloorForClient(int clientNo, bool warnIfMissingId, out Transform floor)
        {
            floor = null;
            if (_movingFloorManager == null)
            {
                return false;
            }

            return _movingFloorManager.TryGetFloorForClient(clientNo, warnIfMissingId, out floor);
        }

        /// <summary>
        /// Compute XROrigin locomotion delta relative to startup origin.
        /// t = p1 - R(dyaw) * p0, where p0/p1 are full 3D positions and R(dyaw) is yaw-only
        /// (so the Y component reduces to t.y = p1.y - p0.y).
        /// </summary>
        internal void ComputeXrOriginDelta(out Vector3 t, out float dyawDeg)
        {
            var p0 = _physicalOffsetPosition;
            var yaw0 = _physicalOffsetRotation.y;

            var p1 = p0;
            var yaw1 = yaw0;
            if (_XrOriginTransform != null)
            {
                p1 = _XrOriginTransform.position;
                yaw1 = _XrOriginTransform.eulerAngles.y;
            }

            dyawDeg = Mathf.DeltaAngle(yaw0, yaw1);
            var deltaRotation = Quaternion.Euler(0f, dyawDeg, 0f);
            t = p1 - (deltaRotation * p0);
        }

        /// <summary>
        /// Update a remote client's Human Presence transform from the client's "physical" pose.
        /// The incoming position/rotation are already the sender's physical HMD pose.
        /// This method runs on the main Unity thread (invoked from MessageProcessor).
        /// </summary>
        /// <param name="clientNo">Remote client number</param>
        /// <param name="position">Physical HMD position</param>
        /// <param name="rotation">Physical HMD rotation; only yaw is used</param>
        internal void UpdateHumanPresenceTransform(
            int clientNo,
            Vector3 position,
            Quaternion rotation,
            double poseTime,
            ushort poseSeq)
        {
            if (_humanPresenceManager == null) { return; }

            var worldPos = position;
            var worldYaw = Quaternion.Euler(0f, MovingFloorManager.ExtractYawDegrees(rotation), 0f);

            _humanPresenceManager.UpdateTransform(clientNo, worldPos, worldYaw, poseTime, poseSeq);
        }

        internal void UpdateBoundHumanPresenceFromPhysical(int clientNo, in PoseSampleData physical)
        {
            if (_humanPresenceManager == null)
            {
                return;
            }

            // Moving-floor-local poses carry direct physical HMD pose in the
            // physical slot. Do not derive presence from the floor-local head,
            // because that would make the physical marker ride the moving floor.
            var worldPos = physical.Position;
            var worldYaw = Quaternion.Euler(0f, MovingFloorManager.ExtractYawDegrees(physical.Rotation), 0f);
            _humanPresenceManager.SetTransformImmediate(clientNo, worldPos, worldYaw);
        }
    }
}
