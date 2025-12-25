// HandPoseNormalizer.cs - Follows HandVisualizer's wrist transform for cross-platform hand tracking
using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.Hands;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Component that synchronizes hand pose with the HandVisualizer's wrist transform (L_Wrist/R_Wrist).
    /// This approach ensures consistent hand positioning across different VR platforms (Quest, Pico, etc.)
    /// by using the XR Hands subsystem's normalized joint data instead of platform-specific InputAction bindings.
    ///
    /// Monitors the hand tracking state via activeInHierarchy:
    /// - Hand tracking active: Disables TrackedPoseDriver and copies pose from HandVisualizer
    /// - Controller mode: Re-enables TrackedPoseDriver for controller-based tracking
    /// </summary>
    internal class HandPoseNormalizer : MonoBehaviour
    {
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

        // XRHandSubsystem tracking state
        private XRHandSubsystem _handSubsystem;
        private bool _wasTracked = false;

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

            // Update XRHandSubsystem tracking state and fire events
            UpdateHandTrackingState();

            // Check if hand tracking is active (HandVisualizer activates the hand object when tracking)
            bool isHandTrackingActive = _handTransform.gameObject.activeInHierarchy;

            if (isHandTrackingActive)
            {
                // Hand tracking mode: disable TrackedPoseDriver and copy from HandVisualizer
                DisableTrackedPoseDriver();

                // Copy the hand transform's local position and rotation
                _selfTransform.localPosition = _handTransform.localPosition;
                _selfTransform.localRotation = _handTransform.localRotation;

                if (_enableDebugLog)
                {
                    Debug.Log($"[HandPoseNormalizer] {_handedness}: pos={_selfTransform.localPosition:F4}, rot={_selfTransform.localRotation.eulerAngles:F2}");
                }
            }
            else
            {
                // Wrist transform is inactive - might be wrong platform, try to find the correct one
                TryFindActiveWristTransform();

                // Controller mode: re-enable TrackedPoseDriver
                EnableTrackedPoseDriver();
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

        /// <summary>
        /// Updates hand tracking state from XRHandSubsystem and fires events on state change.
        /// This distinguishes true hand tracking loss from controller mode switch.
        /// </summary>
        private void UpdateHandTrackingState()
        {
            // Try to get XRHandSubsystem if not available or not running
            if (_handSubsystem == null || !_handSubsystem.running)
            {
                var subsystems = new List<XRHandSubsystem>();
                SubsystemManager.GetSubsystems(subsystems);
                foreach (var s in subsystems)
                {
                    if (s.running)
                    {
                        _handSubsystem = s;
                        break;
                    }
                }
            }

            // Get true tracking state from XRHandSubsystem
            bool isTracked = false;
            if (_handSubsystem != null && _handSubsystem.running)
            {
                var hand = _handedness == Handedness.Left
                    ? _handSubsystem.leftHand
                    : _handSubsystem.rightHand;
                isTracked = hand.isTracked;
            }

            // Detect state change and fire event
            if (_wasTracked != isTracked)
            {
                _wasTracked = isTracked;
                if (_enableDebugLog)
                {
                    Debug.Log($"[HandPoseNormalizer] {_handedness} tracking state changed: {(isTracked ? "Acquired" : "Lost")}");
                }
                OnTrackingStateChanged?.Invoke(_handedness, isTracked);
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
