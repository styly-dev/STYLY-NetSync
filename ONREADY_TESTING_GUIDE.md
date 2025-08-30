# OnReady Event Improvements - Testing Guide

## Overview

This implementation improves the OnReady event to fire only after the module obtains initial network variables from the server, and adds error checking for network variable getter methods.

## How to Test

### 1. Unity Scene Setup

1. Open Unity (version 6000.0.48f1 or later)
2. Load the demo scene: `Assets/Samples_Dev/Debug/Debug Scene.unity`
3. Add the test script `TestOnReadyImprovements.cs` to a GameObject in the scene
4. Configure NetSyncManager with server address (default: localhost)

### 2. Server Setup

```bash
# Navigate to server directory
cd STYLY-NetSync-Server

# Install and run server
pip install -e .
styly-netsync-server --no-beacon
```

### 3. Test Scenarios

#### Scenario A: Empty Room (Tests Timeout Mechanism)
1. Start server
2. Run Unity scene with test script
3. Connect to a new room with no existing variables
4. **Expected Result**: OnReady fires after ~2 seconds (timeout)
5. **Console Output**:
   ```
   [TestOnReadyImprovements] ✓ Correctly threw exception before OnReady
   [TestOnReadyImprovements] ✓ OnReady event fired after 2.XX seconds!
   [TestOnReadyImprovements] ✓ GetGlobalVariable works after OnReady
   [TestOnReadyImprovements] All tests passed!
   ```

#### Scenario B: Room with Variables (Tests Immediate Sync)
1. Use existing demo scene that sets network variables
2. Run first client that sets variables (e.g., `NV_IncrementGlobalVariable`)
3. Wait for variables to be set
4. Run second client with test script
5. **Expected Result**: OnReady fires immediately after receiving sync
6. **Console Output**: Similar to above but with shorter timing

#### Scenario C: Error Handling (Tests Exception Throwing)
1. Run Unity scene with test script
2. **Expected Result**: Exception thrown when calling GetGlobalVariable before OnReady
3. **Console Output**:
   ```
   [TestOnReadyImprovements] ✓ Correctly threw exception before OnReady: Cannot get global variables before OnReady event. Please wait for OnReady to be fired.
   ```

### 4. Manual Testing

You can also test manually:

```csharp
public class ManualTest : MonoBehaviour
{
    void Start()
    {
        // This should throw an exception immediately
        try 
        {
            var value = NetSyncManager.Instance.GetGlobalVariable("test");
            Debug.LogError("ERROR: Should have thrown exception!");
        }
        catch (InvalidOperationException e)
        {
            Debug.Log($"✓ Exception correctly thrown: {e.Message}");
        }
        
        // Subscribe to OnReady
        NetSyncManager.Instance.OnReady.AddListener(() => {
            Debug.Log("✓ OnReady fired!");
            
            // This should work now
            var value = NetSyncManager.Instance.GetGlobalVariable("test", "default");
            Debug.Log($"✓ GetGlobalVariable works: {value}");
        });
    }
}
```

## Expected Behavior Changes

### Before Improvements:
- OnReady fired immediately after connection + handshake
- GetGlobalVariable could return stale/empty data
- Race condition potential

### After Improvements:
- OnReady fires after connection + handshake + network variable sync (or 2s timeout)
- GetGlobalVariable throws exception before OnReady
- No race conditions

## Validation Points

1. **✅ Exception Throwing**: Calling GetGlobalVariable/GetClientVariable before OnReady throws InvalidOperationException
2. **✅ Timeout Mechanism**: Empty rooms trigger OnReady after 2 seconds
3. **✅ Immediate Sync**: Rooms with variables trigger OnReady immediately after sync
4. **✅ Variable Access**: GetGlobalVariable/GetClientVariable work normally after OnReady
5. **✅ Connection Reset**: Ready state resets properly on disconnection

## Troubleshooting

### OnReady Never Fires
- Check server connection (enable debug logs)
- Verify server is running and accessible
- Check room ID matches between client and server

### Exception Not Thrown
- Ensure NetSyncManager.Instance is available
- Check if OnReady already fired (timing issue)
- Verify test script is running before connection

### Variables Don't Work After OnReady
- Check server logs for variable sync messages
- Verify room ID is correct
- Enable network traffic logging for debugging

## Files Modified

- `Runtime/NetSyncManager.cs` - Main manager changes
- `Runtime/Internal/NetworkVariableManager.cs` - Sync tracking and error checking
- `Runtime/Internal/MessageProcessor.cs` - Ready check triggering
- `Assets/Samples_Dev/Debug/TestOnReadyImprovements.cs` - Test script