using UnityEngine;
using Styly.NetSync;

public class ReceiveRPC_to_ChangeColor : MonoBehaviour
{

    /// <summary>
    /// Handles incoming RPC calls.
    /// Set this up in the event of NetSyncManager to receive RPCs.
    /// </summary>
    public void HandleRPC(int senderClientNo, string functionName, string[] args)
    {
        Debug.Log($"[RPC] From client {senderClientNo} → {functionName}({string.Join(", ", args)})");

        if (functionName == "ChangeColor")
        {
            if (float.TryParse(args[0], out float r) && float.TryParse(args[1], out float g) && float.TryParse(args[2], out float b))
            {
                Color color = new(r, g, b);
                transform.GetComponent<Renderer>().material.color = color;
            }
            else
            {
                Debug.LogWarning("Invalid color values received in RPC.");
            }
        }
    }



}
