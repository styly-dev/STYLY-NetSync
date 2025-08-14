// NetSyncAvatar.cs
using System;
using System.Collections.Generic;
using System.Linq;
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
                physical = ConvertToTransformData(_physicalTransform, true),
                head = ConvertToTransformData(_head, false),
                rightHand = ConvertToTransformData(_rightHand, false),
                leftHand = ConvertToTransformData(_leftHand, false),
                virtuals = _virtualTransforms?.Select(v => ConvertToTransformData(v, false)).ToList() ?? new List<TransformData>()
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
                if (_physicalTransform != null && data.physical != null)
                {
                    _physicalTransform.localPosition = data.physical.position;
                    _physicalTransform.localRotation = Quaternion.Euler(data.physical.rotation);
                    _physicalPosition = data.physical.position;
                    _physicalRotation = data.physical.rotation;
                }

                if (_head != null && data.head != null)
                {
                    _head.position = data.head.position;
                    _head.rotation = Quaternion.Euler(data.head.rotation);
                }

                if (_rightHand != null && data.rightHand != null)
                {
                    _rightHand.position = data.rightHand.position;
                    _rightHand.rotation = Quaternion.Euler(data.rightHand.rotation);
                }

                if (_leftHand != null && data.leftHand != null)
                {
                    _leftHand.position = data.leftHand.position;
                    _leftHand.rotation = Quaternion.Euler(data.leftHand.rotation);
                }

                if (_virtualTransforms != null && data.virtuals != null)
                {
                    int count = Mathf.Min(_virtualTransforms.Length, data.virtuals.Count);
                    for (int i = 0; i < count; i++)
                    {
                        if (_virtualTransforms[i] != null && data.virtuals[i] != null)
                        {
                            _virtualTransforms[i].position = data.virtuals[i].position;
                            _virtualTransforms[i].rotation = Quaternion.Euler(data.virtuals[i].rotation);
                        }
                    }
                }
            }

            _targetPhysical = data.physical ?? new TransformData();
            _targetHead = data.head ?? new TransformData();
            _targetRightHand = data.rightHand ?? new TransformData();
            _targetLeftHand = data.leftHand ?? new TransformData();
            _targetVirtuals = data.virtuals ?? new List<TransformData>();
            _hasTargetData = true;
            _physicalPosition = data.physical.position;
            _physicalRotation = data.physical.rotation;

            _clientNo = data.clientNo;
        }


        private void InterpolateTransforms()
        {
            float deltaTime = Time.deltaTime;
            if (_physicalTransform != null)
            {
                float t = deltaTime * _interpolationSpeed;
                _physicalTransform.localPosition = Vector3.Lerp(_physicalTransform.localPosition, _targetPhysical.position, t);
                _physicalTransform.localRotation = Quaternion.Lerp(_physicalTransform.localRotation, Quaternion.Euler(_targetPhysical.rotation), t);
            }
            if (_head != null)
            {
                InterpolateSingleTransform(_head, _targetHead, deltaTime);
            }
            if (_rightHand != null)
            {
                InterpolateSingleTransform(_rightHand, _targetRightHand, deltaTime);
            }
            if (_leftHand != null)
            {
                InterpolateSingleTransform(_leftHand, _targetLeftHand, deltaTime);
            }
            if (_virtualTransforms != null)
            {
                int count = Mathf.Min(_virtualTransforms.Length, _targetVirtuals.Count);
                for (int i = 0; i < count; i++)
                {
                    InterpolateSingleTransform(_virtualTransforms[i], _targetVirtuals[i], deltaTime);
                }
            }
        }

        private void InterpolateSingleTransform(Transform transform, TransformData target, float deltaTime)
        {
            if (transform == null || target == null) return;
            float t = deltaTime * _interpolationSpeed;
            transform.position = Vector3.Lerp(transform.position, target.position, t);
            transform.rotation = Quaternion.Lerp(transform.rotation, Quaternion.Euler(target.rotation), t);
        }

        private TransformData ConvertToTransformData(Transform t, bool isLocal)
        {
            if (t == null) return new TransformData();
            return new TransformData
            {
                position = isLocal ? t.localPosition : t.position,
                rotation = isLocal ? t.localEulerAngles : t.eulerAngles
            };
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