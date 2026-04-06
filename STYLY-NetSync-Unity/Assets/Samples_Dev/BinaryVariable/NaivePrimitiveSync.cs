using UnityEngine;

namespace Styly.NetSync.Samples.BinaryVariable
{
    /// <summary>
    /// Naive approach: syncs each primitive's size and color as individual string NetworkVariables.
    /// This requires 6 separate variables (size_0, color_0, size_1, color_1, size_2, color_2).
    /// </summary>
    public class NaivePrimitiveSync : MonoBehaviour
    {
        [Header("Primitives")]
        [SerializeField] private Transform _primitive0;
        [SerializeField] private Transform _primitive1;
        [SerializeField] private Transform _primitive2;

        [Header("Parameters")]
        [SerializeField] private float _size0 = 1f;
        [SerializeField] private Color _color0 = Color.red;
        [SerializeField] private float _size1 = 1f;
        [SerializeField] private Color _color1 = Color.green;
        [SerializeField] private float _size2 = 1f;
        [SerializeField] private Color _color2 = Color.blue;

        private NetSyncManager _nsm;
        private bool _ready;

        private void Start()
        {
            _nsm = NetSyncManager.Instance;
            if (_nsm == null) return;

            _nsm.OnReady.AddListener(OnReady);
            _nsm.OnGlobalVariableChanged.AddListener(OnGlobalVariableChanged);
        }

        private void OnDestroy()
        {
            if (_nsm == null) return;
            _nsm.OnReady.RemoveListener(OnReady);
            _nsm.OnGlobalVariableChanged.RemoveListener(OnGlobalVariableChanged);
        }

        private void OnReady()
        {
            _ready = true;
            // Apply initial values from network if they exist
            ApplyFromNetwork();
        }

        [ContextMenu("Send All")]
        public void SendAll()
        {
            if (!_ready) return;

            _nsm.SetGlobalVariable("naive_size_0", _size0.ToString("R"));
            _nsm.SetGlobalVariable("naive_color_0", ColorUtility.ToHtmlStringRGBA(_color0));
            _nsm.SetGlobalVariable("naive_size_1", _size1.ToString("R"));
            _nsm.SetGlobalVariable("naive_color_1", ColorUtility.ToHtmlStringRGBA(_color1));
            _nsm.SetGlobalVariable("naive_size_2", _size2.ToString("R"));
            _nsm.SetGlobalVariable("naive_color_2", ColorUtility.ToHtmlStringRGBA(_color2));
        }

        private void OnGlobalVariableChanged(string name, string oldValue, string newValue)
        {
            switch (name)
            {
                case "naive_size_0":
                    if (float.TryParse(newValue, out float s0)) SetSize(_primitive0, s0);
                    break;
                case "naive_color_0":
                    if (ColorUtility.TryParseHtmlString("#" + newValue, out Color c0)) SetColor(_primitive0, c0);
                    break;
                case "naive_size_1":
                    if (float.TryParse(newValue, out float s1)) SetSize(_primitive1, s1);
                    break;
                case "naive_color_1":
                    if (ColorUtility.TryParseHtmlString("#" + newValue, out Color c1)) SetColor(_primitive1, c1);
                    break;
                case "naive_size_2":
                    if (float.TryParse(newValue, out float s2)) SetSize(_primitive2, s2);
                    break;
                case "naive_color_2":
                    if (ColorUtility.TryParseHtmlString("#" + newValue, out Color c2)) SetColor(_primitive2, c2);
                    break;
            }
        }

        private void ApplyFromNetwork()
        {
            string s;

            s = _nsm.GetGlobalVariable("naive_size_0");
            if (s != null && float.TryParse(s, out float s0)) SetSize(_primitive0, s0);
            s = _nsm.GetGlobalVariable("naive_color_0");
            if (s != null && ColorUtility.TryParseHtmlString("#" + s, out Color c0)) SetColor(_primitive0, c0);

            s = _nsm.GetGlobalVariable("naive_size_1");
            if (s != null && float.TryParse(s, out float s1)) SetSize(_primitive1, s1);
            s = _nsm.GetGlobalVariable("naive_color_1");
            if (s != null && ColorUtility.TryParseHtmlString("#" + s, out Color c1)) SetColor(_primitive1, c1);

            s = _nsm.GetGlobalVariable("naive_size_2");
            if (s != null && float.TryParse(s, out float s2)) SetSize(_primitive2, s2);
            s = _nsm.GetGlobalVariable("naive_color_2");
            if (s != null && ColorUtility.TryParseHtmlString("#" + s, out Color c2)) SetColor(_primitive2, c2);
        }

        private static void SetSize(Transform t, float size)
        {
            if (t != null) t.localScale = Vector3.one * size;
        }

        private static void SetColor(Transform t, Color color)
        {
            if (t == null) return;
            var renderer = t.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = color;
        }
    }
}
