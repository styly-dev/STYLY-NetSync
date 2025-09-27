using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Editor
{
    [CustomEditor(typeof(NetSyncManager))]
    [CanEditMultipleObjects]
    public class NetSyncManagerEditor : UnityEditor.Editor
    {
        private static bool showAdvanced;
        private static readonly string[] AdvancedPropertyOrder =
        {
            "BeaconPort",
            "_syncBatteryLevel"
        };
        private static readonly HashSet<string> AdvancedProperties = new HashSet<string>(AdvancedPropertyOrder);

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            var advancedPropertyPaths = new List<string>();

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;

                if (iterator.propertyPath == "m_Script")
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.PropertyField(iterator, true);
                    }
                    continue;
                }

                if (AdvancedProperties.Contains(iterator.propertyPath))
                {
                    advancedPropertyPaths.Add(iterator.propertyPath);
                    continue;
                }

                if (iterator.propertyPath == "_enableDiscovery")
                {
                    continue;
                }

                bool isTargetField = iterator.propertyPath == "_serverAddress" || iterator.propertyPath == "_roomId";
                bool disable = EditorApplication.isPlaying && isTargetField;

                using (new EditorGUI.DisabledScope(disable))
                {
                    EditorGUILayout.PropertyField(iterator, true);
                }
            }

            DrawAdvancedSection(advancedPropertyPaths);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawAdvancedSection(List<string> advancedPropertyPaths)
        {
            if (advancedPropertyPaths.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                showAdvanced = EditorGUILayout.BeginFoldoutHeaderGroup(showAdvanced, "Advanced Options");
                if (showAdvanced)
                {
                    EditorGUI.indentLevel++;

                    foreach (var propertyName in AdvancedPropertyOrder)
                    {
                        if (!advancedPropertyPaths.Contains(propertyName))
                        {
                            continue;
                        }

                        var property = serializedObject.FindProperty(propertyName);
                        if (property != null)
                        {
                            EditorGUILayout.PropertyField(property, true);
                        }
                    }

                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.EndFoldoutHeaderGroup();
            }
        }
    }
}
