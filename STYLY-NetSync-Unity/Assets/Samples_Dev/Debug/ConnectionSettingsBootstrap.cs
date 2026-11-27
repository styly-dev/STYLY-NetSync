using System.Collections;
using Styly.NetSync;
using UnityEngine;

namespace Styly.NetSync.Samples.Debugging
{
    /// <summary>
    /// Manual verification helper for configuring NetSyncManager from code
    /// (issue #537). Attach to any GameObject in the Debug scene.
    ///
    /// "When To Apply" picks the moment the checked values below are assigned
    /// to NetSyncManager. Each value is applied only when its "Apply ..."
    /// toggle is on; unchecked values keep what is set in the NetSyncManager
    /// Inspector. Results are written to the console as "[Bootstrap]" lines.
    /// </summary>
    public class ConnectionSettingsBootstrap : MonoBehaviour
    {
        public enum ApplyTiming
        {
            [InspectorName("Awake (before NetSyncManager's first Update)")]
            Awake,
            [InspectorName("Start (before NetSyncManager's first Update)")]
            Start,
            [InspectorName("Enable NetSyncManager after config (component must start disabled)")]
            EnableNetSyncManagerAfterConfig,
        }

        [Header("When To Apply")]
        [SerializeField, Tooltip("Awake/Start: assign the values from this component's Awake or Start, which run after NetSyncManager.Awake and before its first Update.\n" +
            "Enable NetSyncManager after config: NetSyncManager must be placed DISABLED in the scene. After the delay below the values are assigned and the component is enabled, so networking starts then.")]
        private ApplyTiming _whenToApply = ApplyTiming.Awake;
        [SerializeField, Min(0f), Tooltip("Seconds to wait before configuring and enabling NetSyncManager (only for 'Enable NetSyncManager after config').")]
        private float _enableDelaySeconds = 2f;

        [Header("Values To Apply (unchecked = keep NetSyncManager Inspector value)")]
        [SerializeField, Tooltip("Assign NetSyncManager.ServerAddress. Empty string enables auto-discovery.")]
        private bool _applyServerAddress = true;
        [SerializeField, Tooltip("Host or IP without tcp:// (e.g. localhost). Empty = auto-discovery.")]
        private string _serverAddress = "localhost";

        [SerializeField, Tooltip("Assign NetSyncManager.RoomId.")]
        private bool _applyRoomId = true;
        [SerializeField] private string _roomId = "bootstrap_room";

        [SerializeField, Tooltip("Assign NetSyncManager.OfflineMode.")]
        private bool _applyOfflineMode = false;
        [SerializeField] private bool _offlineMode = false;

        [SerializeField, Tooltip("Assign NetSyncManager.ServerDiscoveryPort (used when Server Address is empty).")]
        private bool _applyDiscoveryPort = false;
        [SerializeField, Min(1)] private int _discoveryPort = 49999;

        [Header("Extra Checks")]
        [SerializeField, Tooltip("From Start, call SetRoomId(Room Id + \"_x\") before the first connection. Expected: the first connection uses the new room without entering the room-switch path.")]
        private bool _callSetRoomIdInStart = false;
        [SerializeField, Tooltip("After OnReady, assign ServerAddress / RoomId / OfflineMode again. Expected: 3 errors are logged and the values stay unchanged.")]
        private bool _trySettersAfterReady = false;
        [SerializeField, Tooltip("Log NetSyncManager state every 3 seconds (3 times) and, in offline mode, probe that SetGlobalVariable still works.")]
        private bool _logPeriodicState = true;

        private NetSyncManager Manager => NetSyncManager.Instance;

        private void Awake()
        {
            Log($"Awake: whenToApply={_whenToApply}, Instance={(Manager != null ? "ok" : "null")}");
            if (Manager == null) { return; }

            bool wantsDisabledManager = _whenToApply == ApplyTiming.EnableNetSyncManagerAfterConfig;
            if (wantsDisabledManager && Manager.enabled)
            {
                LogWarning("'Enable NetSyncManager after config' expects NetSyncManager to be DISABLED in the scene, but it is enabled. Networking will start on its own before the values are applied.");
            }
            else if (!wantsDisabledManager && !Manager.enabled)
            {
                LogWarning("NetSyncManager is disabled in the scene; it will never connect. Enable it or use 'Enable NetSyncManager after config'.");
            }

            if (_whenToApply == ApplyTiming.Awake)
            {
                ApplyValues("Awake");
            }
            LogState("after Awake");
        }

