using UnityEngine;

namespace Styly.NetSync.Samples.BinaryVariable
{
    /// <summary>
    /// Binary approach: syncs all three primitives' parameters via a single byte[] NetworkVariable.
    /// A PrimitiveParams ScriptableObject is serialized/deserialized as a whole,
    /// replacing 6 individual string variables with 1 binary variable.
    /// </summary>
    public class BinaryPrimitiveSync : MonoBehaviour
    {
        private const string VariableName = "binary_primitives";

        [Header("Primitives")]
        [SerializeField] private Transform _primitive0;
        [SerializeField] private Transform _primitive1;
        [SerializeField] private Transform _primitive2;

        [Header("Parameters (edit and press Send)")]
        [SerializeField] private PrimitiveParams _params;

        private NetSyncManager _nsm;
        private bool _ready;

        private void Start()
        {
            _nsm = NetSyncManager.Instance;
            if (_nsm == null) return;

            if (_params == null)
            {
                _params = ScriptableObject.CreateInstance<PrimitiveParams>();
            }

            _nsm.OnReady.AddListener(OnReady);
            _nsm.OnGlobalBytesVariableChanged.AddListener(OnGlobalBytesVariableChanged);
        }

        private void OnDestroy()
        {
            if (_nsm == null) return;
            _nsm.OnReady.RemoveListener(OnReady);
            _nsm.OnGlobalBytesVariableChanged.RemoveListener(OnGlobalBytesVariableChanged);
        }

        private void OnReady()
        {
            _ready = true;
            // Apply initial values from network if they exist
            byte[] existing = _nsm.GetGlobalVariableBytes(VariableName);
            if (existing != null)
            {
                _params.Deserialize(existing);
                ApplyParams();
            }
        }

        [ContextMenu("Send")]
        public void Send()
        {
            if (!_ready) return;
            _nsm.SetGlobalVariable(VariableName, _params.Serialize());
        }

        private void OnGlobalBytesVariableChanged(string name, byte[] newValue, byte[] oldValue)
        {
            if (name != VariableName) return;
            _params.Deserialize(newValue);
            ApplyParams();
        }

        private void ApplyParams()
        {
            ApplyEntry(_primitive0, _params.primitive0);
            ApplyEntry(_primitive1, _params.primitive1);
            ApplyEntry(_primitive2, _params.primitive2);
        }

        private static void ApplyEntry(Transform t, PrimitiveParams.Entry entry)
        {
            if (t == null) return;
            t.localScale = Vector3.one * entry.size;
            var renderer = t.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = entry.color;
        }
    }
}
