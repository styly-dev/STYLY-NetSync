using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Styly.NetSync.Samples.BinaryVariable
{
    /// <summary>
    /// Captures a screenshot and syncs it as a byte[] NetworkVariable.
    /// Displays received screenshots on a RawImage.
    /// </summary>
    public class ScreenshotSync : MonoBehaviour
    {
        private const string VariableName = "screenshot";
        private const int MaxBytes = 65536;
        private const int MaxResolution = 320;
        private const int JpegQuality = 50;

        [Header("UI")]
        [SerializeField] private RawImage _displayImage;
        [SerializeField] private Button _captureButton;

        private NetSyncManager _nsm;
        private bool _ready;
        private Texture2D _texture;

        private void Start()
        {
            _nsm = NetSyncManager.Instance;
            if (_nsm == null) return;

            _captureButton.onClick.AddListener(OnCaptureClicked);
            _nsm.OnReady.AddListener(OnReady);
            _nsm.OnGlobalBytesVariableChanged.AddListener(OnGlobalBytesVariableChanged);
        }

        private void OnDestroy()
        {
            if (_nsm == null) return;
            _nsm.OnReady.RemoveListener(OnReady);
            _nsm.OnGlobalBytesVariableChanged.RemoveListener(OnGlobalBytesVariableChanged);

            if (_texture != null) Destroy(_texture);
        }

        private void OnReady()
        {
            _ready = true;
            byte[] existing = _nsm.GetGlobalVariableBytes(VariableName);
            if (existing != null && existing.Length > 0)
            {
                ApplyImage(existing);
            }
        }

        private void OnCaptureClicked()
        {
            if (!_ready) return;
            StartCoroutine(CaptureAndSend());
        }

        private IEnumerator CaptureAndSend()
        {
            yield return new WaitForEndOfFrame();

            var screenTex = ScreenCapture.CaptureScreenshotAsTexture();
            var resized = Resize(screenTex, MaxResolution);
            Destroy(screenTex);

            byte[] jpg = resized.EncodeToJPG(JpegQuality);
            Destroy(resized);

            if (jpg.Length > MaxBytes)
            {
                UnityEngine.Debug.LogWarning($"[ScreenshotSync] Image too large ({jpg.Length} bytes), skipping send");
                yield break;
            }

            _nsm.SetGlobalVariable(VariableName, jpg);
        }

        private static Texture2D Resize(Texture2D source, int maxSide)
        {
            int w = source.width;
            int h = source.height;
            float scale = Mathf.Min(1f, (float)maxSide / Mathf.Max(w, h));
            int newW = Mathf.Max(1, Mathf.RoundToInt(w * scale));
            int newH = Mathf.Max(1, Mathf.RoundToInt(h * scale));

            var srcPixels = source.GetPixels();
            var dstPixels = new Color[newW * newH];
            for (int y = 0; y < newH; y++)
            {
                float v = (float)y / (newH - 1);
                int srcY = Mathf.Clamp(Mathf.RoundToInt(v * (h - 1)), 0, h - 1);
                for (int x = 0; x < newW; x++)
                {
                    float u = (float)x / (newW - 1);
                    int srcX = Mathf.Clamp(Mathf.RoundToInt(u * (w - 1)), 0, w - 1);
                    dstPixels[y * newW + x] = srcPixels[srcY * w + srcX];
                }
            }
            var resized = new Texture2D(newW, newH, TextureFormat.RGB24, false);
            resized.SetPixels(dstPixels);
            resized.Apply();

            return resized;
        }

        private void OnGlobalBytesVariableChanged(string name, byte[] oldValue, byte[] newValue)
        {
            if (name != VariableName) return;
            ApplyImage(newValue);
        }

        private void ApplyImage(byte[] pngData)
        {
            if (pngData == null || pngData.Length == 0) return;

            if (_texture == null)
            {
                _texture = new Texture2D(2, 2);
            }
            _texture.LoadImage(pngData);
            _displayImage.texture = _texture;
        }
    }
}
