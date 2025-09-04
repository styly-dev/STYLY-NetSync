// NetSyncAvatar.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Unity.XR.CoreUtils;

namespace Styly.NetSync
{
    public class NetSyncAvatar : MonoBehaviour
    {
        [Header("Network Settings")]
        [SerializeField, ReadOnly] private string _deviceId;
        [SerializeField, ReadOnly] private int _clientNo;

        [Header("Physical Transform Data")]
        [ReadOnly] public Vector3 PhysicalPosition;
        [ReadOnly] public Vector3 PhysicalRotation;

        [Header("Body Parts")]
        public Transform _head;
        public Transform _rightHand;
        public Transform _leftHand;
        public Transform[] _virtualTransforms; // Object array to sync Virtual position (world coordinate system)

        // Properties
        public string DeviceId => _deviceId;
        public int ClientNo => _clientNo;
        public bool IsLocalAvatar { get; private set; }

        // Reference to NetSyncManager
        private NetSyncManager _netSyncManager;

        // Smoothing helper for remote avatars (shared with Human Presence)
        private readonly NetSyncTransformSmoother _smoother = new NetSyncTransformSmoother(0.1f);

        // Events
        [Header("Network Variable Events")]
        public UnityEvent<string, string, string> OnClientVariableChanged;

        void Start()
        {

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
        internal void Initialize(string deviceId, bool isLocalAvatar, NetSyncManager manager)
        {
            _deviceId = deviceId;
            IsLocalAvatar = isLocalAvatar;
            _netSyncManager = manager;

            if (isLocalAvatar)
            {
                // For local avatar, client number will be updated via NetSyncManager
                _clientNo = 0;
            }

            _smoother.InitializeForAvatar(_head, _head, _rightHand, _leftHand, _virtualTransforms);
        }

        // Initialization method for remote avatars with known client number
        internal void InitializeRemote(int clientNo, NetSyncManager manager)
        {
            _clientNo = clientNo;
            _deviceId = null; // Will be set when ID mapping is received
            IsLocalAvatar = false;
            _netSyncManager = manager;

            _smoother.InitializeForAvatar(_head, _head, _rightHand, _leftHand, _virtualTransforms);
        }

        void Update()
        {
            if (IsLocalAvatar && _netSyncManager != null)
            {
                // For local avatar, update client number display
                _clientNo = _netSyncManager.ClientNo;
            }

            if (!IsLocalAvatar)
            {
                _smoother.Update(Time.deltaTime);
            }

#if UNITY_EDITOR
            // Update physical transform display values for inspector
            if (!IsLocalAvatar)
            {
                PhysicalPosition = _head.localPosition;
                PhysicalRotation = _head.localEulerAngles;
            }
#endif
        }

        // Get current transform data for sending
        internal ClientTransformData GetTransformData()
        {
            return new ClientTransformData
            {
                deviceId = _deviceId,
                clientNo = _clientNo,
                physical = GetWorldTransform(_head),
                head = GetWorldTransform(_head),
                rightHand = GetWorldTransform(_rightHand),
                leftHand = GetWorldTransform(_leftHand),
                virtuals = GetWorldTransformList(_virtualTransforms)
            };
        }

        // Update device ID when mapping is received
        internal void UpdateDeviceId(string deviceId)
        {
            if (!IsLocalAvatar && !string.IsNullOrEmpty(deviceId))
            {
                _deviceId = deviceId;
            }
        }

        // Receive and apply transform data (for remote avatars)
        internal void SetTransformData(ClientTransformData data)
        {
            if (IsLocalAvatar) { return; }

            _smoother.SetTargets(data);

            PhysicalPosition = data.physical != null ? data.physical.GetPosition() : Vector3.zero;
            PhysicalRotation = data.physical != null ? data.physical.GetRotation() : Vector3.zero;

            // Update client number for remote avatars
            _clientNo = data.clientNo;
        }

        // Get world transform data (world space, full 6DOF)
        internal TransformData GetWorldTransform(Transform transform)
        {
            if (transform == null) return new TransformData();
            return new TransformData(
                transform.position,
                transform.eulerAngles
            );
        }

        // Convert transform array to TransformData list (world space)
        internal List<TransformData> GetWorldTransformList(Transform[] transforms)
        {
            var result = new List<TransformData>(transforms.Length);
            foreach (var t in transforms) result.Add(GetWorldTransform(t));
            return result;
        }

        // Handle client variable changes from NetSyncManager
        internal void HandleClientVariableChanged(int clientNo, string name, string oldValue, string newValue)
        {
            // Only invoke the event if the change is for this client
            if (clientNo == _clientNo)
            {
                if (OnClientVariableChanged != null)
                {
                    OnClientVariableChanged.Invoke(name, oldValue, newValue);
                }
            }
        }

        #region === Network Variables Convenience Methods ===
        /// <summary>
        /// Set a client variable for this NetSyncAvatar's owner
        /// </summary>
        public bool SetClientVariable(string name, string value)
        {
            return NetSyncManager.Instance != null ? NetSyncManager.Instance.SetClientVariable(_clientNo, name, value) : false;
        }

        /// <summary>
        /// Get a client variable for this NetSyncAvatar's owner
        /// </summary>
        public string GetClientVariable(string name, string defaultValue = null)
        {
            return NetSyncManager.Instance != null ? NetSyncManager.Instance.GetClientVariable(_clientNo, name, defaultValue) : defaultValue;
        }

        /// <summary>
        /// Set a client variable for a specific client
        /// </summary>
        public bool SetClientVariable(int targetClientNo, string name, string value)
        {
            return NetSyncManager.Instance != null ? NetSyncManager.Instance.SetClientVariable(targetClientNo, name, value) : false;
        }

        /// <summary>
        /// Get a client variable for a specific client
        /// </summary>
        public string GetClientVariable(int clientNo, string name, string defaultValue = null)
        {
            return NetSyncManager.Instance != null ? NetSyncManager.Instance.GetClientVariable(clientNo, name, defaultValue) : defaultValue;
        }
        #endregion
    }
}
