using UnityEngine;
using Styly.NetSync;

/// <summary>
/// Displays the total touch count from the global network variable.
/// Attach this to a TextMesh object placed below the Sphere Light.
/// </summary>
[RequireComponent(typeof(TextMesh))]
public class TotalTouchCountDisplay : MonoBehaviour
{
    [Header("Display Settings")]
    [SerializeField] private string _prefix = "Total: ";
    [SerializeField] private bool _billboardToCamera = true;

    private TextMesh _textMesh;
    private Camera _mainCamera;

    void Start()
    {
        _textMesh = GetComponent<TextMesh>();
        _mainCamera = Camera.main;

        // Set initial text
        UpdateDisplay();
    }

    void Update()
    {
        UpdateDisplay();

        // Billboard effect: always face the camera
        if (_billboardToCamera && _mainCamera != null)
        {
            transform.rotation = _mainCamera.transform.rotation;
        }
    }

    void UpdateDisplay()
    {
        if (NetSyncManager.Instance == null || !NetSyncManager.Instance.IsReady)
        {
            _textMesh.text = _prefix + "---";
            return;
        }

        string touchCount = NetSyncManager.Instance.GetGlobalVariable("Total Touch Count", "0");
        _textMesh.text = _prefix + touchCount;
    }
}
