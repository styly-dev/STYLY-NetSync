using System.Collections.Generic;
using Styly.NetSync;
using UnityEngine;

public sealed class ReferenceFramSimpleBoardingZone : MonoBehaviour
{
    [SerializeField] private NetSyncRideFrame _rideFrame;
    [SerializeField] private BoxCollider _boardingZone;

    private readonly HashSet<Collider> _localAvatarColliders = new HashSet<Collider>();

    private void Awake()
    {
        if (_boardingZone == null)
        {
            _boardingZone = GetComponent<BoxCollider>();
        }

        if (_boardingZone != null)
        {
            _boardingZone.isTrigger = true;
        }
    }

    private void OnDisable()
    {
        _localAvatarColliders.Clear();

        if (_rideFrame != null && _rideFrame.IsLocalAvatarAttached)
        {
            _rideFrame.DetachLocalAvatar();
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsLocalAvatarCollider(other))
        {
            return;
        }

        bool wasEmpty = _localAvatarColliders.Count == 0;
        _localAvatarColliders.Add(other);
        if (wasEmpty && _localAvatarColliders.Count > 0 && _rideFrame != null)
        {
            _rideFrame.AttachLocalAvatar();
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsLocalAvatarCollider(other))
        {
            return;
        }

        _localAvatarColliders.Remove(other);
        if (_localAvatarColliders.Count == 0 && _rideFrame != null)
        {
            _rideFrame.DetachLocalAvatar();
        }
    }

    private static bool IsLocalAvatarCollider(Collider other)
    {
        if (other == null)
        {
            return false;
        }

        var avatar = other.GetComponentInParent<NetSyncAvatar>();
        if (avatar == null)
        {
            return false;
        }

        return avatar.IsLocalAvatar;
    }
}
