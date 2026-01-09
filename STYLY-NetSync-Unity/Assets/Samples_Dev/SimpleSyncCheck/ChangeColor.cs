using UnityEngine;

public class ChangeColor : MonoBehaviour
{
    [SerializeField]
    private Renderer targetRenderer;

    private bool showDebugLogs = true;
    
    [SerializeField]
    Color[] colors = new Color[]
    {
        Color.red,
        Color.green,
        Color.blue,
        Color.yellow,
        Color.cyan,
        Color.magenta,
        Color.white,
        Color.clear
    };
    const string RPCFunction = "ChangeColor";

    private void Start()
    {
        RegisterRpcHandler();
        RegisterGlobalVariableListener();
    }
    
    private void RegisterRpcHandler()
    {
        var manager = Styly.NetSync.NetSyncManager.Instance;
        if (manager != null)
        {
            manager.OnRPCReceived.RemoveListener(HandleRPC);
            manager.OnRPCReceived.AddListener(HandleRPC);
            Debug.Log("[ChangeColor] Registered RPC handler with NetSyncManager");
        }
        else
        {
            Debug.LogWarning("[ChangeColor] NetSyncManager.Instance is null! Will retry when available.");
        }
    }
    
    private void RegisterGlobalVariableListener()
    {
        var manager = Styly.NetSync.NetSyncManager.Instance;
        if (manager != null)
        {
            manager.OnGlobalVariableChanged.RemoveListener(HandleGlobalVariableChanged);
            manager.OnGlobalVariableChanged.AddListener(HandleGlobalVariableChanged);
            Debug.Log("[ChangeColor] Registered global variable handler with NetSyncManager");
        }
    }

    public void HandleRPC(int senderClientNo, string functionName, string[] args)
    {
        Debug.Log($"[RPC] From client {senderClientNo} → {functionName}({string.Join(", ", args)})");

        switch (functionName)
        {
            case RPCFunction:
                if (args.Length >= 1 && int.TryParse(args[0], out int colorIndex))
                {
                    ChangeColorImpl(colorIndex);
                }
                break;
        }
    }
    
    private void HandleGlobalVariableChanged(string variableName, string previousValue, string newValue)
    {
        if (variableName != RPCFunction)
        {
            return;
        }

        Debug.Log($"[ChangeColor] GlobalVariable changed: \"{newValue}\"");
        
        if (int.TryParse(newValue, out int colorIndex))
        {
            ChangeColorImpl(colorIndex);
        }
    }
    
    private void ChangeColorImpl(int colorIndex)
    {
        if (colorIndex >= 0 && colorIndex < colors.Length)
        {
            Color selectedColor = colors[colorIndex];
            Renderer renderer = targetRenderer;
            if (renderer != null)
            {
                renderer.material.color = selectedColor;
                Debug.Log($"[ChangeColor] Changed color to {selectedColor} (index {colorIndex})");
            }
            else
            {
                Debug.LogWarning("[ChangeColor] No Renderer component found on this GameObject.");
            }
        }
        else
        {
            Debug.LogWarning($"[ChangeColor] Color index {colorIndex} is out of range.");
        }
    }
    
    public void ChangeColorRPC(int colorIndex)
    {
        Styly.NetSync.NetSyncManager.Instance.Rpc(RPCFunction, new[] { colorIndex.ToString() });
    }

    public void ChangeColorGlobalVariable(int colorIndex)
    {
        var manager = Styly.NetSync.NetSyncManager.Instance;
        if (manager == null)
        {
            Debug.LogWarning("[ChangeColor] Cannot update Color because NetSyncManager.Instance is null.");
            return;
        }

        bool success = manager.SetGlobalVariable(RPCFunction, colorIndex.ToString());
    }
}
