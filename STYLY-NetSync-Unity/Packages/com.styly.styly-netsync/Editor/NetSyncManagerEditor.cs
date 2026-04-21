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
        private static readonly string[] AdvancedPropertyOrder =
        {
            "_offlineMode",
            "_transformSendRate",
            "_serverDiscoveryPort",
            "_syncBatteryLevel"
        };
        private static readonly HashSet<string> AdvancedProperties = new HashSet<string>(AdvancedPropertyOrder);

        // UnityEventDrawer swallows the [Tooltip] on UnityEvent fields, so these
        // event properties are rendered manually and get an invisible GUI.Label
        // overlay on the header rect to restore hover tooltips.
        private static readonly HashSet<string> EventProperties = new HashSet<string>
        {
            "OnAvatarConnected",
            "OnAvatarDisconnected",
            "OnRPCReceived",
            "OnGlobalVariableChanged",
            "OnClientVariableChanged",
            "OnReady",
            "OnVersionMismatch"
        };

        // Group headers drawn before the mapped event. [Header] lives in the
        // runtime file normally, but a Header decorator shifts GetLastRect so
        // the tooltip overlay lands on the header label instead of the event
        // foldout — so we render the header here instead.
        private static readonly Dictionary<string, string> EventGroupHeaders = new Dictionary<string, string>
        {
            { "OnAvatarConnected", "Events" }
        };

        public override bool RequiresConstantRepaint()
        {
            return Application.isPlaying;
        }

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

                    using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                    {
                        foreach (var propertyName in AdvancedPropertyOrder)
                        {
                            if (!advancedPropertyPaths.Contains(propertyName))
                            {
                                continue;
                            }

                            var property = serializedObject.FindProperty(propertyName);
                            if (property != null)
                            {
                                // Custom label for transform send rate
                                if (propertyName == "_transformSendRate")
                                {
                                    EditorGUILayout.PropertyField(property, new GUIContent("Transform Send Rate (Hz)", property.tooltip), true);
                                }
                                else
                                {
                                    EditorGUILayout.PropertyField(property, true);
                                }
                            }
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
