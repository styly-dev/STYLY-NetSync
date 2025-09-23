using Styly.NetSync;
using UnityEngine;

public class SwitchVRMR : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        InvokeRepeating(nameof(SwitchToVR), 5f, 10f);
        InvokeRepeating(nameof(SwitchToMR), 10f, 10f);
    }

    void SwitchToVR()
    {
        Debug.Log("Switch to VR");
        NetSyncManager.Instance.SwitchToVR(1, true);
    }

    void SwitchToMR()
    {
        Debug.Log("Switch to MR");
        NetSyncManager.Instance.SwitchToMR(1, true);
    }

}
