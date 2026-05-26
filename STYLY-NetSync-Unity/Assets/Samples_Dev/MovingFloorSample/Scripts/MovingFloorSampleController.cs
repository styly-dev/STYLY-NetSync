using Styly.NetSync;
using UnityEngine;
using UnityEngine.XR;

public class MovingFloorSampleController : MonoBehaviour
{
    [Header("Moving Floor")]
    [SerializeField] private NetSyncMovingFloor _movingFloor;
    [SerializeField] private Transform _movingFloorTransform;

    [Header("Motion")]
    [SerializeField] private bool _animateFloor = true;
    [SerializeField] private Vector3 _motionAmplitude = new Vector3(0f, 0.75f, 2.5f);
    [SerializeField, Min(0.1f)] private float _motionPeriodSeconds = 8f;
    [SerializeField] private float _yawAmplitudeDegrees = 18f;

    [Header("Status")]
    [SerializeField] private Renderer _statusRenderer;
    [SerializeField] private KeyCode _toggleBoardingKey = KeyCode.Space;
    [SerializeField] private KeyCode _toggleMotionKey = KeyCode.M;
    [SerializeField] private bool _enableRightControllerAButton = true;

    private Vector3 _initialFloorLocalPosition;
    private Quaternion _initialFloorLocalRotation;
    private bool _wasRightControllerAPressed;
    private GUIStyle _boxStyle;
    private GUIStyle _titleStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _buttonStyle;

    private void Awake()
    {
        ResolveMovingFloor();
        if (_movingFloorTransform != null)
        {
            _initialFloorLocalPosition = _movingFloorTransform.localPosition;
            _initialFloorLocalRotation = _movingFloorTransform.localRotation;
        }
    }

    private void Start()
    {
        EnsureMovingFloor();
        UpdateStatusColor();
    }

    private void OnDisable()
    {
        if (_movingFloor != null && _movingFloor.IsLocalAvatarOnFloor)
        {
            _movingFloor.LeaveLocalAvatar();
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(_toggleBoardingKey) || GetRightControllerAButtonDown())
        {
            ToggleBoarding();
        }

        if (Input.GetKeyDown(_toggleMotionKey))
        {
            ToggleMotion();
        }
    }

    private void LateUpdate()
    {
        UpdateMovingFloorMotion();
    }

    private void OnGUI()
    {
        EnsureGuiStyles();

        const int width = 620;
        const int height = 300;
        GUILayout.BeginArea(new Rect(24, 24, width, height), _boxStyle);
        GUILayout.Label("Moving Floor Sample", _titleStyle);
        GUILayout.Space(8f);
        GUILayout.Label("Floor ID: " + GetFloorIdLabel(), _labelStyle);
        GUILayout.Label("Boarding: " + (IsOnFloor() ? "On Floor" : "Off Floor"), _labelStyle);
        GUILayout.Label("Motion: " + (_animateFloor ? "Running" : "Paused"), _labelStyle);
        GUILayout.Space(12f);

        string boardingLabel = IsOnFloor()
            ? "Leave Moving Floor (Space / Right A)"
            : "Board Moving Floor (Space / Right A)";
        if (GUILayout.Button(boardingLabel, _buttonStyle, GUILayout.Height(56f)))
        {
            ToggleBoarding();
        }

        if (GUILayout.Button(_animateFloor ? "Pause Floor Motion (M)" : "Resume Floor Motion (M)", _buttonStyle, GUILayout.Height(56f)))
        {
            ToggleMotion();
        }

        GUILayout.EndArea();
    }

    public void BoardLocalAvatar()
    {
        if (!EnsureMovingFloor())
        {
            UpdateStatusColor();
            return;
        }

        bool boarded = _movingFloor.BoardLocalAvatar();
        Debug.Log("[MovingFloorSample] BoardLocalAvatar(" + _movingFloor.FloorId + ") => " + boarded);
        UpdateStatusColor();
    }

    public void LeaveLocalAvatar()
    {
        if (_movingFloor != null)
        {
            _movingFloor.LeaveLocalAvatar();
        }

        Debug.Log("[MovingFloorSample] Local avatar left moving floor.");
        UpdateStatusColor();
    }

    public void ToggleBoarding()
    {
        if (IsOnFloor())
        {
            LeaveLocalAvatar();
        }
        else
        {
            BoardLocalAvatar();
        }
    }

    public void ToggleMotion()
    {
        _animateFloor = !_animateFloor;
    }

    private bool EnsureMovingFloor()
    {
        ResolveMovingFloor();
        if (_movingFloor == null)
        {
            if (_movingFloorTransform != null)
            {
                _movingFloor = _movingFloorTransform.GetComponent<NetSyncMovingFloor>();
            }
        }

        if (_movingFloor == null)
        {
            Debug.LogWarning("[MovingFloorSample] NetSyncMovingFloor is required on the moving floor.", this);
            return false;
        }

        ResolveMovingFloor();
        return true;
    }

    private void ResolveMovingFloor()
    {
        if (_movingFloorTransform == null && _movingFloor != null)
        {
            _movingFloorTransform = _movingFloor.transform;
        }

        if (_movingFloor == null && _movingFloorTransform != null)
        {
            _movingFloor = _movingFloorTransform.GetComponent<NetSyncMovingFloor>();
        }
    }

    private bool IsOnFloor()
    {
        return _movingFloor != null && _movingFloor.IsLocalAvatarOnFloor;
    }

    private string GetFloorIdLabel()
    {
        if (_movingFloor != null && !string.IsNullOrEmpty(_movingFloor.FloorId))
        {
            return _movingFloor.FloorId;
        }

        return "";
    }

    private void UpdateMovingFloorMotion()
    {
        if (!_animateFloor || _movingFloorTransform == null)
        {
            return;
        }

        float phase = Time.timeSinceLevelLoad / _motionPeriodSeconds * Mathf.PI * 2f;
        Vector3 offset = new Vector3(
            Mathf.Sin(phase * 0.5f) * _motionAmplitude.x,
            Mathf.Sin(phase) * _motionAmplitude.y,
            Mathf.Sin(phase * 0.75f) * _motionAmplitude.z);
        float yaw = Mathf.Sin(phase * 0.5f) * _yawAmplitudeDegrees;

        _movingFloorTransform.localPosition = _initialFloorLocalPosition + offset;
        _movingFloorTransform.localRotation = _initialFloorLocalRotation * Quaternion.Euler(0f, yaw, 0f);
    }

    private void UpdateStatusColor()
    {
        if (_statusRenderer == null)
        {
            return;
        }

        Color color = Color.red;
        if (IsOnFloor())
        {
            color = Color.green;
        }
        else if (_movingFloor != null)
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
