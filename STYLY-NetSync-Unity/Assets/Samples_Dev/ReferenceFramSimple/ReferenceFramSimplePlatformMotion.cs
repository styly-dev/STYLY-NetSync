using UnityEngine;

public sealed class ReferenceFramSimplePlatformMotion : MonoBehaviour
{
    [SerializeField] private float _amplitude = 2f;
    [SerializeField, Min(0.1f)] private float _periodSeconds = 6f;

    private Vector3 _startPosition;

    private void Awake()
    {
        _startPosition = transform.position;
    }

    private void Update()
    {
        float phase = Time.timeSinceLevelLoad / _periodSeconds * Mathf.PI * 2f;
        transform.position = _startPosition + Vector3.right * (Mathf.Sin(phase) * _amplitude);
    }
}
