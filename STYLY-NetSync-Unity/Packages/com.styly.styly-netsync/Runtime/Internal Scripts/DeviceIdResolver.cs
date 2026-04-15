// DeviceIdResolver.cs
using System;
using UnityEngine;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Resolves a stable device ID asynchronously.
    /// Attempts Device-ID-Provider first, falls back to SystemInfo.deviceUniqueIdentifier.
    /// On Android, handles the permission dialog without blocking the main thread.
    /// </summary>
    internal sealed class DeviceIdResolver
    {
        /// <summary>
        /// PlayerPrefs key to remember that the user denied the media permission.
        /// Cleared automatically on app reinstall (PlayerPrefs is wiped).
        /// </summary>
        private const string PermissionDeniedKey = "DeviceIdProvider_PermissionDenied";

        // Android permission strings required by Device-ID-Provider
        private const string PermissionReadExternalStorage = "android.permission.READ_EXTERNAL_STORAGE";
        private const string PermissionReadMediaImages = "android.permission.READ_MEDIA_IMAGES";

        /// <summary>
        /// True once the device ID has been resolved (either successfully or via fallback).
        /// </summary>
        public bool IsResolved { get; private set; }

        /// <summary>
        /// The resolved device ID. Only valid when <see cref="IsResolved"/> is true.
        /// </summary>
        public string DeviceId { get; private set; }

        private readonly bool _enableDebugLogs;

        // Thread-safe flags set by PermissionCallbacks (may fire on Java UI thread).
        // Actual Unity API work is deferred to the main thread via Tick().
        private volatile bool _permissionGranted;
        private volatile bool _permissionDenied;

        public DeviceIdResolver(bool enableDebugLogs)
        {
            _enableDebugLogs = enableDebugLogs;
        }

        /// <summary>
        /// Begin device ID resolution. May complete synchronously or asynchronously
        /// depending on platform and permission state.
        /// </summary>
        public void Resolve()
        {
            ResolveRuntime();
        }

        /// <summary>
        /// Must be called from Update() on the main thread.
        /// Completes deferred permission results that arrived from the Java UI thread.
        /// </summary>
        public void Tick()
        {
            if (_permissionGranted)
            {
                _permissionGranted = false;
                Log("Android: permission granted (deferred), calling Device-ID-Provider");
                ResolveWithDeviceIdProvider();
            }
            else if (_permissionDenied)
            {
                _permissionDenied = false;
                Log("Android: permission denied (deferred), saving denial and using fallback");
                PlayerPrefs.SetInt(PermissionDeniedKey, 1);
                PlayerPrefs.Save();
                ResolveWithFallback();
            }
        }

        private void ResolveRuntime()
        {
            // Android-specific: check permission state before calling Device-ID-Provider
            if (Application.platform == RuntimePlatform.Android)
            {
                ResolveAndroid();
                return;
            }

            // Non-Android platforms: try Device-ID-Provider synchronously (no permission needed)
            ResolveWithDeviceIdProvider();
        }

        private void ResolveAndroid()
        {
            // Determine which permission is needed based on Android SDK version
            string permission = GetRequiredAndroidPermission();
            if (permission == null)
            {
                // SDK < 29: Device-ID-Provider doesn't support this, use fallback
                Log("Android: SDK < 29, Device-ID-Provider not supported, using fallback");
                ResolveWithFallback();
                return;
            }

            // Check if permission is already granted (e.g., user granted it later in system settings)
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission))
            {
                // Clear any stale denial flag so we don't skip the provider path next time
                if (PlayerPrefs.GetInt(PermissionDeniedKey, 0) == 1)
                {
                    PlayerPrefs.DeleteKey(PermissionDeniedKey);
                    PlayerPrefs.Save();
                }
                Log("Android: permission already granted, calling Device-ID-Provider");
                ResolveWithDeviceIdProvider();
                return;
            }

            // If the user previously denied permission, skip straight to fallback
            // (don't show the dialog again until the app is reinstalled or permission is granted externally)
            if (PlayerPrefs.GetInt(PermissionDeniedKey, 0) == 1)
            {
                Log("Android: permission was previously denied, using fallback");
                ResolveWithFallback();
                return;
            }

            // Permission not yet granted — request it asynchronously (non-blocking).
            // Callbacks may fire on the Java UI thread, so we only set volatile flags here.
            // Actual Unity API work is deferred to Tick() on the main thread.
            Log($"Android: requesting permission {permission}");
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += OnPermissionGranted;
            callbacks.PermissionDenied += OnPermissionDenied;
            callbacks.PermissionDeniedAndDontAskAgain += OnPermissionDenied;
            UnityEngine.Android.Permission.RequestUserPermission(permission, callbacks);
        }

        private void OnPermissionGranted(string permission)
        {
            // Only set a flag — do NOT call Unity APIs here (may be off main thread)
            _permissionGranted = true;
        }

        private void OnPermissionDenied(string permission)
        {
            // Only set a flag — do NOT call Unity APIs here (may be off main thread)
            _permissionDenied = true;
        }

        /// <summary>
        /// Returns the Android permission string required by Device-ID-Provider,
        /// or null if the current SDK version is below 29 (unsupported).
        /// </summary>
        private static string GetRequiredAndroidPermission()
        {
            try
            {
                using (var versionClass = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    int sdk = versionClass.GetStatic<int>("SDK_INT");
                    if (sdk < 29) return null;
                    return sdk >= 33
                        ? PermissionReadMediaImages
                        : PermissionReadExternalStorage;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DeviceIdResolver] Failed to get Android SDK version: {ex.Message}");
                return null;
            }
        }

        private void ResolveWithDeviceIdProvider()
        {
            try
            {
                DeviceId = Styly.Device.DeviceIdProvider.GetDeviceID();
                Log($"Using Device-ID-Provider: {DeviceId}");
                IsResolved = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DeviceIdResolver] Device-ID-Provider failed ({ex.GetType().Name}: {ex.Message}), using fallback");
                ResolveWithFallback();
            }
        }

        private void ResolveWithFallback()
        {
            string deviceId = SystemInfo.deviceUniqueIdentifier;
            if (!string.IsNullOrEmpty(deviceId) && deviceId != SystemInfo.unsupportedIdentifier)
            {
                DeviceId = deviceId;
                Log($"Fallback: using SystemInfo.deviceUniqueIdentifier: {DeviceId}");
            }
            else
            {
                DeviceId = Guid.NewGuid().ToString();
                Log($"Fallback: SystemInfo.deviceUniqueIdentifier not available, using generated GUID: {DeviceId}");
            }
            IsResolved = true;
        }

        private void Log(string message)
        {
            if (_enableDebugLogs)
            {
                Debug.Log($"[DeviceIdResolver] {message}");
            }
        }
    }
}
