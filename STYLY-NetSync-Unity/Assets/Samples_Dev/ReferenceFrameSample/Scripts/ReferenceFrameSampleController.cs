using Styly.NetSync;
using UnityEngine;
using UnityEngine.XR;

public class ReferenceFrameSampleController : MonoBehaviour
{
    [Header("Reference Frame")]
    [SerializeField] private string _frameId = "reference-frame-sample/platform-a";
    [SerializeField] private Transform _referenceFrame;
    [SerializeField] private bool _attachOnStart = true;

    [Header("Motion")]
    [SerializeField] private bool _animateFrame = true;
    [SerializeField] private Vector3 _motionAmplitude = new Vector3(0f, 0.75f, 2.5f);
    [SerializeField, Min(0.1f)] private float _motionPeriodSeconds = 8f;
    [SerializeField] private float _yawAmplitudeDegrees = 18f;

    [Header("Local Rider Simulation")]
    [SerializeField] private Transform _riderRoot;
    [SerializeField] private bool _carryRiderRootWhileAttached = true;

    [Header("Status")]
    [SerializeField] private Renderer _statusRenderer;
    [SerializeField] private KeyCode _toggleAttachKey = KeyCode.Space;
    [SerializeField] private KeyCode _toggleMotionKey = KeyCode.M;
    [SerializeField] private bool _enableRightControllerAButton = true;

    private Vector3 _initialFrameLocalPosition;
    private Quaternion _initialFrameLocalRotation;
    private Vector3 _riderFrameLocalPosition;
    private Quaternion _riderFrameLocalRotation = Quaternion.identity;
    private bool _hasRiderFramePose;
    private bool _isRegistered;
    private bool _isAttached;
    private bool _wasRightControllerAPressed;
    private GUIStyle _boxStyle;
    private GUIStyle _titleStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _buttonStyle;

    private void Awake()
    {
        if (_referenceFrame != null)
        {
            _initialFrameLocalPosition = _referenceFrame.localPosition;
            _initialFrameLocalRotation = _referenceFrame.localRotation;
        }
    }

    private void Start()
    {
        CacheRiderFramePose();
        RegisterReferenceFrame();

        if (_attachOnStart)
        {
            AttachLocalAvatar();
        }
        else
        {
            UpdateStatusColor();
        }
    }

    private void OnDisable()
    {
        var manager = NetSyncManager.Instance;
        if (manager == null)
        {
            return;
        }

        if (_isAttached)
        {
            manager.DetachLocalAvatarFromReferenceFrame();
            _isAttached = false;
        }

        if (_isRegistered)
        {
            manager.UnregisterReferenceFrame(_frameId);
            _isRegistered = false;
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(_toggleAttachKey) || GetRightControllerAButtonDown())
        {
            ToggleAttachment();
        }

        if (Input.GetKeyDown(_toggleMotionKey))
        {
            ToggleMotion();
        }
    }

    private void LateUpdate()
    {
        UpdateReferenceFrameMotion();

        if (_carryRiderRootWhileAttached && _isAttached)
        {
            UpdateRiderRootPose();
        }
    }

    private void OnGUI()
    {
        EnsureGuiStyles();

        const int width = 620;
        const int height = 300;
        GUILayout.BeginArea(new Rect(24, 24, width, height), _boxStyle);
        GUILayout.Label("Reference Frame Sample", _titleStyle);
        GUILayout.Space(8f);
        GUILayout.Label("Frame ID: " + _frameId, _labelStyle);
        GUILayout.Label("Attachment: " + (_isAttached ? "Attached" : "Detached"), _labelStyle);
        GUILayout.Label("Motion: " + (_animateFrame ? "Running" : "Paused"), _labelStyle);
        GUILayout.Space(12f);

        string attachLabel = _isAttached
            ? "Detach Local Avatar (Space / Right A)"
            : "Attach Local Avatar (Space / Right A)";
        if (GUILayout.Button(attachLabel, _buttonStyle, GUILayout.Height(56f)))
        {
            ToggleAttachment();
        }

        if (GUILayout.Button(_animateFrame ? "Pause Platform Motion (M)" : "Resume Platform Motion (M)", _buttonStyle, GUILayout.Height(56f)))
        {
            ToggleMotion();
        }

        GUILayout.EndArea();
    }

