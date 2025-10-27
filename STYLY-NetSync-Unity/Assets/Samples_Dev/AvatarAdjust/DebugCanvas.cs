using UnityEngine;

namespace Styly.NetSync.AvatarAdjust
{
    public class DebugCanvas : MonoBehaviour
    {
        void Start()
        {
            // UnityEditorのみで表示
            gameObject.SetActive(Application.isEditor);
        }

        private const string VRMode = "VRMode";
        private bool isVrMode = false;
        public void ToggleVRMode()
        {
            isVrMode = !isVrMode;
            NetSyncManager.Instance.SetGlobalVariable(VRMode, isVrMode.ToString());
        }
        
    }
}
