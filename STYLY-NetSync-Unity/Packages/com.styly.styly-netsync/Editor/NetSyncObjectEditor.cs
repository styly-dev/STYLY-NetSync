using System.Globalization;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Styly.NetSync.Editor
{
    [CustomEditor(typeof(NetSyncObject))]
    [CanEditMultipleObjects]
    public class NetSyncObjectEditor : UnityEditor.Editor
    {
        private NetSyncObject _netSyncObject;
        private double _lastRepaint;

        private static readonly GUIContent s_ManualToggleLabel = new GUIContent(
            "Manual Object ID",
            "When enabled, the Object ID is user-specified and the auto-assign pipeline leaves it alone. Use this to match the same logical entity across separate scenes (e.g., a player scene and an admin console scene).");

        private static readonly GUIContent s_ObjectIdLabel = new GUIContent(
            "Object ID",
            "32-bit identifier used on the wire to address this NetSyncObject. Auto-derived from Unity's GlobalObjectId in the editor, or user-specified when Manual Object ID is on. 0 means unassigned. Shown in hex.");

        private static readonly GUIContent s_OwnerLabel = new GUIContent(
            "Owner Client No",
            "Runtime-only field showing which client currently owns this object. 'None' means no owner yet; '(Me)' marks your own client. Only meaningful during Play mode.");

        private static readonly GUIContent s_OwnershipChangedLabel = new GUIContent("On Ownership Changed");

        // UnityEventDrawer swallows the header tooltip, so we overlay an empty
        // GUI.Label carrying only a tooltip on the event's header rect. No
        // visible element is added; the hover tooltip still fires.
        private static readonly GUIContent s_OwnershipChangedTooltip = new GUIContent(
            string.Empty,
            "Raised when ownership transfers. Args: (newOwnerClientNo, previousOwnerClientNo). 0 means no owner.");

        private void OnEnable()
        {
            _netSyncObject = (NetSyncObject)target;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var objectIdProp = serializedObject.FindProperty("_objectId");
            var manualProp = serializedObject.FindProperty("_manualObjectId");
            DrawObjectIdSection(objectIdProp, manualProp);

            // Owner Client No: read-only runtime state. Shown always so the field
            // is discoverable in edit mode, but only meaningful during Play.
            // Guard against destroyed target (domain reload / exiting Play) to
            // avoid MissingReferenceException from a "fake null" target.
            if (_netSyncObject == null)
            {
                serializedObject.ApplyModifiedProperties();
                return;
            }
            int ownerClientNo = _netSyncObject.OwnerClientNo;
            string ownerLabel;
            if (!Application.isPlaying)
            {
                ownerLabel = "(runtime only)";
            }
            else if (ownerClientNo == 0)
            {
                ownerLabel = "None";
            }
            else if (_netSyncObject.IsOwnedByMe)
            {
                ownerLabel = $"Client #{ownerClientNo} (Me)";
            }
            else
            {
                ownerLabel = $"Client #{ownerClientNo}";
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(s_OwnerLabel, ownerLabel);
            }

            // Runtime-only ownership controls.
            if (Application.isPlaying)
            {
                using (new EditorGUI.DisabledScope(_netSyncObject.IsOwnedByMe))
                {
                    if (GUILayout.Button("Request Ownership"))
                    {
                        _netSyncObject.RequestOwnership();
                    }
                }
            }

            // Draw everything else but skip the hidden fields and the event we render ourselves.
            DrawPropertiesExcluding(serializedObject, "m_Script", "_objectId", "_manualObjectId", "OnOwnershipChanged");

            // Re-draw the "Events" header (lost because [Header] only renders
            // under the default drawer) and the event. Overlay an invisible
            // tooltip label on the header row so hover reveals the arg order.
            var ownershipEventProp = serializedObject.FindProperty("OnOwnershipChanged");
            if (ownershipEventProp != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Events", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(ownershipEventProp, s_OwnershipChangedLabel, true);
                var eventRect = GUILayoutUtility.GetLastRect();
                var headerRect = new Rect(
                    eventRect.x,
                    eventRect.y,
                    eventRect.width,
                    EditorGUIUtility.singleLineHeight);
                GUI.Label(headerRect, s_OwnershipChangedTooltip);
            }

            serializedObject.ApplyModifiedProperties();

            // Throttle inspector repaint during Play mode so the owner field stays live.
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

        // ObjectId: auto-assigned 32-bit value derived from GlobalObjectId, or
        // user-specified when "Manual Object ID" is toggled on. Manual mode lets
        // the same logical entity share an ID across separate scenes.
        private void DrawObjectIdSection(SerializedProperty objectIdProp, SerializedProperty manualProp)
        {
            if (objectIdProp == null || manualProp == null) return;

            EditorGUI.BeginChangeCheck();
            bool newManual = EditorGUILayout.Toggle(s_ManualToggleLabel, manualProp.boolValue);
            if (EditorGUI.EndChangeCheck() && newManual != manualProp.boolValue)
            {
                // Auto → Manual: keep the currently-visible ID as the starting
                // value so the user's manual field is pre-populated.
                // Manual → Auto: clear the ID so the next reconcile pass probes
                // a fresh natural hash rather than inheriting the manual value.
                manualProp.boolValue = newManual;
                if (!newManual)
                {
                    objectIdProp.longValue = 0L;
                }
            }

            uint currentId = unchecked((uint)objectIdProp.longValue);

            if (manualProp.boolValue && !manualProp.hasMultipleDifferentValues)
            {
                DrawManualIdField(objectIdProp, currentId);
                DrawManualModeHelpBoxes(currentId);
            }
            else
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    string display = manualProp.hasMultipleDifferentValues
                        ? "—"
                        : (currentId == 0u ? "(unassigned)" : $"0x{currentId:X8}");
                    EditorGUILayout.TextField(s_ObjectIdLabel, display);
                }
            }
        }

        private void DrawManualIdField(SerializedProperty objectIdProp, uint currentId)
        {
            bool multi = serializedObject.isEditingMultipleObjects;
            using (new EditorGUI.DisabledScope(multi))
            {
                string initial = multi ? "—" : $"0x{currentId:X8}";
                EditorGUI.BeginChangeCheck();
                string entered = EditorGUILayout.DelayedTextField(s_ObjectIdLabel, initial);
                if (EditorGUI.EndChangeCheck() && !multi)
                {
                    if (TryParseHexUInt(entered, out uint parsed))
                    {
                        if (parsed == 0u)
                        {
                            Debug.LogWarning("[NetSyncObject] Manual Object ID cannot be 0. Value unchanged.");
                        }
                        else if (parsed != currentId)
                        {
                            objectIdProp.longValue = parsed;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[NetSyncObject] Could not parse '{entered}' as a hex Object ID. Value unchanged.");
                    }
                }
            }
        }

        private void DrawManualModeHelpBoxes(uint currentId)
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                EditorGUILayout.HelpBox(
                    "Manual IDs on a prefab asset are shared by all instances. For per-instance IDs, set the manual ID as a prefab instance override in the scene.",
                    MessageType.Info);
            }

            if (currentId != 0u && !serializedObject.isEditingMultipleObjects && HasCollisionInLoadedScenes(currentId))
            {
                EditorGUILayout.HelpBox(
                    "Another loaded NetSyncObject uses this ID. If this is intentional cross-scene matching, ignore this warning.",
                    MessageType.Warning);
            }
        }

        private bool HasCollisionInLoadedScenes(uint id)
        {
            var all = Object.FindObjectsByType<NetSyncObject>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                var other = all[i];
                if (other == null) continue;
                if (other == _netSyncObject) continue;
                if (other.ObjectIdEditorOnly == id) return true;
            }
            return false;
        }

        private static bool TryParseHexUInt(string input, out uint value)
        {
            value = 0u;
            if (string.IsNullOrWhiteSpace(input)) return false;
            string s = input.Trim();
            if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("#"))
            {
                s = s.Substring(s[0] == '#' ? 1 : 2);
            }
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }
}
