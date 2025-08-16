using UnityEngine;
using Styly.NetSync;

public class SendRPC : MonoBehaviour
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
    }
}
