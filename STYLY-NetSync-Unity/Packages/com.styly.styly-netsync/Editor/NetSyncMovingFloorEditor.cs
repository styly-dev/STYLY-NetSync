using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Styly.NetSync.Editor
{
    [CustomEditor(typeof(NetSyncMovingFloor))]
    [CanEditMultipleObjects]
    public class NetSyncMovingFloorEditor : UnityEditor.Editor
    {
        private NetSyncMovingFloor _movingFloor;

        private static readonly GUIContent s_ManualToggleLabel = new GUIContent(
            "Manual Floor ID",
            "When enabled, the Floor ID is user-specified and the auto-assign pipeline leaves it alone. Use this to match the same logical moving floor across separate scenes.");

        private static readonly GUIContent s_FloorIdLabel = new GUIContent(
            "Floor ID",
            "32-bit identifier used to match this moving floor across clients. Auto-derived from Unity's GlobalObjectId in the editor, or user-specified when Manual Floor ID is on. 0 means unassigned. Shown in hex.");

        private void OnEnable()
        {
            _movingFloor = (NetSyncMovingFloor)target;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var floorIdProp = serializedObject.FindProperty("_floorId");
            var manualProp = serializedObject.FindProperty("_manualFloorId");
            DrawFloorIdSection(floorIdProp, manualProp);

            DrawPropertiesExcluding(serializedObject, "m_Script", "_floorId", "_manualFloorId");

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawFloorIdSection(SerializedProperty floorIdProp, SerializedProperty manualProp)
        {
            if (floorIdProp == null || manualProp == null) return;

            EditorGUILayout.LabelField("Moving Floor", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            bool newManual = EditorGUILayout.Toggle(s_ManualToggleLabel, manualProp.boolValue);
            if (EditorGUI.EndChangeCheck() && newManual != manualProp.boolValue)
            {
                manualProp.boolValue = newManual;
                if (!newManual)
                {
                    floorIdProp.longValue = 0L;
                }
            }

            uint currentId = unchecked((uint)floorIdProp.longValue);

            if (manualProp.boolValue && !manualProp.hasMultipleDifferentValues)
            {
                DrawManualIdField(floorIdProp, currentId);
                DrawManualModeHelpBoxes(currentId);
            }
            else
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    string display = manualProp.hasMultipleDifferentValues
                        ? "-"
                        : MovingFloorManager.FormatFloorId(currentId);
                    EditorGUILayout.TextField(s_FloorIdLabel, display);
                }
            }

            EditorGUILayout.Space();
        }

        private void DrawManualIdField(SerializedProperty floorIdProp, uint currentId)
        {
            bool multi = serializedObject.isEditingMultipleObjects;
            using (new EditorGUI.DisabledScope(multi))
            {
                string initial = multi ? "-" : $"0x{currentId:X8}";
                EditorGUI.BeginChangeCheck();
                string entered = EditorGUILayout.DelayedTextField(s_FloorIdLabel, initial);
                if (EditorGUI.EndChangeCheck() && !multi)
                {
                    if (TryParseHexUInt(entered, out uint parsed))
                    {
                        if (parsed == 0u)
                        {
                            Debug.LogWarning("[NetSyncMovingFloor] Manual Floor ID cannot be 0. Value unchanged.");
                        }
                        else if (parsed != currentId)
                        {
                            floorIdProp.longValue = parsed;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[NetSyncMovingFloor] Could not parse '{entered}' as a hex Floor ID. Value unchanged.");
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
                    "Another loaded NetSyncMovingFloor uses this ID. If this is intentional cross-scene matching, ignore this warning.",
                    MessageType.Warning);
            }
        }

        private bool HasCollisionInLoadedScenes(uint id)
        {
            var all = Object.FindObjectsByType<NetSyncMovingFloor>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                var other = all[i];
                if (other == null) continue;
                if (other == _movingFloor) continue;
                if (other.FloorIdEditorOnly == id) return true;
            }
            return false;
        }

        private static bool TryParseHexUInt(string input, out uint value)
        {
            value = 0u;
            if (string.IsNullOrWhiteSpace(input)) return false;
            string s = input.Trim();
            if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("#", System.StringComparison.Ordinal))
            {
                s = s.Substring(s[0] == '#' ? 1 : 2);
            }
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }
}
