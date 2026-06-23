// HandPoseNormalizer.cs - Drives the synced hand from the XRHandSubsystem wrist joint for cross-platform hand tracking
using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.Hands;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Component that synchronizes the hand pose using the XR Hands subsystem's normalized wrist joint
    /// (XRHandJointID.Wrist). Sourcing the pose from the subsystem keeps hand positioning consistent across
    /// VR platforms (Quest, Pico, etc.) and is rig-agnostic: it works whether the rig uses a HandVisualizer
    /// or skeleton-driven glove meshes (XRHandSkeletonDriver), and does not depend on a particular scene
    /// Transform exposing a rig-relative localPosition.
    ///
    /// A scene Transform named L_Wrist/R_Wrist (when present) is still used only as a hand-tracking
    /// active-state probe via activeInHierarchy; the synced position/rotation comes from the subsystem:
    /// - Hand tracking active: Disables TrackedPoseDriver and applies the subsystem wrist pose
    /// - Controller mode: Re-enables TrackedPoseDriver for controller-based tracking
    /// </summary>
    internal class HandPoseNormalizer : MonoBehaviour
    {
        /// <summary>
        /// Hand tracking state for state machine management.
        /// </summary>
        private enum TrackingState
        {
            /// <summary>Not yet initialized or no XRHandSubsystem available</summary>
            Unknown,
            /// <summary>Hand is being actively tracked</summary>
            Tracking,
            /// <summary>Waiting to confirm if tracking is truly lost (3-frame delay)</summary>
            PendingLostCheck,
            /// <summary>True tracking lost - maintaining head-relative position</summary>
            TrueLost,
            /// <summary>Controller mode - TrackedPoseDriver handles input</summary>
            ControllerMode
        }

        [SerializeField]
        private Handedness _handedness = Handedness.Right;

        [SerializeField]
        [Tooltip("Enable to output pose values to the console for debugging")]
        private bool _enableDebugLog = false;

        private Transform _handTransform;
        private Transform _selfTransform;
        private TrackedPoseDriver _trackedPoseDriver;
        private bool _isInitialized = false;
        private bool _hasLoggedWarning = false;
        private bool _trackedPoseDriverWasDisabled = false;

        // XRHandSubsystem reference
        private XRHandSubsystem _handSubsystem;

        // State machine for tracking state
        private TrackingState _trackingState = TrackingState.Unknown;
        private int _lostCheckFrameCount = 0;
        private const int LostCheckDelayFrames = 3; // Wait 3 frames before confirming lost

        // --- Pose-staleness lost detection ---
        // Some runtimes keep XRHand.isTracked == true while the wrist pose stops updating (the
        // subsystem returns the bit-identical pose every frame), and the joint trackingState does
        // NOT degrade in that case. The only reliable signal is pose staleness: when the wrist pose
        // is unchanged for PoseStaleSeconds while tracked, treat the hand as lost so the validity
        // flag clears and remote hands hide instead of freezing in place. A live tracked hand always
        // jitters, so an exactly-repeated pose sustained this long does not occur during genuine
        // tracking.
        private Vector3 _staleLastWristPos;
        private bool _staleHasLastWristPos = false;
        private float _staleUnchangedSince = 0f;
        private const float PoseStaleEpsilon = 1e-6f;  // only a bit-identical repeat stays under this
        private const float PoseStaleSeconds = 0.5f;   // sustained no-change before treating as lost

        // Head transform reference for maintaining relative position during lost state
        private Transform _headTransform;

        // Offset from head recorded when tracking is lost
        private Vector3 _lostPositionOffsetFromHead;
        private Quaternion _lostRotationOffsetFromHead;

        /// <summary>
        /// Event fired when hand tracking state changes (acquired or lost).
        /// Parameters: (Handedness handedness, bool isTracking)
        /// </summary>
        public event Action<Handedness, bool> OnTrackingStateChanged;

        /// <summary>
        /// Gets or sets which hand this normalizer is for.
        /// </summary>
        public Handedness Handedness
        {
            get => _handedness;
            set => _handedness = value;
        }

        /// <summary>
        /// Gets or sets whether debug logging is enabled.
        /// </summary>
        public bool EnableDebugLog
        {
            get => _enableDebugLog;
            set => _enableDebugLog = value;
        }

        /// <summary>
        /// Gets or sets the head transform for maintaining relative position during lost state.
        /// </summary>
        public Transform HeadTransform
        {
            get => _headTransform;
            set => _headTransform = value;
        }

        private void Start()
        {
            _selfTransform = transform;
            _trackedPoseDriver = GetComponent<TrackedPoseDriver>();
            FindHandTransform();
        }

        private void OnDisable()
        {
            // Re-enable TrackedPoseDriver if we disabled it
            if (_trackedPoseDriverWasDisabled && _trackedPoseDriver != null)
            {
                _trackedPoseDriver.enabled = true;
                _trackedPoseDriverWasDisabled = false;
                if (_enableDebugLog)
                {
                    Debug.Log($"[HandPoseNormalizer] Re-enabled TrackedPoseDriver for {_handedness}");
                }
            }
        }

        private void LateUpdate()
        {
            // Handle state-specific behavior
            switch (_trackingState)
            {
                case TrackingState.TrueLost:
                    // Maintain head-relative position
                    if (_headTransform != null)
                    {
                        _selfTransform.position = _headTransform.TransformPoint(_lostPositionOffsetFromHead);
                        _selfTransform.rotation = _headTransform.rotation * _lostRotationOffsetFromHead;
                    }
                    // Check for tracking restoration
                    UpdateHandTrackingState();
                    return;

                case TrackingState.ControllerMode:
                    // TrackedPoseDriver handles input, just check for state changes
                    UpdateHandTrackingState();
                    return;
            }

            // Try to find hand transform if not initialized
            if (!_isInitialized)
            {
                FindHandTransform();
                if (!_isInitialized)
                {
                    // Hand not found - TrackedPoseDriver handles tracking
                    return;
                }
            }

            // Check if hand transform is still valid
            if (_handTransform == null)
            {
                _isInitialized = false;
                EnableTrackedPoseDriver();
                return;
            }

            // Check if hand tracking is active. The matched wrist GameObject (HandVisualizer wrist or a
            // skeleton-driven glove bone) is active while hand tracking is on; used only as a state probe.
            bool isHandTrackingActive = _handTransform.gameObject.activeInHierarchy;

            if (isHandTrackingActive)
            {
                // Hand tracking mode: disable TrackedPoseDriver and apply the subsystem wrist pose.
                DisableTrackedPoseDriver();

                // Source the pose from the XRHandSubsystem (rig-agnostic) instead of copying the
                // matched scene Transform's localPosition. The matched Transform is used only as an
                // active-state probe above; its localPosition is a valid rig-relative hand pose for a
                // HandVisualizer wrist but a near-constant bone offset for skeleton-driven gloves.
                if (TryGetWristPose(out var wristPose))
                {
                    _selfTransform.localPosition = wristPose.position;
                    _selfTransform.localRotation = wristPose.rotation;
                }
                // If the wrist pose is momentarily unavailable, hold the last good pose.
            }
            else
            {
                // Wrist transform is inactive - might be wrong platform, try to find the correct one
                TryFindActiveWristTransform();

                // Controller mode: re-enable TrackedPoseDriver
                EnableTrackedPoseDriver();
            }

            // Update hand tracking state and fire events AFTER TrackedPoseDriver state is updated
            UpdateHandTrackingState();
        }

        /// <summary>
        /// Ensures a running XRHandSubsystem reference is cached.
        /// Returns true when a running subsystem is available.
        /// </summary>
        private bool EnsureHandSubsystem()
        {
            if (_handSubsystem == null || !_handSubsystem.running)
            {
                var subsystems = new List<XRHandSubsystem>();
                SubsystemManager.GetSubsystems(subsystems);
                _handSubsystem = null;
                foreach (var s in subsystems)
                {
                    if (s.running)
                    {
                        _handSubsystem = s;
                        break;
                    }
                }
            }

            return _handSubsystem != null && _handSubsystem.running;
        }

        /// <summary>
        /// Gets the wrist joint pose directly from the XRHandSubsystem.
        /// The pose is XROrigin-relative, which matches this transform's parent space (the avatar
        /// root tracks the XROrigin), so it is applied as localPosition/localRotation.
        ///
        /// This is rig-agnostic: it works whether the rig uses a HandVisualizer-style shallow wrist
        /// or skeleton-driven glove meshes (XRHandSkeletonDriver). It deliberately does NOT read the
        /// matched scene Transform's localPosition, which only represents a rig-relative hand pose for
        /// a HandVisualizer wrist and collapses to a near-constant bone offset for skeleton bones.
        /// </summary>
        private bool TryGetWristPose(out Pose pose)
        {
            pose = Pose.identity;

            if (!EnsureHandSubsystem())
            {
                return false;
            }

            var hand = _handedness == Handedness.Left
                ? _handSubsystem.leftHand
                : _handSubsystem.rightHand;
            if (!hand.isTracked)
            {
                return false;
            }

            return hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out pose);
        }

        /// <summary>
        /// Returns true when the wrist pose has been unchanged (stale) for at least PoseStaleSeconds
        /// while the hand is tracked. Some runtimes keep isTracked == true while the subsystem returns
        /// the bit-identical wrist pose every frame; that frozen source must be treated as not-tracked.
        /// Must be called once per frame with the current subsystem hand.
        /// </summary>
        private bool IsWristPoseStale(XRHand hand, bool rawTracked)
        {
            if (!rawTracked)
            {
                _staleHasLastWristPos = false;
                return false;
            }

            float now = Time.time;
            bool hasPose = hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out var wristPose);

            if (!_staleHasLastWristPos)
            {
                _staleHasLastWristPos = true;
                _staleLastWristPos = hasPose ? wristPose.position : Vector3.zero;
                _staleUnchangedSince = now;
                return false;
            }

            bool moved = hasPose && Vector3.Distance(wristPose.position, _staleLastWristPos) > PoseStaleEpsilon;
            if (moved)
            {
                _staleLastWristPos = wristPose.position;
                _staleUnchangedSince = now;
                return false;
            }

            // Pose unchanged (stale repeat) or no fresh pose while still tracked.
            return (now - _staleUnchangedSince) >= PoseStaleSeconds;
        }

        /// <summary>
        /// Updates hand tracking state from XRHandSubsystem using state machine.
        /// Uses delayed check for lost detection to distinguish controller switch from true lost.
        /// </summary>
        private void UpdateHandTrackingState()
        {
            // If no running XRHandSubsystem, reset to Unknown state
            if (!EnsureHandSubsystem())
            {
                if (_trackingState != TrackingState.Unknown && _trackingState != TrackingState.ControllerMode)
                {
                    _trackingState = TrackingState.Unknown;
                    _lostCheckFrameCount = 0;
                }
                // Re-init staleness on next available frame so a subsystem drop/reacquire can't be
                // misread as a stale (frozen) pose from before the gap.
                _staleHasLastWristPos = false;
                return;
            }

            // Get true tracking state from XRHandSubsystem
            var hand = _handedness == Handedness.Left
                ? _handSubsystem.leftHand
                : _handSubsystem.rightHand;
            bool rawTracked = hand.isTracked;
            // A frozen (stale) wrist pose while isTracked stays true is treated as not-tracked, so
            // the existing lost path fires and remote hands hide instead of freezing in place.
            bool poseStale = IsWristPoseStale(hand, rawTracked);
            bool isTracked = rawTracked && !poseStale;

            // State machine transitions
            switch (_trackingState)
            {
                case TrackingState.Unknown:
                case TrackingState.ControllerMode:
                    if (isTracked)
                    {
                        // Start tracking
                        if (_enableDebugLog)
                        {
                            Debug.Log($"[HandPoseNormalizer] {_handedness} tracking state changed: Acquired");
                        }
                        _trackingState = TrackingState.Tracking;
                        OnTrackingStateChanged?.Invoke(_handedness, true);
                    }
                    break;

                case TrackingState.Tracking:
                    if (!isTracked)
                    {
                        // Start delayed lost check
                        if (_enableDebugLog)
                        {
                            Debug.Log($"[HandPoseNormalizer] {_handedness} tracking lost - starting delayed check");
                        }
                        _trackingState = TrackingState.PendingLostCheck;
                        _lostCheckFrameCount = 0;
                    }
                    break;

                case TrackingState.PendingLostCheck:
                    _lostCheckFrameCount++;

                    if (isTracked)
                    {
                        // Tracking resumed - cancel lost check
                        if (_enableDebugLog)
                        {
                            Debug.Log($"[HandPoseNormalizer] {_handedness} tracking resumed - cancelling pending lost check");
                        }
                        _trackingState = TrackingState.Tracking;
                        _lostCheckFrameCount = 0;
                        OnTrackingStateChanged?.Invoke(_handedness, true);
                    }
                    else if (_lostCheckFrameCount >= LostCheckDelayFrames)
                    {
                        // Delay complete - determine if controller mode or true lost
                        _lostCheckFrameCount = 0;

                        if (_trackedPoseDriver != null && _trackedPoseDriver.enabled)
                        {
                            // Controller mode - suppress Lost event
                            if (_enableDebugLog)
                            {
                                Debug.Log($"[HandPoseNormalizer] {_handedness} tracking lost but TrackedPoseDriver is active - controller mode");
                            }
                            _trackingState = TrackingState.ControllerMode;
                        }
                        else
                        {
                            // True lost - record head-relative offset and fire event
                            if (_enableDebugLog)
                            {
                                Debug.Log($"[HandPoseNormalizer] {_handedness} tracking state changed: Lost (confirmed after delay)");
                            }

                            // Record current position relative to head
                            if (_headTransform != null)
                            {
                                _lostPositionOffsetFromHead = _headTransform.InverseTransformPoint(_selfTransform.position);
                                _lostRotationOffsetFromHead = Quaternion.Inverse(_headTransform.rotation) * _selfTransform.rotation;
                            }

                            _trackingState = TrackingState.TrueLost;
                            OnTrackingStateChanged?.Invoke(_handedness, false);
                        }
                    }
                    break;

                case TrackingState.TrueLost:
                    if (isTracked)
                    {
                        // Tracking restored
                        if (_enableDebugLog)
                        {
                            Debug.Log($"[HandPoseNormalizer] {_handedness} tracking state changed: Acquired");
                        }
                        _trackingState = TrackingState.Tracking;
                        OnTrackingStateChanged?.Invoke(_handedness, true);
                    }
                    break;
            }
        }

        private void DisableTrackedPoseDriver()
        {
            if (_trackedPoseDriver != null && _trackedPoseDriver.enabled)
            {
                _trackedPoseDriver.enabled = false;
                _trackedPoseDriverWasDisabled = true;
                if (_enableDebugLog)
                {
                    Debug.Log($"[HandPoseNormalizer] Hand tracking active, disabled TrackedPoseDriver for {_handedness}");
                }
            }
        }

        private void EnableTrackedPoseDriver()
        {
            if (_trackedPoseDriverWasDisabled && _trackedPoseDriver != null && !_trackedPoseDriver.enabled)
            {
                _trackedPoseDriver.enabled = true;
                _trackedPoseDriverWasDisabled = false;
                if (_enableDebugLog)
                {
                    Debug.Log($"[HandPoseNormalizer] Controller mode, re-enabled TrackedPoseDriver for {_handedness}");
                }
            }
        }

        private void TryFindActiveWristTransform()
        {
            // Try to find an active wrist transform under XROrigin (different platform mesh might be active)
            var xrOrigin = UnityEngine.Object.FindFirstObjectByType<XROrigin>();
            if (xrOrigin == null) return;

            string wristName = _handedness == Handedness.Left ? "L_Wrist" : "R_Wrist";
            var allTransforms = xrOrigin.GetComponentsInChildren<Transform>(true);

            foreach (var t in allTransforms)
            {
                if (t == _handTransform) continue;

                if (t.name == wristName && t.gameObject.activeInHierarchy)
                {
                    _handTransform = t;
                    if (_enableDebugLog)
                    {
                        Debug.Log($"[HandPoseNormalizer] Switched to active wrist: {GetFullPath(t)}");
                    }
                    return;
                }
            }
        }

        private void FindHandTransform()
        {
            // First, find XROrigin (local player's root)
            // This ensures we only find wrist transforms for the local player, not remote avatars
            var xrOrigin = UnityEngine.Object.FindFirstObjectByType<XROrigin>();
            if (xrOrigin == null)
            {
                if (!_hasLoggedWarning)
                {
                    Debug.LogWarning("[HandPoseNormalizer] XROrigin not found in scene.");
                    _hasLoggedWarning = true;
                }
                return;
            }

            // Search for wrist transform under XROrigin
            string wristName = _handedness == Handedness.Left ? "L_Wrist" : "R_Wrist";
            Transform fallbackTransform = null;

            // GetComponentsInChildren searches only within XROrigin hierarchy
            var allTransforms = xrOrigin.GetComponentsInChildren<Transform>(true); // includeInactive=true

            foreach (var t in allTransforms)
            {
                if (t.name == wristName)
                {
                    if (t.gameObject.activeInHierarchy)
                    {
                        _handTransform = t;
                        _isInitialized = true;
                        if (_enableDebugLog)
                        {
                            Debug.Log($"[HandPoseNormalizer] Found active wrist: {GetFullPath(t)}");
                        }
                        return;
                    }
                    else
                    {
                        fallbackTransform = t;
                    }
                }
            }

            if (fallbackTransform != null)
            {
                _handTransform = fallbackTransform;
                _isInitialized = true;
                if (_enableDebugLog)
                {
                    Debug.Log($"[HandPoseNormalizer] Found inactive wrist (will monitor): {GetFullPath(fallbackTransform)}");
                }
                return;
            }

            if (!_hasLoggedWarning)
            {
                Debug.LogWarning($"[HandPoseNormalizer] Could not find {wristName} under XROrigin.");
                _hasLoggedWarning = true;
            }
        }

        private string GetFullPath(Transform t)
        {
            string path = t.name;
            var current = t.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }
    }
}
