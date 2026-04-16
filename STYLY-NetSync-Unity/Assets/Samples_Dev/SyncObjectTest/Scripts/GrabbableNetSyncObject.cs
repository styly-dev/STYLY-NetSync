using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Styly.NetSync;

[RequireComponent(typeof(NetSyncObject))]
[RequireComponent(typeof(Rigidbody))]
public class GrabbableNetSyncObject : MonoBehaviour
{
    [SerializeField, Tooltip("When true, this client claims ownership of objects left by disconnected clients.")]
    private bool _claimOnOwnerDisconnect;

    private NetSyncObject _netSyncObject;
    private Rigidbody _rb;
    private bool _isGrabbed;
    private XRGrabInteractable _xrGrab;

    public bool IsGrabbed => _isGrabbed;

    void Awake()
    {
        _netSyncObject = GetComponent<NetSyncObject>();
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;

        _xrGrab = GetComponent<XRGrabInteractable>();
    }

    void OnEnable()
    {
        _netSyncObject.OnOwnershipChanged.AddListener(OnOwnershipChanged);

        if (_xrGrab != null)
        {
            _xrGrab.selectEntered.AddListener(OnXRSelectEntered);
            _xrGrab.selectExited.AddListener(OnXRSelectExited);
        }

        var manager = NetSyncManager.Instance;
        if (manager != null)
        {
            manager.OnAvatarDisconnected.AddListener(OnAvatarDisconnected);
        }
    }

    void OnDisable()
    {
        _netSyncObject.OnOwnershipChanged.RemoveListener(OnOwnershipChanged);

        if (_xrGrab != null)
        {
            _xrGrab.selectEntered.RemoveListener(OnXRSelectEntered);
            _xrGrab.selectExited.RemoveListener(OnXRSelectExited);
        }

        var manager = NetSyncManager.Instance;
        if (manager != null)
        {
            manager.OnAvatarDisconnected.RemoveListener(OnAvatarDisconnected);
        }
    }

    private void OnAvatarDisconnected(int clientNo)
    {
        if (!_claimOnOwnerDisconnect) return;
        if (_netSyncObject.OwnerClientNo != clientNo) return;

        _netSyncObject.RequestOwnership();
    }

    private void OnXRSelectEntered(SelectEnterEventArgs args)
    {
        Grab();
    }

    private void OnXRSelectExited(SelectExitEventArgs args)
    {
        Release();
        // XRGrabInteractable restores isKinematic to its original state on release.
        // Override it so physics continues while we still own the object.
        if (_netSyncObject.IsOwnedByMe)
        {
            _rb.isKinematic = false;
        }
    }

    public void Grab()
    {
        _isGrabbed = true;
        _netSyncObject.RequestOwnership();
        _rb.isKinematic = false;
    }

    public void Release()
    {
        _isGrabbed = false;
    }

    private void OnOwnershipChanged(int newOwner, int previousOwner)
    {
        if (_netSyncObject.IsOwnedByMe)
        {
            _rb.isKinematic = false;
        }
        else
        {
            _isGrabbed = false;
            _rb.isKinematic = true;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
    }
}
