// NetSyncAvatar.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;
using Unity.XR.CoreUtils;


namespace Styly.NetSync
{
    public class NetSyncAvatar : MonoBehaviour
    {
        [Header("Network Settings")]
        [SerializeField, ReadOnly] private string _deviceId;
        [SerializeField, ReadOnly] private int _clientNo;

        [Header("Transform Sync Settings")]
        [SerializeField, ReadOnly] private Transform _physicalTransform; // Object to sync Physical position (local coordinate system). _head will be used for physical transform of local player

        [Header("Physical Transform Data (Runtime)")]
        [SerializeField, ReadOnly] private Vector3 _physicalPosition;
        [SerializeField, ReadOnly] private Vector3 _physicalRotation;

        [Header("Body Parts")]
        [SerializeField] private Transform _head;
        [SerializeField] private Transform _rightHand;
        [SerializeField] private Transform _leftHand;

        [SerializeField] private Transform[] _virtualTransforms; // Object array to sync Virtual position (world coordinate system)

        // Properties
        public string DeviceId => _deviceId;
        public int ClientNo => _clientNo;
        public bool IsLocalAvatar { get; private set; }

        // Reference to NetSyncManager
        private NetSyncManager _netSyncManager;

        private NetSyncAvatarSmoother _smoother;

        // Events
        [Header("Network Variable Events")]
        public UnityEvent<string, string, string> OnClientVariableChanged;

        void Start()
        {
            if (IsLocalAvatar)
            {
                // Use head as the physical transform for local player
                _physicalTransform = _head;
            }
        }

        void OnEnable()
        {
            // Subscribe to NetSyncManager's client variable change event
            if (NetSyncManager.Instance != null)
            {
                NetSyncManager.Instance.OnClientVariableChanged.AddListener(HandleClientVariableChanged);
            }
        }

        void OnDisable()
        {
            // Unsubscribe from NetSyncManager's client variable change event
            if (NetSyncManager.Instance != null)
            {
                NetSyncManager.Instance.OnClientVariableChanged.RemoveListener(HandleClientVariableChanged);
            }
        }

        // Initialization method called from NetSyncManager
        public void Initialize(string deviceId, bool isLocalAvatar, NetSyncManager manager)
        {
            _deviceId = deviceId;
            IsLocalAvatar = isLocalAvatar;
            _netSyncManager = manager;

            if (isLocalAvatar)
            {
                // For local player, client number will be updated via NetSyncManager
                _clientNo = 0;
            }

            // No interpolation initialization needed
        }

        // Initialization method for remote players with known client number
        public void InitializeRemote(int clientNo, NetSyncManager manager)
        {
            _clientNo = clientNo;
            _deviceId = null; // Will be set when ID mapping is received
            IsLocalAvatar = false;
            _netSyncManager = manager;

            _smoother = new NetSyncAvatarSmoother();

            // No interpolation initialization needed
        }

        void Update()
        {
            if (IsLocalAvatar && _netSyncManager != null)
            {
                // For local player, update client number display
                _clientNo = _netSyncManager.ClientNo;
            }

            // Update physical transform display values for inspector
#if UNITY_EDITOR
            if (_physicalTransform != null)
            {
                _physicalPosition = _physicalTransform.localPosition;
                _physicalRotation = _physicalTransform.localEulerAngles;
            }
#endif
        }
        private void LateUpdate()
        {
            if (IsLocalAvatar || _smoother == null) return;

            _smoother.Apply(_physicalTransform, _head, _rightHand, _leftHand, _virtualTransforms, Time.time);
        }


        // Get current transform data for sending
        public ClientTransformData GetTransformData()
        {
            return new ClientTransformData
            {
                deviceId = _deviceId,
                clientNo = _clientNo,
                physical = GetPhysicalTransform(),
                head = GetWorldTransform(_head),
                rightHand = GetWorldTransform(_rightHand),
                leftHand = GetWorldTransform(_leftHand),
                virtuals = GetWorldTransformList(_virtualTransforms)
            };
        }

        // Update device ID when mapping is received
        public void UpdateDeviceId(string deviceId)
        {
            if (!IsLocalAvatar && !string.IsNullOrEmpty(deviceId))
            {
                _deviceId = deviceId;
            }
        }

        // Receive and apply transform data (for remote players)
        public void SetTransformData(ClientTransformData data)
        {
            if (IsLocalAvatar) { return; }

            _smoother ??= new NetSyncAvatarSmoother();

            int virtualLimit = _virtualTransforms != null ? _virtualTransforms.Length : 0;
            _smoother.Enqueue(data, Time.time, virtualLimit);

            _clientNo = data.clientNo;
        }

        // Get physical transform data (local space, full 6DOF)
        private TransformData GetPhysicalTransform()
        {
            if (_physicalTransform == null) return new TransformData();
            return new TransformData(
                _physicalTransform.localPosition,
                _physicalTransform.localEulerAngles
            );
        }

        // Get world transform data (world space, full 6DOF)
        private TransformData GetWorldTransform(Transform transform)
        {
            if (transform == null) return new TransformData();
            return new TransformData(
                transform.position,
                transform.eulerAngles
            );
        }

        // Convert transform array to TransformData list (world space)
        private List<TransformData> GetWorldTransformList(Transform[] transforms)
        {
            var result = new List<TransformData>(transforms.Length);
            foreach (var t in transforms) result.Add(GetWorldTransform(t));
            return result;
        }

        // Handle client variable changes from NetSyncManager
        private void HandleClientVariableChanged(int clientNo, string name, string oldValue, string newValue)
        {
            // Only invoke the event if the change is for this client
            if (clientNo == _clientNo)
            {
                OnClientVariableChanged?.Invoke(name, oldValue, newValue);
            }
        }

        #region === Network Variables Convenience Methods ===
        /// <summary>
        /// Set a client variable for this NetSyncAvatar's owner
        /// </summary>
        public bool SetClientVariable(string name, string value)
        {
            return NetSyncManager.Instance?.SetClientVariable(_clientNo, name, value) ?? false;
        }

        /// <summary>
        /// Get a client variable for this NetSyncAvatar's owner
        /// </summary>
        public string GetClientVariable(string name, string defaultValue = null)
        {
            return NetSyncManager.Instance?.GetClientVariable(_clientNo, name, defaultValue);
        }

        /// <summary>
        /// Set a client variable for a specific client
        /// </summary>
        public bool SetClientVariable(int targetClientNo, string name, string value)
        {
            return NetSyncManager.Instance?.SetClientVariable(targetClientNo, name, value) ?? false;
        }

        /// <summary>
        /// Get a client variable for a specific client
        /// </summary>
        public string GetClientVariable(int clientNo, string name, string defaultValue = null)
        {
            return NetSyncManager.Instance?.GetClientVariable(clientNo, name, defaultValue);
        }
        #endregion
    }
}