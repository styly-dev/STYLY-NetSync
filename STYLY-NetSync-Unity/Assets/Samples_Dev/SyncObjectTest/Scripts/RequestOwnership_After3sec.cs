using System.Collections;
using UnityEngine;
using Styly.NetSync;

[RequireComponent(typeof(NetSyncObject))]
public class RequestOwnership_After3sec : MonoBehaviour
{
    private IEnumerator Start()
    {
        yield return new WaitForSeconds(3f);
        GetComponent<NetSyncObject>().RequestOwnership();
        Debug.Log("Requested ownership after 3 seconds.");
    }
}
