using UnityEngine;

public class MouseGrabber : MonoBehaviour
{
    [SerializeField] private float _grabDistance = 10f;
    [SerializeField] private float _springForce = 20f;

    private GrabbableNetSyncObject _grabbed;
    private float _grabDepth;
    private Camera _camera;

    void Awake()
    {
        _camera = GetComponent<Camera>();
        if (_camera == null) _camera = Camera.main;
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            TryGrab();
        }

        if (Input.GetMouseButtonUp(0))
        {
            TryRelease();
        }

        if (_grabbed != null && _grabbed.IsGrabbed)
        {
            MoveGrabbed();
        }
    }

    private void TryGrab()
    {
        Ray ray = _camera.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, _grabDistance))
        {
            var grabbable = hit.collider.GetComponent<GrabbableNetSyncObject>();
            if (grabbable != null)
            {
                _grabbed = grabbable;
                _grabDepth = hit.distance;
                _grabbed.Grab();
            }
        }
    }

    private void TryRelease()
    {
        if (_grabbed != null)
        {
            _grabbed.Release();
            _grabbed = null;
        }
    }

    private void MoveGrabbed()
    {
        Ray ray = _camera.ScreenPointToRay(Input.mousePosition);
        Vector3 targetPos = ray.GetPoint(_grabDepth);

        Rigidbody rb = _grabbed.GetComponent<Rigidbody>();
        if (rb != null)
        {
            Vector3 delta = targetPos - rb.position;
            rb.linearVelocity = delta * _springForce;
        }
    }
}
