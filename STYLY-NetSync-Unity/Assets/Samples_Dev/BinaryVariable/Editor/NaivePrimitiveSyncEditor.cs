using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Samples.BinaryVariable.Editor
{
    [CustomEditor(typeof(NaivePrimitiveSync))]
    public class NaivePrimitiveSyncEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Send All"))
                {
                    ((NaivePrimitiveSync)target).SendAll();
                }
            }
        }
    }
}
