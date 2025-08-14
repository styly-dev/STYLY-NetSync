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

        // [Header("Interpolation Settings")]
        private float _interpolationSpeed = 10f;

        // Properties
        public string DeviceId => _deviceId;
        public int ClientNo => _clientNo;
        public bool IsLocalAvatar { get; private set; }

        // Variables for interpolation
        private TransformData _targetPhysical;
        private TransformData _targetHead;
        private TransformData _targetRightHand;
        private TransformData _targetLeftHand;
        private List<TransformData> _targetVirtuals = new List<TransformData>();
        private bool _hasTargetData = false;

        // Reference to NetSyncManager
        private NetSyncManager _netSyncManager;

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

            if (!isLocalAvatar)
            {
                // For remote players, set initial data for interpolation
                _targetPhysical = new TransformData();
                _targetHead = new TransformData();
                _targetRightHand = new TransformData();
                _targetLeftHand = new TransformData();
                _targetVirtuals.Clear();
                for (int i = 0; i < _virtualTransforms.Length; i++)
                {
                    _targetVirtuals.Add(new TransformData());
                }
            }
        }

        // Initialization method for remote players with known client number
        public void InitializeRemote(int clientNo, NetSyncManager manager)
        {
            _clientNo = clientNo;
            _deviceId = null; // Will be set when ID mapping is received
            IsLocalAvatar = false;
            _netSyncManager = manager;

            // For remote players, set initial data for interpolation
            _targetPhysical = new TransformData();
            _targetHead = new TransformData();
            _targetRightHand = new TransformData();
            _targetLeftHand = new TransformData();
            _targetVirtuals.Clear();
            for (int i = 0; i < _virtualTransforms.Length; i++)
            {
                _targetVirtuals.Add(new TransformData());
            }
        }

        void Update()
        {
            if (!IsLocalAvatar)
            {
                // For remote players, interpolate and update Transform
                if (_hasTargetData)
                {
                    InterpolateTransforms();
                }
            }
            else if (_netSyncManager != null)
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

            // If this is the first data received, immediately set position to avoid interpolation from origin
            if (!_hasTargetData)
            {
                // Set physical transform immediately (local space)
                if (_physicalTransform != null && data.physical != null)
                {
                    _physicalPosition = data.physical.GetPosition();
                    _physicalRotation = data.physical.GetRotation();
                    _physicalTransform.localPosition = data.physical.GetPosition();
                    _physicalTransform.localRotation = Quaternion.Euler(data.physical.GetRotation());
                }

                // Set head transform immediately (world space)
                if (_head != null && data.head != null)
                {
                    _head.position = data.head.GetPosition();
                    _head.rotation = Quaternion.Euler(data.head.GetRotation());
                }

                // Set hand transforms immediately (world space)
                if (_rightHand != null && data.rightHand != null)
                {
                    _rightHand.position = data.rightHand.GetPosition();
                    _rightHand.rotation = Quaternion.Euler(data.rightHand.GetRotation());
                }

                if (_leftHand != null && data.leftHand != null)
                {
                    _leftHand.position = data.leftHand.GetPosition();
                    _leftHand.rotation = Quaternion.Euler(data.leftHand.GetRotation());
                }

                // Set virtual transforms immediately (world space)
                if (_virtualTransforms != null && data.virtuals != null)
                {
                    int count = Mathf.Min(_virtualTransforms.Length, data.virtuals.Count);
                    for (int i = 0; i < count; i++)
                    {
                        if (_virtualTransforms[i] != null && data.virtuals[i] != null)
                        {
                            var vt = data.virtuals[i];
                            _virtualTransforms[i].position = vt.GetPosition();
                            _virtualTransforms[i].rotation = Quaternion.Euler(vt.GetRotation());
                        }
                    }
                }
            }

            _targetPhysical = data.physical;
            _targetHead = data.head;
            _targetRightHand = data.rightHand;
            _targetLeftHand = data.leftHand;
            _targetVirtuals = data.virtuals;
            _hasTargetData = true;
            _physicalPosition = data.physical?.GetPosition() ?? Vector3.zero;
            _physicalRotation = data.physical?.GetRotation() ?? Vector3.zero;

            // Update client number for remote players
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
            var result = new List<TransformData>();
            if (transforms != null)
            {
                foreach (var t in transforms)
                {
                    if (t != null)
                    {
                        result.Add(GetWorldTransform(t));
                    }
                }
            }
            return result;
        }

        // Simplified transform interpolation
        private void InterpolateTransforms()
        {
            float deltaTime = Time.deltaTime * _interpolationSpeed;
            
            // Physical Transform interpolation (local space)
            if (_physicalTransform != null && _targetPhysical != null)
            {
                _physicalTransform.localPosition = Vector3.Lerp(_physicalTransform.localPosition, _targetPhysical.GetPosition(), deltaTime);
                _physicalTransform.localRotation = Quaternion.Lerp(_physicalTransform.localRotation, Quaternion.Euler(_targetPhysical.GetRotation()), deltaTime);
            }
            
            // Head Transform interpolation (world space)
            if (_head != null && _targetHead != null)
            {
                _head.position = Vector3.Lerp(_head.position, _targetHead.GetPosition(), deltaTime);
                _head.rotation = Quaternion.Lerp(_head.rotation, Quaternion.Euler(_targetHead.GetRotation()), deltaTime);
            }
            
            // Right Hand Transform interpolation (world space)
            if (_rightHand != null && _targetRightHand != null)
            {
                _rightHand.position = Vector3.Lerp(_rightHand.position, _targetRightHand.GetPosition(), deltaTime);
                _rightHand.rotation = Quaternion.Lerp(_rightHand.rotation, Quaternion.Euler(_targetRightHand.GetRotation()), deltaTime);
            }
            
            // Left Hand Transform interpolation (world space)
            if (_leftHand != null && _targetLeftHand != null)
            {
                _leftHand.position = Vector3.Lerp(_leftHand.position, _targetLeftHand.GetPosition(), deltaTime);
                _leftHand.rotation = Quaternion.Lerp(_leftHand.rotation, Quaternion.Euler(_targetLeftHand.GetRotation()), deltaTime);
            }

            // Virtual Transforms interpolation (world space)
            if (_virtualTransforms != null && _targetVirtuals != null)
            {
                int count = Mathf.Min(_virtualTransforms.Length, _targetVirtuals.Count);
                for (int i = 0; i < count; i++)
                {
                    if (_virtualTransforms[i] != null && _targetVirtuals[i] != null)
                    {
                        _virtualTransforms[i].position = Vector3.Lerp(_virtualTransforms[i].position, _targetVirtuals[i].GetPosition(), deltaTime);
                        _virtualTransforms[i].rotation = Quaternion.Lerp(_virtualTransforms[i].rotation, Quaternion.Euler(_targetVirtuals[i].GetRotation()), deltaTime);
                    }
                }
            }
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