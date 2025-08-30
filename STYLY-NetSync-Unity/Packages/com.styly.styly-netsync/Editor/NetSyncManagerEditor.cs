using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Editor
{
    [CustomEditor(typeof(NetSyncManager))]
    [CanEditMultipleObjects]
    public class NetSyncManagerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var iterator = serializedObject.GetIterator();
            bool enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;

                bool isTargetField = iterator.name == "_serverAddress" || iterator.name == "_roomId";
                bool disable = EditorApplication.isPlaying && isTargetField;

                using (new EditorGUI.DisabledScope(disable))
                {
                    EditorGUILayout.PropertyField(iterator, true);
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}

