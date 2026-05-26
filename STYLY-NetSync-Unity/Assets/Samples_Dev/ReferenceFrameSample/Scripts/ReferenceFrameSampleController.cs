using Styly.NetSync;
using UnityEngine;
using UnityEngine.XR;

public class ReferenceFrameSampleController : MonoBehaviour
{
    [Header("Ride Frame")]
    [SerializeField] private NetSyncRideFrame _rideFrame;
    [SerializeField] private Transform _referenceFrame;

    [Header("Motion")]
    [SerializeField] private bool _animateFrame = true;
    [SerializeField] private Vector3 _motionAmplitude = new Vector3(0f, 0.75f, 2.5f);
    [SerializeField, Min(0.1f)] private float _motionPeriodSeconds = 8f;
    [SerializeField] private float _yawAmplitudeDegrees = 18f;

    [Header("Status")]
    [SerializeField] private Renderer _statusRenderer;
    [SerializeField] private KeyCode _toggleAttachKey = KeyCode.Space;
    [SerializeField] private KeyCode _toggleMotionKey = KeyCode.M;
    [SerializeField] private bool _enableRightControllerAButton = true;

    private Vector3 _initialFrameLocalPosition;
    private Quaternion _initialFrameLocalRotation;
    private bool _wasRightControllerAPressed;
    private GUIStyle _boxStyle;
    private GUIStyle _titleStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _buttonStyle;

    private void Awake()
    {
        ResolveReferenceFrame();
        if (_referenceFrame != null)
        {
            _initialFrameLocalPosition = _referenceFrame.localPosition;
            _initialFrameLocalRotation = _referenceFrame.localRotation;
        }
    }

    private void Start()
    {
        EnsureRideFrame();
        UpdateStatusColor();
    }

    private void OnDisable()
    {
        if (_rideFrame != null && _rideFrame.IsLocalAvatarAttached)
        {
            _rideFrame.DetachLocalAvatar();
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
    }

    private void OnGUI()
    {
        EnsureGuiStyles();

        const int width = 620;
        const int height = 300;
        GUILayout.BeginArea(new Rect(24, 24, width, height), _boxStyle);
        GUILayout.Label("Reference Frame Sample", _titleStyle);
        GUILayout.Space(8f);
        GUILayout.Label("Frame ID: " + GetFrameIdLabel(), _labelStyle);
        GUILayout.Label("Attachment: " + (IsAttached() ? "Attached" : "Detached"), _labelStyle);
        GUILayout.Label("Motion: " + (_animateFrame ? "Running" : "Paused"), _labelStyle);
        GUILayout.Space(12f);

        string attachLabel = IsAttached()
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

    public void AttachLocalAvatar()
    {
        if (!EnsureRideFrame())
        {
            UpdateStatusColor();
            return;
        }

        bool attached = _rideFrame.AttachLocalAvatar();
        Debug.Log("[ReferenceFrameSample] AttachLocalAvatar(" + _rideFrame.FrameId + ") => " + attached);
        UpdateStatusColor();
    }

    public void DetachLocalAvatar()
    {
        if (_rideFrame != null)
        {
            _rideFrame.DetachLocalAvatar();
        }

        Debug.Log("[ReferenceFrameSample] Detached local avatar from ride frame.");
        UpdateStatusColor();
    }

    public void ToggleAttachment()
    {
        if (IsAttached())
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

    private bool EnsureRideFrame()
    {
        ResolveReferenceFrame();
        if (_rideFrame == null)
        {
            if (_referenceFrame != null)
            {
                _rideFrame = _referenceFrame.GetComponent<NetSyncRideFrame>();
            }
        }

        if (_rideFrame == null)
        {
            Debug.LogWarning("[ReferenceFrameSample] NetSyncRideFrame is required on the moving platform.", this);
            return false;
        }

        ResolveReferenceFrame();
        return true;
    }

    private void ResolveReferenceFrame()
    {
        if (_referenceFrame == null && _rideFrame != null)
        {
            _referenceFrame = _rideFrame.transform;
        }

        if (_rideFrame == null && _referenceFrame != null)
        {
            _rideFrame = _referenceFrame.GetComponent<NetSyncRideFrame>();
        }
    }

    private bool IsAttached()
    {
        return _rideFrame != null && _rideFrame.IsLocalAvatarAttached;
    }

    private string GetFrameIdLabel()
    {
        if (_rideFrame != null && !string.IsNullOrEmpty(_rideFrame.FrameId))
        {
            return _rideFrame.FrameId;
        }

        return "";
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

    private void UpdateStatusColor()
    {
        if (_statusRenderer == null)
        {
            return;
        }

        Color color = Color.red;
        if (IsAttached())
        {
            color = Color.green;
        }
        else if (_rideFrame != null)
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
