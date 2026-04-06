using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Samples.BinaryVariable.Editor
{
    [CustomEditor(typeof(BinaryPrimitiveSync))]
    public class BinaryPrimitiveSyncEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Send"))
                {
                    ((BinaryPrimitiveSync)target).Send();
                }
            }
        }
    }
}
