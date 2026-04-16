using UnityEngine;
using UnityEditor;

namespace Styly.NetSync.Editor
{
    [CustomEditor(typeof(NetSyncObject))]
    public class NetSyncObjectEditor : UnityEditor.Editor
    {
        private NetSyncObject _netSyncObject;
        private double _lastRepaint;

        private void OnEnable()
        {
            _netSyncObject = (NetSyncObject)target;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();

            if (!Application.isPlaying)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;

            int ownerClientNo = _netSyncObject.OwnerClientNo;
            string ownerLabel = ownerClientNo == 0 ? "0 (Unowned)" : ownerClientNo.ToString();
            if (_netSyncObject.IsOwnedByMe)
            {
                ownerLabel += " (Me)";
            }
            EditorGUILayout.LabelField("Owner ClientNo", ownerLabel);
            EditorGUILayout.LabelField("IsOwnedByMe", _netSyncObject.IsOwnedByMe.ToString());
            EditorGUILayout.LabelField("ObjectId", _netSyncObject.ObjectId);

            EditorGUI.indentLevel--;

            // Throttle inspector repaint during Play mode.
            if (Event.current.type == EventType.Layout)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastRepaint > 0.2d)
                {
                    Repaint();
                    _lastRepaint = now;
                }
            }
        }
    }
}
