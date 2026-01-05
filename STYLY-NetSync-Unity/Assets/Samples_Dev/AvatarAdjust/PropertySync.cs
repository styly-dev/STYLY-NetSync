using UnityEngine;

namespace Styly.NetSync.AvatarAdjust
{
    public class PropertySync : MonoBehaviour
    {
        [SerializeField]
        private bool isSender = false;

        [SerializeField] private Transform head;
        [SerializeField] private Transform body;
        [SerializeField] private BodyTransformSolver bodyTransformSolver;
        
        const string VarName_HeadScale = "HeadScale";
        const string VarName_BodyScale = "BodyScale";
        const string VarName_BodyOffsetY = "BodyOffsetY";
        
        void Start()
        {
            NetSyncManager.Instance.OnGlobalVariableChanged.AddListener(OnGlobalVariableChanged);
        }

        void OnGlobalVariableChanged(string name, string oldValue, string newValue)
        {
            if (isSender)
            {
                return;
            }

            switch (name)
            {
                case VarName_HeadScale:
                {
                    var scale = float.Parse(newValue);
                    head.localScale = new Vector3(scale, scale, scale);
                    break;
                }
                case VarName_BodyScale:
                {
                    var scale = float.Parse(newValue);
                    body.localScale = new Vector3(scale, scale, scale);
                    break;
                }
                case VarName_BodyOffsetY:
                {
                    var offsetY = float.Parse(newValue);
                    bodyTransformSolver.offsetY = offsetY;
                    break;
                }
            }
        }
        
        void Update()
        {
            if (!isSender)
            {
                return;
            }

            // 値が変わらなければ実際に送信されずネットワーク負荷がかからない
            NetSyncManager.Instance.SetGlobalVariable(VarName_HeadScale, head.localScale.x.ToString());
            NetSyncManager.Instance.SetGlobalVariable(VarName_BodyScale, body.localScale.x.ToString());
            NetSyncManager.Instance.SetGlobalVariable(VarName_BodyOffsetY, bodyTransformSolver.offsetY.ToString());
        }
    }
}
