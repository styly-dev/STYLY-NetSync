using System.Collections.Generic;
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

        // UnityEventDrawer swallows the [Tooltip] on UnityEvent fields, so these
        // event properties are rendered manually and get an invisible GUI.Label
        // overlay on the header rect to restore hover tooltips.
        private static readonly HashSet<string> EventProperties = new HashSet<string>
        {
            "OnClientVariableChanged",
            "OnHandTrackingLost",
            "OnHandTrackingRestored"
        };

        // Group headers drawn before the mapped event. [Header] lives in the
        // runtime file normally, but a Header decorator shifts GetLastRect so
        // the tooltip overlay lands on the header label instead of the event
        // foldout — so we render the header here instead.
        private static readonly Dictionary<string, string> EventGroupHeaders = new Dictionary<string, string>
        {
            { "OnClientVariableChanged", "Network Variable Events" },
            { "OnHandTrackingLost", "Hand Tracking Events" }
        };

        private void OnEnable()
        {
            _netSyncAvatar = (NetSyncAvatar)target;
        }

        public override void OnInspectorGUI()
        {
            // Update serialized object to reflect runtime changes
            serializedObject.Update();

            DrawInspectorProperties();

            serializedObject.ApplyModifiedProperties();

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

        private void DrawInspectorProperties()
        {
            var iterator = serializedObject.GetIterator();
            bool enterChildren = true;

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

                if (EventProperties.Contains(iterator.propertyPath))
                {
                    if (EventGroupHeaders.TryGetValue(iterator.propertyPath, out var header))
                    {
                        EditorGUILayout.Space();
                        EditorGUILayout.LabelField(header, EditorStyles.boldLabel);
                    }
                    DrawEventWithTooltip(iterator);
                    continue;
                }

                EditorGUILayout.PropertyField(iterator, true);
            }
        }

        // Reserves the PropertyField's rect explicitly so we know its top
        // position, then draws the UnityEvent and overlays an invisible
        // GUI.Label on its foldout header row. GetLastRect is unreliable here
        // because an expanded UnityEvent's last sub-control is the "+/-" row
        // at the bottom, not the foldout header.
        private static void DrawEventWithTooltip(SerializedProperty property)
        {
            var height = EditorGUI.GetPropertyHeight(property, true);
            var rect = EditorGUILayout.GetControlRect(true, height);
            EditorGUI.PropertyField(rect, property, true);

            if (string.IsNullOrEmpty(property.tooltip))
            {
                return;
            }

            var headerRect = new Rect(
                rect.x,
                rect.y,
                rect.width,
                EditorGUIUtility.singleLineHeight);
            GUI.Label(headerRect, new GUIContent(string.Empty, property.tooltip));
        }
    }
}
