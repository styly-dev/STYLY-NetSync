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
                virtuals = ConvertToTransformDataList(_virtualTransforms, false)
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

            if (_physicalTransform != null && data.physical != null)
            {
                _physicalTransform.localPosition = data.physical.position;
                _physicalTransform.localRotation = Quaternion.Euler(data.physical.rotation);
                _physicalPosition = data.physical.position;
                _physicalRotation = data.physical.rotation;
            }

            if (!_hasTargetData)
            {
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

            _targetHead = data.head;
            _targetRightHand = data.rightHand;
            _targetLeftHand = data.leftHand;
            _targetVirtuals = data.virtuals;
            _hasTargetData = true;

            _clientNo = data.clientNo;
        }

        private TransformData ConvertToTransformData(Transform transform, bool useLocal)
        {
            if (transform == null) return new TransformData();
            return new TransformData
            {
                position = useLocal ? transform.localPosition : transform.position,
                rotation = useLocal ? transform.localEulerAngles : transform.eulerAngles
            };
        }

        private List<TransformData> ConvertToTransformDataList(Transform[] transforms, bool useLocal)
        {
            var result = new List<TransformData>();
            if (transforms != null)
            {
                foreach (var t in transforms)
                {
                    if (t != null)
                    {
                        result.Add(ConvertToTransformData(t, useLocal));
                    }
                }
            }
            return result;
        }

        // Simplified transform interpolation
        private void InterpolateTransforms()
        {
            float deltaTime = Time.deltaTime * _interpolationSpeed;
            // Head Transform interpolation (world space)
            if (_head != null && _targetHead != null)
            {
                InterpolateSingleTransform(_head, _targetHead, deltaTime);
            }
            // Right Hand Transform interpolation (world space)
            if (_rightHand != null && _targetRightHand != null)
            {
                InterpolateSingleTransform(_rightHand, _targetRightHand, deltaTime);
            }
            // Left Hand Transform interpolation (world space)
            if (_leftHand != null && _targetLeftHand != null)
            {
                InterpolateSingleTransform(_leftHand, _targetLeftHand, deltaTime);
            }

            // Virtual Transforms interpolation (world space)
            if (_virtualTransforms != null && _targetVirtuals != null)
            {
                int count = Mathf.Min(_virtualTransforms.Length, _targetVirtuals.Count);
                for (int i = 0; i < count; i++)
                {
                    if (_virtualTransforms[i] != null)
                    {
                        InterpolateSingleTransform(_virtualTransforms[i], _targetVirtuals[i], deltaTime);
                    }
                }
            }
        }

        private void InterpolateSingleTransform(Transform transform, TransformData target, float deltaTime)
        {
            Vector3 targetPos = target.position;
            Quaternion targetRot = Quaternion.Euler(target.rotation);
            transform.position = Vector3.Lerp(transform.position, targetPos, deltaTime);
            transform.rotation = Quaternion.Lerp(transform.rotation, targetRot, deltaTime);
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