using UnityEngine;
using Styly.NetSync;

[RequireComponent(typeof(Renderer))]
public class ColorCubeTouched : MonoBehaviour
{
    void OnTriggerEnter(Collider other)
    {
        Debug.Log("Collided with: " + other.gameObject.name);

        // Send an RPC with the color of this object
        Color color = GetComponent<Renderer>().material.color;
        NetSyncManager.Instance.Rpc("ChangeColor", new[] {
            color.r.ToString(),
            color.g.ToString(),
            color.b.ToString()
        });

        // Set the client variable for the touched color
        NetSyncManager.Instance.SetClientVariable("Touched Color", color.ToString());
        IncrementTouchCount_Global();
        IncrementTouchCount_Client();
    }

    void IncrementTouchCount_Global()
    {
        int touchCount = int.Parse(NetSyncManager.Instance.GetGlobalVariable("Total Touch Count", "0"));
        touchCount++;
        NetSyncManager.Instance.SetGlobalVariable("Total Touch Count", touchCount.ToString());
    }

    void IncrementTouchCount_Client()
    {
        int touchCount = int.Parse(NetSyncManager.Instance.GetClientVariable("Touch Count", "0"));
        touchCount++;
        NetSyncManager.Instance.SetClientVariable("Touch Count", touchCount.ToString());
    }
}
