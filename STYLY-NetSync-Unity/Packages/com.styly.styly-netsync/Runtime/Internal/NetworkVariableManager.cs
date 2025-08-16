// NetworkVariableManager.cs - Handles Network Variables system
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using NetMQ;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace Styly.NetSync
{
    public class NetworkVariableManager
    {
        private readonly ConnectionManager _connectionManager;
        private readonly string _deviceId;
        private readonly NetSyncManager _netSyncManager;

        // Network Variables storage
        private readonly Dictionary<string, string> _globalVariables = new();
        private readonly Dictionary<int, Dictionary<string, string>> _clientVariables = new();

        // Network Variables limits (must match server)
        private const int MAX_GLOBAL_VARS = 20;
        private const int MAX_CLIENT_VARS = 20;
        private const int MAX_VAR_NAME_LENGTH = 64;
        private const int MAX_VAR_VALUE_LENGTH = 1024;

        // Debounce configuration
        private const double DEBOUNCE_INTERVAL = 0.1; // 100ms debounce

        // Send-side dedupe caches
        private readonly Dictionary<string, string> _lastSentGlobal = new();
        private readonly Dictionary<(int, string), string> _lastSentClient = new();

        // Debounce buffers for pending sends
        private readonly Dictionary<string, string> _pendingGlobal = new();
        private readonly Dictionary<string, double> _dueGlobal = new();
        private readonly Dictionary<(int, string), string> _pendingClient = new();
        private readonly Dictionary<(int, string), double> _dueClient = new();

        // Events (using C# events, NOT SendMessage)
        public event Action<string, string, string> OnGlobalVariableChanged;
        public event Action<int, string, string, string> OnClientVariableChanged;

        public NetworkVariableManager(ConnectionManager connectionManager, string deviceId, NetSyncManager netSyncManager)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            _netSyncManager = netSyncManager;
        }

        // Global Variables API
        public bool SetGlobalVariable(string name, string value, string roomId)
        {
            if (!ValidateVariableName(name) || !ValidateVariableValue(value))
                return false;

            if (_globalVariables.Count >= MAX_GLOBAL_VARS && !_globalVariables.ContainsKey(name))
            {
                Debug.LogWarning($"Global variable limit ({MAX_GLOBAL_VARS}) reached");
                return false;
            }

            // Dedupe: check if value is same as last sent
            if (_lastSentGlobal.TryGetValue(name, out var lastSent) && lastSent == value)
            {
                return false; // Value unchanged, don't send
            }

            // Buffer with debounce
            _pendingGlobal[name] = value;
            _dueGlobal[name] = UnityEngine.Time.realtimeSinceStartupAsDouble + DEBOUNCE_INTERVAL;
            return true; // Buffered for later send
        }

        // Internal method to send now (used by flush)
        private bool TrySendGlobalNow(string name, string value, string roomId)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var data = new Dictionary<string, object>
            {
                ["senderClientNo"] = _netSyncManager.ClientNo,
                ["variableName"] = name,
                ["variableValue"] = value,
                ["timestamp"] = timestamp
            };

            try
            {
                var binaryData = BinarySerializer.SerializeGlobalVarSet(data);
                var msg = new NetMQMessage();
                msg.Append(roomId);
                msg.Append(binaryData);

                bool sent = _connectionManager.DealerSocket != null ? _connectionManager.DealerSocket.TrySendMultipartMessage(msg) : false;
                if (sent)
                {
                    _lastSentGlobal[name] = value; // Update last sent cache
                }
                return sent;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to send global variable: {ex.Message}");
                return false;
            }
        }

        public string GetGlobalVariable(string name, string defaultValue = null)
        {
            return _globalVariables.TryGetValue(name, out var value) ? value : defaultValue;
        }

        // Client Variables API
        public bool SetClientVariable(int targetClientNo, string name, string value, string roomId)
        {
            if (!ValidateVariableName(name) || !ValidateVariableValue(value))
                return false;

            if (!_clientVariables.ContainsKey(targetClientNo))
                _clientVariables[targetClientNo] = new Dictionary<string, string>();

            var clientVars = _clientVariables[targetClientNo];
            if (clientVars.Count >= MAX_CLIENT_VARS && !clientVars.ContainsKey(name))
            {
                Debug.LogWarning($"Client variable limit ({MAX_CLIENT_VARS}) reached for client {targetClientNo}");
                return false;
            }

            // Dedupe: check if value is same as last sent
            var key = (targetClientNo, name);
            if (_lastSentClient.TryGetValue(key, out var lastSent) && lastSent == value)
            {
                return false; // Value unchanged, don't send
            }

            // Buffer with debounce
            _pendingClient[key] = value;
            _dueClient[key] = UnityEngine.Time.realtimeSinceStartupAsDouble + DEBOUNCE_INTERVAL;
            return true; // Buffered for later send
        }

        // Internal method to send now (used by flush)
        private bool TrySendClientNow(int targetClientNo, string name, string value, string roomId)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var data = new Dictionary<string, object>
            {
                ["senderClientNo"] = _netSyncManager.ClientNo,
                ["targetClientNo"] = targetClientNo,
                ["variableName"] = name,
                ["variableValue"] = value,
                ["timestamp"] = timestamp
            };

            try
            {
                if (_connectionManager.DealerSocket == null)
                {
                    return false;
                }

                var binaryData = BinarySerializer.SerializeClientVarSet(data);
                var msg = new NetMQMessage();
                msg.Append(roomId);
                msg.Append(binaryData);

                bool sent = _connectionManager.DealerSocket.TrySendMultipartMessage(msg);
                if (sent)
                {
                    var key = (targetClientNo, name);
                    _lastSentClient[key] = value; // Update last sent cache
                }
                return sent;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to send client variable: {ex.Message}");
                return false;
            }
        }

        public string GetClientVariable(int clientNo, string name, string defaultValue = null)
        {
            if (_clientVariables.TryGetValue(clientNo, out var clientVars))
            {
                return clientVars.TryGetValue(name, out var value) ? value : defaultValue;
            }
            return defaultValue;
        }

        // Helper methods for type conversion
        private static object[] ConvertToObjectArray(object obj)
        {
            return obj switch
            {
                object[] arr => arr,
                JArray jArr => jArr.ToObject<object[]>(),
                _ => null
            };
        }

        private static Dictionary<string, object> ConvertToDictionary(object obj)
        {
            return obj switch
            {
                Dictionary<string, object> dict => dict,
                JObject jObj => jObj.ToObject<Dictionary<string, object>>(),
                _ => null
            };
        }

        // Message handling (called by MessageProcessor)
        public void HandleGlobalVariableSync(Dictionary<string, object> data)
        {
            if (data.TryGetValue("variables", out var variablesObj))
            {
                object[] variables = ConvertToObjectArray(variablesObj);

                if (variables == null) return;

                foreach (var varObj in variables)
                {
                    Dictionary<string, object> variable = ConvertToDictionary(varObj);

                    if (variable != null)
                    {
                        var name = variable.TryGetValue("name", out var nameObj) ? (nameObj != null ? nameObj.ToString() : null) : null;
                        var value = variable.TryGetValue("value", out var valueObj) ? (valueObj != null ? valueObj.ToString() : null) : null;

                        if (!string.IsNullOrEmpty(name) && value != null)
                        {
                            var oldValue = _globalVariables.TryGetValue(name, out var existing) ? existing : null;
                            _globalVariables[name] = value;

                            // Trigger event with direct method call (NOT SendMessage)
                            if (OnGlobalVariableChanged != null)
                            {
                                OnGlobalVariableChanged.Invoke(name, oldValue, value);
                            }
                        }
                    }
                }
            }
        }

        public void HandleClientVariableSync(Dictionary<string, object> data)
        {
            if (data.TryGetValue("clientVariables", out var clientVarsObj))
            {
                Dictionary<string, object> clientVariables = ConvertToDictionary(clientVarsObj);

                if (clientVariables == null)
                {
                    return;
                }

                foreach (var kvp in clientVariables)
                {
                    if (int.TryParse(kvp.Key, out var clientNo))
                    {
                        object[] variables = ConvertToObjectArray(kvp.Value);

                        if (variables == null) continue;

                        if (!_clientVariables.ContainsKey(clientNo))
                            _clientVariables[clientNo] = new Dictionary<string, string>();

                        var clientVars = _clientVariables[clientNo];

                        foreach (var varObj in variables)
                        {
                            Dictionary<string, object> variable = ConvertToDictionary(varObj);

                            if (variable != null)
                            {
                                var name = variable.TryGetValue("name", out var nameObj) ? (nameObj != null ? nameObj.ToString() : null) : null;
                                var value = variable.TryGetValue("value", out var valueObj) ? (valueObj != null ? valueObj.ToString() : null) : null;

                                if (!string.IsNullOrEmpty(name) && value != null)
                                {
                                    var oldValue = clientVars.TryGetValue(name, out var existing) ? existing : null;
                                    clientVars[name] = value;

                                    // Trigger event with direct method call (NOT SendMessage)
                                    if (OnClientVariableChanged != null)
                                    {
                                        OnClientVariableChanged.Invoke(clientNo, name, oldValue, value);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Validation helpers
        private bool ValidateVariableName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MAX_VAR_NAME_LENGTH)
            {
                Debug.LogWarning($"Invalid variable name: must be 1-{MAX_VAR_NAME_LENGTH} characters");
                return false;
            }
            return true;
        }

        private bool ValidateVariableValue(string value)
        {
            if (value == null || value.Length > MAX_VAR_VALUE_LENGTH)
            {
                Debug.LogWarning($"Invalid variable value: must be 0-{MAX_VAR_VALUE_LENGTH} characters");
                return false;
            }
            return true;
        }

        // Get all global variables (for debugging/inspection)
        public Dictionary<string, string> GetAllGlobalVariables()
        {
            return new Dictionary<string, string>(_globalVariables);
        }

        // Get all client variables for a specific client (for debugging/inspection)
        public Dictionary<string, string> GetAllClientVariables(int clientNo)
        {
            if (_clientVariables.TryGetValue(clientNo, out var clientVars))
            {
                return new Dictionary<string, string>(clientVars);
            }
            return new Dictionary<string, string>();
        }

        // Tick method to process pending NV updates
        public void Tick(double nowSeconds, string roomId)
        {
            FlushPendingGlobal(nowSeconds, roomId);
            FlushPendingClient(nowSeconds, roomId);
        }

        private void FlushPendingGlobal(double nowSeconds, string roomId)
        {
            var toFlush = new List<(string name, string value)>();

            // Find all due items
            foreach (var kvp in _dueGlobal)
            {
                if (kvp.Value <= nowSeconds)
                {
                    if (_pendingGlobal.TryGetValue(kvp.Key, out var value))
                    {
                        toFlush.Add((kvp.Key, value));
                    }
                }
            }

            // Flush due items
            foreach (var (name, value) in toFlush)
            {
                if (TrySendGlobalNow(name, value, roomId))
                {
                    // Remove from pending
                    _pendingGlobal.Remove(name);
                    _dueGlobal.Remove(name);
                }
            }
        }

        private void FlushPendingClient(double nowSeconds, string roomId)
        {
            var toFlush = new List<((int targetClientNo, string name) key, string value)>();

            // Find all due items
            foreach (var kvp in _dueClient)
            {
                if (kvp.Value <= nowSeconds)
                {
                    if (_pendingClient.TryGetValue(kvp.Key, out var value))
                    {
                        toFlush.Add((kvp.Key, value));
                    }
                }
            }

            // Flush due items
            foreach (var (key, value) in toFlush)
            {
                if (TrySendClientNow(key.Item1, key.Item2, value, roomId))
                {
                    // Remove from pending
                    _pendingClient.Remove(key);
                    _dueClient.Remove(key);
                }
            }
        }
    }
}