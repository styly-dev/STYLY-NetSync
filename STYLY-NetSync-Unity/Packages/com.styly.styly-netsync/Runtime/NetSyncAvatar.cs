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
        private Transform3D _targetPhysical;
        private Transform3D _targetHead;
        private Transform3D _targetRightHand;
        private Transform3D _targetLeftHand;
        private List<Transform3D> _targetVirtuals = new List<Transform3D>();
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
                _targetPhysical = new Transform3D();
                _targetHead = new Transform3D();
                _targetRightHand = new Transform3D();
                _targetLeftHand = new Transform3D();
                _targetVirtuals.Clear();
                for (int i = 0; i < _virtualTransforms.Length; i++)
                {
                    _targetVirtuals.Add(new Transform3D());
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
            _targetPhysical = new Transform3D();
            _targetHead = new Transform3D();
            _targetRightHand = new Transform3D();
            _targetLeftHand = new Transform3D();
            _targetVirtuals.Clear();
            for (int i = 0; i < _virtualTransforms.Length; i++)
            {
                _targetVirtuals.Add(new Transform3D());
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
                physical = ConvertToTransform3D(_physicalTransform, true),
                head = ConvertToTransform3D(_head, false),
                rightHand = ConvertToTransform3D(_rightHand, false),
                leftHand = ConvertToTransform3D(_leftHand, false),
                virtuals = ConvertToTransform3DList(_virtualTransforms, false)
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
                // Set physical transform immediately
                if (_physicalTransform != null && data.physical != null)
                {
                    _physicalPosition = new Vector3(data.physical.posX, data.physical.posY, data.physical.posZ);
                    Vector3 newPhysicalPosition = new Vector3(data.physical.posX, data.physical.posY, data.physical.posZ);
                    Vector3 newPhysicalRotation = new Vector3(data.physical.rotX, data.physical.rotY, data.physical.rotZ);
                    _physicalPosition = newPhysicalPosition;
                    _physicalRotation = newPhysicalRotation;
                    _physicalTransform.localPosition = newPhysicalPosition;
                    _physicalTransform.localEulerAngles = newPhysicalRotation;
                    _physicalTransform.localPosition = _physicalPosition;
                    _physicalTransform.localEulerAngles = _physicalRotation;
                }

                // Set head transform immediately
                if (_head != null && data.head != null)
                {
                    _head.position = new Vector3(data.head.posX, data.head.posY, data.head.posZ);
                    _head.rotation = Quaternion.Euler(data.head.rotX, data.head.rotY, data.head.rotZ);
                }

                // Set hand transforms immediately
                if (_rightHand != null && data.rightHand != null)
                {
                    _rightHand.position = new Vector3(data.rightHand.posX, data.rightHand.posY, data.rightHand.posZ);
                    _rightHand.rotation = Quaternion.Euler(data.rightHand.rotX, data.rightHand.rotY, data.rightHand.rotZ);
                }

                if (_leftHand != null && data.leftHand != null)
                {
                    _leftHand.position = new Vector3(data.leftHand.posX, data.leftHand.posY, data.leftHand.posZ);
                    _leftHand.rotation = Quaternion.Euler(data.leftHand.rotX, data.leftHand.rotY, data.leftHand.rotZ);
                }

                // Set virtual transforms immediately
                if (_virtualTransforms != null && data.virtuals != null)
                {
                    int count = Mathf.Min(_virtualTransforms.Length, data.virtuals.Count);
                    for (int i = 0; i < count; i++)
                    {
                        if (_virtualTransforms[i] != null && data.virtuals[i] != null)
                        {
                            var vt = data.virtuals[i];
                            _virtualTransforms[i].position = new Vector3(vt.posX, vt.posY, vt.posZ);
                            _virtualTransforms[i].rotation = Quaternion.Euler(vt.rotX, vt.rotY, vt.rotZ);
                        }
                    }
                }
            }

            _targetHead = data.head;
            _targetRightHand = data.rightHand;
            _targetLeftHand = data.leftHand;
            _targetVirtuals = data.virtuals;
            _hasTargetData = true;
            _physicalPosition = new Vector3(data.physical.posX, data.physical.posY, data.physical.posZ);
            _physicalRotation = new Vector3(data.physical.rotX, data.physical.rotY, data.physical.rotZ);

            // Update client number for remote players
            _clientNo = data.clientNo;
        }

        // Unified transform conversion method
        // isLocalSpace: whether to read from local space (physical) vs world space (virtual)
        private Transform3D ConvertToTransform3D(Transform transform, bool isLocalSpace)
        {
            if (transform == null) { return new Transform3D(); }

            if (isLocalSpace)
            {
                // Physical/local transform (XZ position, Y rotation only)
                return new Transform3D(
                    transform.localPosition.x,
                    0,
                    transform.localPosition.z,
                    0,
                    transform.localEulerAngles.y,
                    0,
                    true
                );
            }
            else
            {
                // Virtual/world transform (full 6DOF)
                return new Transform3D(
                    transform.position.x,
                    transform.position.y,
                    transform.position.z,
                    transform.eulerAngles.x,
                    transform.eulerAngles.y,
                    transform.eulerAngles.z,
                    false
                );
            }
        }

        // Convert transform array to Transform3D list
        private List<Transform3D> ConvertToTransform3DList(Transform[] transforms, bool isLocalSpace)
        {
            var result = new List<Transform3D>();
            if (transforms != null)
            {
                foreach (var t in transforms)
                {
                    if (t != null)
                    {
                        result.Add(ConvertToTransform3D(t, isLocalSpace));
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
                InterpolateSingleTransform(_head, _targetHead, deltaTime, false);
            }
            // Right Hand Transform interpolation (world space)
            if (_rightHand != null && _targetRightHand != null)
            {
                InterpolateSingleTransform(_rightHand, _targetRightHand, deltaTime, false);
            }
            // Left Hand Transform interpolation (world space)
            if (_leftHand != null && _targetLeftHand != null)
            {
                InterpolateSingleTransform(_leftHand, _targetLeftHand, deltaTime, false);
            }

            // Virtual Transforms interpolation (world space)
            if (_virtualTransforms != null && _targetVirtuals != null)
            {
                int count = Mathf.Min(_virtualTransforms.Length, _targetVirtuals.Count);
                for (int i = 0; i < count; i++)
                {
                    if (_virtualTransforms[i] != null)
                    {
                        InterpolateSingleTransform(_virtualTransforms[i], _targetVirtuals[i], deltaTime, false);
                    }
                }
            }
        }

        // Unified interpolation method for any transform
        // isLocalSpace: interpolate using localPosition/localRotation vs world position/rotation
        private void InterpolateSingleTransform(Transform transform, Transform3D target, float deltaTime, bool isLocalSpace)
        {
            Vector3 targetPos = isLocalSpace
                ? new Vector3(target.posX, transform.localPosition.y, target.posZ)
                : new Vector3(target.posX, target.posY, target.posZ);
            Quaternion targetRot = Quaternion.Euler(target.rotX, target.rotY, target.rotZ);

            if (isLocalSpace)
            {
                transform.localPosition = Vector3.Lerp(transform.localPosition, targetPos, deltaTime);
                transform.localRotation = Quaternion.Lerp(transform.localRotation, targetRot, deltaTime);
            }
            else
            {
                transform.position = Vector3.Lerp(transform.position, targetPos, deltaTime);
                transform.rotation = Quaternion.Lerp(transform.rotation, targetRot, deltaTime);
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