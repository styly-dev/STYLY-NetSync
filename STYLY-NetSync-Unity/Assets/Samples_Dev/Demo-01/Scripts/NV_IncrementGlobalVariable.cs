using Styly.NetSync;
using UnityEngine;

public class NV_IncrementGlobalVariable : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Invoke(nameof(FuncEvery10Sec), 10f);
    }

    void FuncEvery10Sec()
    {
        InvokeRepeating(nameof(IncrementGlobalVariable), 10f, 10f);
    }

    void IncrementGlobalVariable()
    {
        string GlobalNumber_current = NetSyncManager.Instance.GetGlobalVariable("GlobalNumber", "0");
        int GlobalNumber = int.Parse(GlobalNumber_current);
        GlobalNumber++;
        NetSyncManager.Instance.SetGlobalVariable("GlobalNumber", GlobalNumber.ToString());
    }
}
