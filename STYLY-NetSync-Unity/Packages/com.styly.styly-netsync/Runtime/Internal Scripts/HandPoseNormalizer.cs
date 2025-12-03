// HandPoseNormalizer.cs - Follows HandVisualizer's wrist transform for cross-platform hand tracking
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

        private void TryFindActiveWristTransform()
        {
            // Try to find an active wrist transform (different platform mesh might be active)
            string wristName = _handedness == Handedness.Left ? "L_Wrist" : "R_Wrist";
            var allTransforms = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);

            foreach (var t in allTransforms)
            {
                if (t == null || t == _handTransform) continue;

                if (t.name == wristName && t.gameObject.activeInHierarchy && IsPartOfHandVisualizer(t))
                {
                    _handTransform = t;
                    if (_enableDebugLog)
                    {
                        Debug.Log($"[HandPoseNormalizer] Switched to active wrist transform: {GetFullPath(t)}");
                    }
                    return;
                }
            }
        }

        private void FindHandTransform()
        {
            // Search for HandVisualizer's wrist transform in the scene
            // The naming convention is "L_Wrist" or "R_Wrist"
            // HandVisualizer has multiple hand meshes for different platforms (MetaQuest, AndroidXR, etc.)
            // We need to find the one that is currently active

            string wristName = _handedness == Handedness.Left ? "L_Wrist" : "R_Wrist";

            var allTransforms = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
            Transform fallbackTransform = null;

            foreach (var t in allTransforms)
            {
                if (t == null) continue;

                // Check for exact match with L_Wrist or R_Wrist
                if (t.name == wristName)
                {
                    // Verify this is part of a hand visualization hierarchy (has XRHandSkeletonDriver in parent)
                    if (IsPartOfHandVisualizer(t))
                    {
                        // Prefer the active one (correct platform)
                        if (t.gameObject.activeInHierarchy)
                        {
                            _handTransform = t;
                            _isInitialized = true;
                            if (_enableDebugLog)
                            {
                                Debug.Log($"[HandPoseNormalizer] Found active wrist transform: {GetFullPath(t)}");
                            }
                            return;
                        }
                        else
                        {
                            // Keep as fallback in case no active one is found yet
                            fallbackTransform = t;
                        }
                    }
                }
            }

            // Use fallback if no active one found (hand tracking may not be active yet)
            if (fallbackTransform != null)
            {
                _handTransform = fallbackTransform;
                _isInitialized = true;
                if (_enableDebugLog)
                {
                    Debug.Log($"[HandPoseNormalizer] Found inactive wrist transform (will monitor): {GetFullPath(fallbackTransform)}");
                }
                return;
            }

            // Log warning only once
            if (!_hasLoggedWarning)
            {
                Debug.LogWarning($"[HandPoseNormalizer] Could not find {_handedness} wrist transform ({wristName}). Make sure HandVisualizer is in the scene.");
                _hasLoggedWarning = true;
            }
        }

        private bool IsPartOfHandVisualizer(Transform t)
        {
            // Walk up the hierarchy to find XRHandSkeletonDriver or HandVisualizer
            var current = t;
            int depth = 0;
            const int maxDepth = 10;

            while (current != null && depth < maxDepth)
            {
                // Check for XRHandSkeletonDriver component
                if (current.GetComponent<XRHandSkeletonDriver>() != null)
                {
                    return true;
                }

                // Check for object names that indicate hand visualizer
                string name = current.name.ToLower();
                if (name.Contains("handvisualizer") || name.Contains("hand visualizer"))
                {
                    return true;
                }

                current = current.parent;
                depth++;
            }

            return false;
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