        private void Start()
        {
            if (Manager == null) { return; }

            if (_whenToApply == ApplyTiming.Start)
            {
                ApplyValues("Start");
            }
            if (_callSetRoomIdInStart)
            {
                string room = _roomId + "_x";
                Log($"Start: calling SetRoomId(\"{room}\") before the first connection");
                Manager.SetRoomId(room);
            }
            if (_trySettersAfterReady)
            {
                Manager.OnReady.AddListener(OnReadyTrySetters);
            }
            if (_whenToApply == ApplyTiming.EnableNetSyncManagerAfterConfig)
            {
                StartCoroutine(EnableAfterConfigRoutine());
            }
            LogState("after Start");
            if (_logPeriodicState)
            {
                StartCoroutine(ReportRoutine());
            }
        }

        /// <summary>Assigns every checked value to NetSyncManager.</summary>
        private void ApplyValues(string timing)
        {
            if (_applyServerAddress) { Manager.ServerAddress = _serverAddress; }
            if (_applyRoomId) { Manager.RoomId = _roomId; }
            if (_applyOfflineMode) { Manager.OfflineMode = _offlineMode; }
            if (_applyDiscoveryPort) { Manager.ServerDiscoveryPort = _discoveryPort; }
            Log($"{timing}: applied " +
                $"{(_applyServerAddress ? $"ServerAddress='{_serverAddress}' " : "")}" +
                $"{(_applyRoomId ? $"RoomId='{_roomId}' " : "")}" +
                $"{(_applyOfflineMode ? $"OfflineMode={_offlineMode} " : "")}" +
                $"{(_applyDiscoveryPort ? $"ServerDiscoveryPort={_discoveryPort} " : "")}".TrimEnd());
        }

        private IEnumerator EnableAfterConfigRoutine()
        {
            yield return new WaitForSeconds(_enableDelaySeconds);
            Log($"EnableAfterConfig: NetSyncManager.enabled={Manager.enabled} before configuring");
            ApplyValues("EnableAfterConfig");
            Manager.enabled = true;
            LogState("EnableAfterConfig: NetSyncManager enabled");
        }

        private void OnReadyTrySetters()
        {
            Log("OnReady fired - assigning setters again (expect 3 errors, values unchanged)");
            Manager.ServerAddress = "10.0.0.1";
            Manager.RoomId = "should_not_apply";
            Manager.OfflineMode = !Manager.OfflineMode;
            LogState("after rejected setters");
        }

        private IEnumerator ReportRoutine()
        {
            for (int i = 1; i <= 3; i++)
            {
                yield return new WaitForSeconds(3f);
                LogState($"t={3 * i}s");
                if (Manager.OfflineMode)
                {
                    // Prove the NV path is alive (it would be frozen if _roomSwitching stayed true).
                    bool ok = Manager.SetGlobalVariable("bootstrap_probe", i.ToString());
                    Log($"SetGlobalVariable -> {ok}, read back = {Manager.GetGlobalVariable("bootstrap_probe")}");
                }
            }
        }

        private void LogState(string tag)
        {
            if (Manager == null) { Log($"{tag}: Instance is null"); return; }
            Log($"{tag}: ServerAddress='{Manager.ServerAddress}' RoomId='{Manager.RoomId}' " +
                $"OfflineMode={Manager.OfflineMode} ServerDiscoveryPort={Manager.ServerDiscoveryPort} " +
                $"HasServerConnection={Manager.HasServerConnection} ClientNo={Manager.ClientNo} IsReady={Manager.IsReady}");
        }

        private static void Log(string message) => UnityEngine.Debug.Log($"[Bootstrap] {message}");
        private static void LogWarning(string message) => UnityEngine.Debug.LogWarning($"[Bootstrap] {message}");
    }
}
