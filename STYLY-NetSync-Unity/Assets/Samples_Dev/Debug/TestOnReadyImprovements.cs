using Styly.NetSync;
using UnityEngine;

/// <summary>
/// Test script to validate OnReady event improvements.
/// This script tests that:
/// 1. OnReady event fires after network variables are synced (or timeout)
/// 2. GetGlobalVariable throws exception before OnReady
/// 3. GetGlobalVariable works correctly after OnReady
/// </summary>
public class TestOnReadyImprovements : MonoBehaviour
{
    private bool _onReadyFired = false;
    private bool _hasTestedBeforeReady = false;
    private float _startTime;

    void Start()
    {
        _startTime = Time.time;
        Debug.Log("[TestOnReadyImprovements] Starting OnReady improvements test...");
        
        // Subscribe to OnReady event
        if (NetSyncManager.Instance != null)
        {
            NetSyncManager.Instance.OnReady.AddListener(OnReadyHandler);
            
            // Test calling GetGlobalVariable before OnReady
            TestGetVariableBeforeReady();
        }
        else
        {
            Debug.LogError("[TestOnReadyImprovements] NetSyncManager.Instance is null!");
        }
    }

    private void TestGetVariableBeforeReady()
    {
        if (_hasTestedBeforeReady) return;
        _hasTestedBeforeReady = true;
        
        try
        {
            string result = NetSyncManager.Instance.GetGlobalVariable("testVar", "default");
            Debug.LogError("[TestOnReadyImprovements] ERROR: GetGlobalVariable should have thrown exception before OnReady!");
        }
        catch (System.InvalidOperationException ex)
        {
            Debug.Log($"[TestOnReadyImprovements] ✓ Correctly threw exception before OnReady: {ex.Message}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[TestOnReadyImprovements] ERROR: Unexpected exception type: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnReadyHandler()
    {
        _onReadyFired = true;
        float timeToReady = Time.time - _startTime;
        Debug.Log($"[TestOnReadyImprovements] ✓ OnReady event fired after {timeToReady:F2} seconds!");
        
        // Test that GetGlobalVariable works after OnReady
        TestGetVariableAfterReady();
    }

    private void TestGetVariableAfterReady()
    {
        try
        {
            string result = NetSyncManager.Instance.GetGlobalVariable("testVar", "defaultValue");
            Debug.Log($"[TestOnReadyImprovements] ✓ GetGlobalVariable works after OnReady: {result}");
            
            // Test GetClientVariable as well
            string clientVar = NetSyncManager.Instance.GetClientVariable(1, "testClientVar", "defaultClientValue");
            Debug.Log($"[TestOnReadyImprovements] ✓ GetClientVariable works after OnReady: {clientVar}");
            
            Debug.Log("[TestOnReadyImprovements] All tests passed!");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[TestOnReadyImprovements] ERROR: GetGlobalVariable failed after OnReady: {ex.Message}");
        }
    }

    void Update()
    {
        // Periodically check ready state and test before ready if not done yet
        if (!_hasTestedBeforeReady && NetSyncManager.Instance != null)
        {
            TestGetVariableBeforeReady();
        }
        
        // Log status every few seconds
        if (Time.time > _startTime + 1f && !_onReadyFired)
        {
            if ((int)(Time.time - _startTime) % 5 == 0 && Time.time - (int)(Time.time) < 0.1f)
            {
                Debug.Log($"[TestOnReadyImprovements] Waiting for OnReady... ({Time.time - _startTime:F1}s elapsed)");
                if (NetSyncManager.Instance != null)
                {
                    Debug.Log($"  - HasServerConnection: {NetSyncManager.Instance.HasServerConnection}");
                    Debug.Log($"  - HasHandshake: {NetSyncManager.Instance.HasHandshake}");
                    Debug.Log($"  - HasNetworkVariablesSync: {NetSyncManager.Instance.HasNetworkVariablesSync}");
                    Debug.Log($"  - IsReady: {NetSyncManager.Instance.IsReady}");
                }
            }
        }
    }

    void OnDestroy()
    {
        if (NetSyncManager.Instance != null && NetSyncManager.Instance.OnReady != null)
        {
            NetSyncManager.Instance.OnReady.RemoveListener(OnReadyHandler);
        }
    }
}