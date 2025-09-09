using System.Collections.Generic;
using UnityEngine;
using Styly.NetSync;

/// <summary>
/// Test script to demonstrate and validate the GetConnectedClientIds functionality.
/// This script periodically logs the connected client IDs and handles avatar connection events.
/// </summary>
public class ClientListDebugger : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private float logInterval = 5f;
    
    private float lastLogTime;
    private NetSyncManager netSyncManager;
    
    void Start()
    {
        netSyncManager = NetSyncManager.Instance;
        if (netSyncManager == null)
        {
            Debug.LogError("[ClientListDebugger] NetSyncManager instance not found!");
            return;
        }
        
        // Subscribe to avatar connection events
        netSyncManager.OnAvatarConnected.AddListener(OnAvatarConnected);
        netSyncManager.OnAvatarDisconnected.AddListener(OnAvatarDisconnected);
        
        Debug.Log("[ClientListDebugger] Started - will log connected clients every " + logInterval + " seconds");
    }
    
    void Update()
    {
        if (netSyncManager == null) return;
        
        if (Time.time - lastLogTime >= logInterval)
        {
            lastLogTime = Time.time;
            LogConnectedClients();
        }
    }
    
    private void LogConnectedClients()
    {
        List<int> connectedClients = netSyncManager.GetConnectedClientIds();
        
        if (connectedClients.Count == 0)
        {
            Debug.Log("[ClientListDebugger] No connected clients (only local client)");
        }
        else
        {
            string clientsList = string.Join(", ", connectedClients);
            Debug.Log($"[ClientListDebugger] Connected clients ({connectedClients.Count}): [{clientsList}]");
        }
        
        Debug.Log($"[ClientListDebugger] Local client ID: {netSyncManager.ClientNo}");
    }
    
    private void OnAvatarConnected(int clientId)
    {
        Debug.Log($"[ClientListDebugger] Avatar connected: Client#{clientId}");
        
        // Log updated client list immediately when someone connects
        List<int> connectedClients = netSyncManager.GetConnectedClientIds();
        string clientsList = string.Join(", ", connectedClients);
        Debug.Log($"[ClientListDebugger] Updated connected clients: [{clientsList}]");
    }
    
    private void OnAvatarDisconnected(int clientId)
    {
        Debug.Log($"[ClientListDebugger] Avatar disconnected: Client#{clientId}");
        
        // Log updated client list immediately when someone disconnects
        List<int> connectedClients = netSyncManager.GetConnectedClientIds();
        string clientsList = string.Join(", ", connectedClients);
        Debug.Log($"[ClientListDebugger] Updated connected clients: [{clientsList}]");
    }
    
    void OnDestroy()
    {
        // Clean up event subscriptions
        if (netSyncManager != null)
        {
            netSyncManager.OnAvatarConnected.RemoveListener(OnAvatarConnected);
            netSyncManager.OnAvatarDisconnected.RemoveListener(OnAvatarDisconnected);
        }
    }
}