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
        _xrGrab = GetComponent<XRGrabInteractable>();
        ApplyKinematicForOwnership();
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

        // Re-evaluate on scene load / re-enable: HandleRoomObjects may have
        // already assigned an owner before our listener was registered.
        ApplyKinematicForOwnership();
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
        // XRGrabInteractable may have flipped isKinematic on release; re-apply
        // the ownership-derived state so it survives the XR toolkit override.
        ApplyKinematicForOwnership();
    }

    public void Grab()
    {
        _isGrabbed = true;
        _netSyncObject.RequestOwnership();
    }

    public void Release()
    {
        _isGrabbed = false;
    }

    private void OnOwnershipChanged(int newOwner, int previousOwner)
    {
        if (!_netSyncObject.IsOwnedByMe)
        {
            _isGrabbed = false;
        }
        ApplyKinematicForOwnership();
    }

    // Single source of truth for Rigidbody.isKinematic:
    //   OwnerClientNo == 0        -> dynamic (local physics, unowned)
    //   IsOwnedByMe               -> dynamic (local physics, I'm authority)
    //   someone else owns         -> kinematic (sync-driven)
    private void ApplyKinematicForOwnership()
    {
        bool shouldBeKinematic =
            _netSyncObject.OwnerClientNo != 0 && !_netSyncObject.IsOwnedByMe;

        if (shouldBeKinematic)
        {
            if (!_rb.isKinematic)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic = true;
            }
        }
        else if (_rb.isKinematic)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.isKinematic = false;
        }
    }
}
