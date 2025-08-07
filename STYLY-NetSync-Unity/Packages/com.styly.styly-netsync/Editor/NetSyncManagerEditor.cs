using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Styly.NetSync.Editor
{
    [CustomEditor(typeof(NetSyncManager))]
    public class NetSyncManagerEditor : UnityEditor.Editor
    {
        private NetSyncManager _netSyncManager;
        private bool _showGlobalVariables = true;
        
        private void OnEnable()
        {
            _netSyncManager = (NetSyncManager)target;
        }
        
        public override void OnInspectorGUI()
        {
            // Draw default inspector
            DrawDefaultInspector();
            
            // Only show network variables during play mode
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
                
                var globalVars = _netSyncManager.GetAllGlobalVariables();
                
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
            
            // Force repaint to show real-time updates
            if (Application.isPlaying && Event.current.type == EventType.Layout)
            {
                Repaint();
            }
        }
    }
}