using UnityEngine;
using UnityEditor;

namespace Styly.NetSync.Editor
{
    [CustomEditor(typeof(NetSyncAvatar))]
    public class NetSyncAvatarEditor : UnityEditor.Editor
    {
        private NetSyncAvatar _netSyncAvatar;
        private bool _showClientVariables = true;
        
        private void OnEnable()
        {
            _netSyncAvatar = (NetSyncAvatar)target;
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
            
            // Client Network Variables section
            _showClientVariables = EditorGUILayout.BeginFoldoutHeaderGroup(_showClientVariables, $"Client Network Variables (Client #{_netSyncAvatar.ClientNo})");
            if (_showClientVariables)
            {
                EditorGUI.indentLevel++;
                
                // Get client variables for this network object's client
                var clientVars = NetSyncManager.Instance != null ? NetSyncManager.Instance.GetAllClientVariables(_netSyncAvatar.ClientNo) : null;
                
                if (clientVars == null || clientVars.Count == 0)
                {
                    EditorGUILayout.HelpBox($"No client variables set for client #{_netSyncAvatar.ClientNo}", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    
                    foreach (var kvp in clientVars)
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