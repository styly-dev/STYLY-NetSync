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

            // Owner Client No: read-only runtime state. Shown always so the field
            // is discoverable in edit mode, but only meaningful during Play.
            // Guard against destroyed target (domain reload / exiting Play) to
            // avoid MissingReferenceException from a "fake null" target.
            if (_netSyncObject == null)
            {
                serializedObject.ApplyModifiedProperties();
                return;
            }
            int ownerClientNo = _netSyncObject.OwnerClientNo;
            string ownerLabel;
            if (!Application.isPlaying)
            {
                ownerLabel = "(runtime only)";
            }
            else if (ownerClientNo == 0)
            {
                ownerLabel = "None";
            }
            else if (_netSyncObject.IsOwnedByMe)
            {
                ownerLabel = $"Client #{ownerClientNo} (Me)";
            }
            else
            {
                ownerLabel = $"Client #{ownerClientNo}";
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Owner Client No", ownerLabel);
            }

            // Runtime-only ownership controls.
            if (Application.isPlaying)
            {
                using (new EditorGUI.DisabledScope(_netSyncObject.IsOwnedByMe))
                {
                    if (GUILayout.Button("Request Ownership"))
                    {
                        _netSyncObject.RequestOwnership();
                    }
                }
            }

            // Draw everything else (Events etc.) but skip the hidden _objectId field.
            DrawPropertiesExcluding(serializedObject, "m_Script", "_objectId");
            serializedObject.ApplyModifiedProperties();

            // Throttle inspector repaint during Play mode so the owner field stays live.
            if (Application.isPlaying && Event.current.type == EventType.Layout)
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
