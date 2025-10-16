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
        private bool _showGlobalVariables = true;
        // Timestamp of the last inspector repaint. Used to throttle repaint calls in play mode.
        private double _lastRepaint;
        private static readonly string[] AdvancedPropertyOrder =
        {
            "ServerDiscoveryPort",
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
            
            DrawGlobalVariablesSection();

            serializedObject.ApplyModifiedProperties();
            
            // Throttle inspector repaint during Play mode to reduce CPU usage.
            // Repaint only once every ~0.2 seconds on Layout events.
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

        private void DrawGlobalVariablesSection()
        {
            // Only show global network variables during play mode
            if (!Application.isPlaying)
            {
                return;
            }
            
            EditorGUILayout.Space();
            
            // Global Network Variables section
            _showGlobalVariables = EditorGUILayout.BeginFoldoutHeaderGroup(_showGlobalVariables, "Global Network Variables");
            if (_showGlobalVariables)
            {
                EditorGUI.indentLevel++;
                
                // Get global variables from NetSyncManager instance
                var globalVars = NetSyncManager.Instance != null ? NetSyncManager.Instance.GetAllGlobalVariables() : null;
                
                if (globalVars == null || globalVars.Count == 0)
                {
                    EditorGUILayout.HelpBox("No global variables set", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    
                    foreach (var kvp in globalVars)
                    {
                        EditorGUILayout.BeginHorizontal();
                        
                        // Key
                        EditorGUILayout.LabelField(kvp.Key, GUILayout.Width(150));
                        
                        // Value
                        if (kvp.Value.Length > 50)
                        {
                            EditorGUILayout.EndHorizontal();
                            EditorGUI.indentLevel++;
                            EditorGUILayout.TextArea(kvp.Value, EditorStyles.wordWrappedLabel);
                            EditorGUI.indentLevel--;
                        }
                        else
                        {
                            EditorGUILayout.LabelField(kvp.Value, EditorStyles.wordWrappedLabel);
                            EditorGUILayout.EndHorizontal();
                        }
                        
                        EditorGUILayout.Space(2);
                    }
                    
                    EditorGUILayout.EndVertical();
                }
                
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }
    }
}
