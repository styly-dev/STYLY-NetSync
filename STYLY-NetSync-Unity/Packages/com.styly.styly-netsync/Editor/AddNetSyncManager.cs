using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Editor
{
    public static class AddNetSyncManager
    {
        // Path to the prefab within the package
        private static readonly string prefabPath = "Packages/com.styly.styly-netsync/Runtime/Prefabs/NetSyncManager.prefab";

        // Add a menu item in the hierarchy context menu
        [MenuItem("GameObject/STYLY NetSync/NetSyncManager", false, 10)]
        static void AddPackagePrefab(MenuCommand menuCommand)
        {
            // Load the prefab from the package
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogError("NetSyncManager Prefab not found at " + prefabPath);
                return;
            }

            // Instantiate the prefab
            GameObject prefabInstance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

            // Ensure it gets reparented if this was a context click (otherwise does nothing)
            GameObjectUtility.SetParentAndAlign(prefabInstance, menuCommand.context as GameObject);

            // Register the creation in the undo system
            Undo.RegisterCreatedObjectUndo(prefabInstance, "Create " + prefabInstance.name);

            // Select the new GameObject
            Selection.activeObject = prefabInstance;
        }
    }
}
