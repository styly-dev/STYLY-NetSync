// PlatformBindingTestController.cs
using UnityEngine;
using UnityEngine.UI;

namespace Styly.NetSync.Samples.PlatformBindingTest
{
    /// <summary>
    /// Issue #425 sample. Two elevators are always in the scene and both move
    /// in lockstep when 発信 is pressed. The local user steps onto an elevator
    /// (handled by <see cref="ElevatorBoardingZone"/>) — only riders are carried
    /// by the elevator. To compare with vs. without the platform-binding fix:
    ///
    ///   Both clients ride Elevator_Fixed → AttachLocalAvatarToPlatform is in effect
    ///                                       and remote avatars stay glued.
    ///   Both clients ride Elevator_Raw   → no Attach call, legacy lag is visible.
    ///
    /// Offline mode shows nothing — the fix only changes how a *remote* avatar is
    /// reconstructed, so at least two clients on the same room are required.
    /// </summary>
    public class PlatformBindingTestController : MonoBehaviour
    {
        // One-shot motion start broadcast. Implemented as an RPC (not a network
        // variable) because NV values persist on the server across sessions —
        // a stale trigger value would replay on the next initial sync and start
        // the elevator immediately on scene play.
        public const string RpcStartMotion = "PlatformBindingTest.StartMotion";

        [Header("Scene References")]
        [SerializeField] private GameObject _elevatorFixed;
        [SerializeField] private GameObject _elevatorRaw;
        [SerializeField] private Button _sendButton;
        [SerializeField] private Text _statusLabel;

        private ElevatorBoardingZone _fixedZone;
        private ElevatorBoardingZone _rawZone;

        private void Start()
        {
            var netsync = NetSyncManager.Instance;
            if (netsync == null)
            {
                UnityEngine.Debug.LogError("[PlatformBindingTest] NetSyncManager.Instance is null at Start.");
                if (_statusLabel != null) { _statusLabel.text = "ERROR: NetSyncManager missing"; }
                return;
            }

            netsync.OnRPCReceived.AddListener(HandleRpcReceived);
            if (_sendButton != null) { _sendButton.onClick.AddListener(BroadcastStart); }

            if (_elevatorFixed != null) { _fixedZone = _elevatorFixed.GetComponentInChildren<ElevatorBoardingZone>(); }
            if (_elevatorRaw != null) { _rawZone = _elevatorRaw.GetComponentInChildren<ElevatorBoardingZone>(); }

            RefreshBoardingStatus();
        }

        private void Update()
        {
            // Reflect boarding state every frame so the user can see whether
            // their local rig is currently riding an elevator (and which one).
            RefreshBoardingStatus();
        }

        private void RefreshBoardingStatus()
        {
            string text;
            if (_fixedZone != null && _fixedZone.IsBoarded)
            {
                text = $"<color=#7FFF7F>● Riding</color> {_fixedZone.ElevatorRoot.name} — binding <b>ON</b> (fix applied)";
            }
            else if (_rawZone != null && _rawZone.IsBoarded)
            {
                text = $"<color=#FF7F7F>● Riding</color> {_rawZone.ElevatorRoot.name} — binding <b>OFF</b> (legacy / control)";
            }
            else
            {
                text = "<color=#CCCCCC>○ Not riding</color> — walk onto an elevator (WASD/E) then press 発信.";
            }
            if (_statusLabel != null) { _statusLabel.text = text; }
        }

        private void OnDestroy()
        {
            var netsync = NetSyncManager.Instance;
            if (netsync != null)
            {
                netsync.OnRPCReceived.RemoveListener(HandleRpcReceived);
            }
            if (_sendButton != null) { _sendButton.onClick.RemoveListener(BroadcastStart); }
        }

        private void HandleRpcReceived(int senderClientNo, string functionName, string[] args)
        {
            if (functionName == RpcStartMotion) { TriggerLocalMotion(); }
        }

        private void BroadcastStart()
        {
            // Send to other clients; RPCs are not echoed back, so trigger locally too.
            NetSyncManager.Instance.Rpc(RpcStartMotion);
            TriggerLocalMotion();
        }

        private void TriggerLocalMotion()
        {
            var startAt = Time.time;
            StartElevator(_elevatorFixed, startAt);
            StartElevator(_elevatorRaw, startAt);
            UnityEngine.Debug.Log("[PlatformBindingTest] Motion started.");
        }

        private static void StartElevator(GameObject elevator, float startAt)
        {
            if (elevator == null) { return; }
            var motion = elevator.GetComponent<ElevatorMotion>();
            if (motion == null) { return; }
            if (motion.IsMoving) { return; }
            motion.StartMotion(startAt);
        }

    }
}
