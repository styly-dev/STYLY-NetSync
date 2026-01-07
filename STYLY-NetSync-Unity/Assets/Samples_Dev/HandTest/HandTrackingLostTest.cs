using UnityEngine;
using Styly.NetSync;

[RequireComponent(typeof(NetSyncAvatar))]
public class HandTrackingLostTest : MonoBehaviour
{
    [SerializeField] private GameObject _leftHand;
    [SerializeField] private GameObject _rightHand;
    
    void Start()
    {
        var avatar = GetComponent<NetSyncAvatar>();
        
        avatar.OnHandTrackingLost.AddListener(x =>
        {
            Debug.Log($"###Test  Lost {x}");
            if (x == Hand.Left)
            {
                // 表示状態の同期も必要
                _leftHand.SetActive(false);
            }
            else
            {
                _rightHand.SetActive(false);
            }
        });
        avatar.OnHandTrackingRestored.AddListener(x =>
        {
            Debug.Log($"###Test  Restore {x}");
            if (x == Hand.Left)
            {
                _leftHand.SetActive(true);
            }
            else
            {
                _rightHand.SetActive(true);
            }
        });
    }
}
