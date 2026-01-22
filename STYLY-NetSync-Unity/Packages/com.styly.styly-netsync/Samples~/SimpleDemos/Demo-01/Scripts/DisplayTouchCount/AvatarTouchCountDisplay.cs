using UnityEngine;
using Styly.NetSync;

/// <summary>
/// Displays the touch count for a specific avatar above their head.
/// This script should be attached to a TextMesh child object of the avatar's head.
/// </summary>
[RequireComponent(typeof(TextMesh))]
public class AvatarTouchCountDisplay : MonoBehaviour
{
    [Header("Display Settings")]
    [SerializeField] private string _prefix = "";
    [SerializeField] private bool _billboardToCamera = true;
    [SerializeField] private Vector3 _offset = new Vector3(0, 0.3f, 0);

    private TextMesh _textMesh;
    private Camera _mainCamera;
    private NetSyncAvatar _avatar;
    private Transform _headTransform;

    void Start()
    {
        _textMesh = GetComponent<TextMesh>();
        _mainCamera = Camera.main;

        // Find the NetSyncAvatar component in parent hierarchy
        _avatar = GetComponentInParent<NetSyncAvatar>();

        if (_avatar != null && _avatar._head != null)
        {
            _headTransform = _avatar._head;
        }

        UpdateDisplay();
    }

    void Update()
    {
        // Position above the head
        if (_headTransform != null)
        {
            transform.position = _headTransform.position + _offset;
        }

        // Billboard effect: always face the camera
        if (_billboardToCamera && _mainCamera != null)
        {
            transform.rotation = _mainCamera.transform.rotation;
        }

        UpdateDisplay();
    }

    void UpdateDisplay()
    {
        if (_avatar == null || NetSyncManager.Instance == null || !NetSyncManager.Instance.IsReady)
        {
            _textMesh.text = "";
            return;
        }

        // Get touch count for this avatar's client number
        string touchCount = _avatar.GetClientVariable("Touch Count", "0");
        _textMesh.text = _prefix + touchCount;
    }
}
