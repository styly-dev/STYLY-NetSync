using UnityEngine;
using Styly.XRRig;

namespace Styly.NetSync.AvatarAdjust
{
    public class Debug : MonoBehaviour
    {
        [SerializeField] private Canvas _canvas;
        [SerializeField] private StylyXrRig _stylyXrRig;
        private const string VRMode = "VRMode";
        private bool _isVrMode = false;

        void Start()
        {
            // UnityEditorのみで表示
            _canvas.gameObject.SetActive(Application.isEditor);
            
            NetSyncManager.Instance.OnGlobalVariableChanged.AddListener((name, oldValue, newValue) =>
            {
                if (name == VRMode)
                {
                    _isVrMode = bool.Parse(newValue);
                    if (_isVrMode)
                    {
                        _stylyXrRig.SwitchToMR(); 
                    }
                    else
                    {
                        _stylyXrRig.SwitchToVR();
                    }
                }
            });
        }

        public void ToggleVRMode()
        {
            _isVrMode = !_isVrMode;
            NetSyncManager.Instance.SetGlobalVariable(VRMode, _isVrMode.ToString());
        }
        
    }
}
