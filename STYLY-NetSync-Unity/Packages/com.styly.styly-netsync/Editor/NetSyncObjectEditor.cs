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

            // ObjectId: auto-assigned 32-bit value derived from GlobalObjectId.
            // Displayed read-only as hex so users can verify uniqueness at a glance.
            var objectIdProp = serializedObject.FindProperty("_objectId");
            if (objectIdProp != null)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    uint currentId = unchecked((uint)objectIdProp.longValue);
                    EditorGUILayout.TextField("Object ID", currentId == 0u ? "(unassigned)" : $"0x{currentId:X8}");
                }
            }

            // Draw everything else (Events etc.) but skip the hidden _objectId field.
            DrawPropertiesExcluding(serializedObject, "m_Script", "_objectId");
            serializedObject.ApplyModifiedProperties();

            if (!Application.isPlaying)
            {
                return;
            }

            EditorGUILayout.Space();

            int ownerClientNo = _netSyncObject.OwnerClientNo;
            string ownerLabel = ownerClientNo == 0
                ? "None"
                : _netSyncObject.IsOwnedByMe
                    ? $"Client #{ownerClientNo} (Me)"
                    : $"Client #{ownerClientNo}";

            EditorGUILayout.LabelField("Owner Client No", ownerLabel);

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
