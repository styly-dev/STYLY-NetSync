using UnityEngine;
using UnityEditor;

namespace Styly.NetSync.Editor
{
    [CustomEditor(typeof(NetSyncAvatar))]
    public class NetSyncAvatarEditor : UnityEditor.Editor
    {
        private NetSyncAvatar _netSyncAvatar;
        private bool _showClientVariables = true;
        // Timestamp of the last inspector repaint. Used to throttle repaint calls in play mode.
        private double _lastRepaint;
        
        private void OnEnable()
        {
            _netSyncAvatar = (NetSyncAvatar)target;
        }
        
        public override void OnInspectorGUI()
        {
            // Update serialized object to reflect runtime changes
            serializedObject.Update();

            // Draw default inspector
            DrawDefaultInspector();

            // Only show network variables during play mode
            if (!Application.isPlaying)
            {
                return;
            }

            EditorGUILayout.Space();
            
            // Client Network Variables section
            int displayClientNo = _netSyncAvatar.ClientNo;
            if (displayClientNo == 0 && NetSyncManager.Instance != null)
            {
                // Fallback to NetSyncManager's client number early in play mode
                displayClientNo = NetSyncManager.Instance.ClientNo;
            }
            _showClientVariables = EditorGUILayout.BeginFoldoutHeaderGroup(_showClientVariables, $"Client Network Variables (Client #{displayClientNo})");
            if (_showClientVariables)
            {
                EditorGUI.indentLevel++;
                
                // Get client variables for this network object's client (with fallback)
                var clientVars = NetSyncManager.Instance != null ? NetSyncManager.Instance.GetAllClientVariables(displayClientNo) : null;
                
                if (clientVars == null || clientVars.Count == 0)
                {
                    EditorGUILayout.HelpBox($"No client variables set for client #{displayClientNo}", MessageType.Info);
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
    }
}