    public void RegisterReferenceFrame()
    {
        var manager = NetSyncManager.Instance;
        if (manager == null)
        {
            Debug.LogWarning("[ReferenceFrameSample] NetSyncManager is not available.");
            _isRegistered = false;
            UpdateStatusColor();
            return;
        }

        if (string.IsNullOrEmpty(_frameId) || _referenceFrame == null)
        {
            Debug.LogWarning("[ReferenceFrameSample] A frame id and reference frame Transform are required.");
            _isRegistered = false;
            UpdateStatusColor();
            return;
        }

        _isRegistered = manager.RegisterReferenceFrame(_frameId, _referenceFrame);
        Debug.Log("[ReferenceFrameSample] RegisterReferenceFrame(" + _frameId + ") => " + _isRegistered);
        UpdateStatusColor();
    }

    public void AttachLocalAvatar()
    {
        if (!_isRegistered)
        {
            RegisterReferenceFrame();
        }

        var manager = NetSyncManager.Instance;
        if (manager == null || !_isRegistered)
        {
            _isAttached = false;
            UpdateStatusColor();
            return;
        }

        _isAttached = manager.AttachLocalAvatarToReferenceFrame(_frameId);
        Debug.Log("[ReferenceFrameSample] AttachLocalAvatarToReferenceFrame(" + _frameId + ") => " + _isAttached);
        UpdateStatusColor();
    }

    public void DetachLocalAvatar()
    {
        var manager = NetSyncManager.Instance;
        if (manager != null)
        {
            manager.DetachLocalAvatarFromReferenceFrame();
        }

        _isAttached = false;
        Debug.Log("[ReferenceFrameSample] Detached local avatar from reference frame.");
        UpdateStatusColor();
    }

    public void ToggleAttachment()
    {
        if (_isAttached)
        {
            DetachLocalAvatar();
        }
        else
        {
            AttachLocalAvatar();
        }
    }

    public void ToggleMotion()
    {
        _animateFrame = !_animateFrame;
    }

    private void CacheRiderFramePose()
    {
        _hasRiderFramePose = false;
        if (_referenceFrame == null || _riderRoot == null)
        {
            return;
        }

        _riderFrameLocalPosition = _referenceFrame.InverseTransformPoint(_riderRoot.position);
        _riderFrameLocalRotation = Quaternion.Inverse(_referenceFrame.rotation) * _riderRoot.rotation;
        _hasRiderFramePose = true;
    }

    private void UpdateReferenceFrameMotion()
    {
        if (!_animateFrame || _referenceFrame == null)
        {
            return;
        }

        float phase = Time.timeSinceLevelLoad / _motionPeriodSeconds * Mathf.PI * 2f;
        Vector3 offset = new Vector3(
            Mathf.Sin(phase * 0.5f) * _motionAmplitude.x,
            Mathf.Sin(phase) * _motionAmplitude.y,
            Mathf.Sin(phase * 0.75f) * _motionAmplitude.z);
        float yaw = Mathf.Sin(phase * 0.5f) * _yawAmplitudeDegrees;

        _referenceFrame.localPosition = _initialFrameLocalPosition + offset;
        _referenceFrame.localRotation = _initialFrameLocalRotation * Quaternion.Euler(0f, yaw, 0f);
    }

    private void UpdateRiderRootPose()
    {
        if (!_hasRiderFramePose || _referenceFrame == null || _riderRoot == null)
        {
            return;
        }

        _riderRoot.position = _referenceFrame.TransformPoint(_riderFrameLocalPosition);
        _riderRoot.rotation = _referenceFrame.rotation * _riderFrameLocalRotation;
    }

    private void UpdateStatusColor()
    {
        if (_statusRenderer == null)
        {
            return;
        }

        Color color = Color.red;
        if (_isAttached)
        {
            color = Color.green;
        }
        else if (_isRegistered)
        {
            color = Color.yellow;
        }

        _statusRenderer.material.color = color;
    }

    private bool GetRightControllerAButtonDown()
    {
        if (!_enableRightControllerAButton)
        {
            _wasRightControllerAPressed = false;
            return false;
        }

        bool isPressed = false;
        InputDevice rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (rightHand.isValid)
        {
            rightHand.TryGetFeatureValue(CommonUsages.primaryButton, out isPressed);
        }

        bool pressedThisFrame = isPressed && !_wasRightControllerAPressed;
        _wasRightControllerAPressed = isPressed;
        return pressedThisFrame;
    }

    private void EnsureGuiStyles()
    {
        if (_boxStyle != null)
        {
            return;
        }

        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(20, 20, 18, 18)
        };
        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 30,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 24,
            normal = { textColor = Color.white }
        };
        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 24,
            fontStyle = FontStyle.Bold
        };
    }
}
