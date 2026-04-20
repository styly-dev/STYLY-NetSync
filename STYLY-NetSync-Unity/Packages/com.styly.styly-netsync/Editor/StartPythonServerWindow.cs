using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Editor
{
    internal class StartPythonServerWindow : EditorWindow
    {
        private const string PrefsPrefix = "STYLY_NetSync_Server_";

        // Port settings
        private int _dealerPort = 5555;
        private int _pubPort = 5556;
        private int _serverDiscoveryPort = 9999;
        private int _restApiPort = 8800;
        private bool _disableServerDiscovery = false;
        private string _configFile = "";

        // Logging settings (advanced)
        private string _logDir = "";
        private string _logRotation = "";
        private string _logRetention = "";
        private bool _logJsonConsole = false;
        private string _logLevelConsole = "";

        private string _serverVersion;
        private bool _showAdvancedOptions;
        private Vector2 _scrollPosition;

        private static readonly string[] LogLevelOptions = { "(default)", "TRACE", "DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL" };
        private int _logLevelIndex = 0;

        [MenuItem("STYLY/STYLY NetSync/Start NetSync Server", false, 100)]
        private static void ShowWindow()
        {
            var window = GetWindow<StartPythonServerWindow>("Start NetSync Server");
            window.minSize = new Vector2(420, 340);
            window.Show();
        }

        private void OnEnable()
        {
            LoadSettings();
            _serverVersion = StartPythonServer.GetServerVersionSafe();
        }

        private void OnDisable()
        {
            SaveSettings();
        }

        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            // --- Server Discovery Port ---
            _serverDiscoveryPort = PortField("Server Discovery Port", _serverDiscoveryPort);

            EditorGUILayout.Space(4);

            // --- Config File ---
            EditorGUILayout.LabelField("Config File", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            _configFile = EditorGUILayout.TextField(_configFile);
            if (GUILayout.Button("Browse...", GUILayout.Width(80)))
            {
                string path = EditorUtility.OpenFilePanel("Select Config File", "", "toml");
                if (!string.IsNullOrEmpty(path))
                {
                    _configFile = path;
                }
            }
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                _configFile = "";
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);

            // --- Advanced Options (foldout) ---
            _showAdvancedOptions = EditorGUILayout.BeginFoldoutHeaderGroup(_showAdvancedOptions, "Advanced Options");
            if (_showAdvancedOptions)
            {
                EditorGUI.indentLevel++;

                // Port settings
                EditorGUILayout.LabelField("Ports", EditorStyles.boldLabel);
                _dealerPort = PortField("Dealer Port", _dealerPort);
                _pubPort = PortField("PUB Port", _pubPort);
                _restApiPort = PortField("REST API Port", _restApiPort);

                EditorGUILayout.Space(4);

                // Server Discovery
                _disableServerDiscovery = EditorGUILayout.Toggle("Disable Server Discovery", _disableServerDiscovery);

                EditorGUILayout.Space(4);

                // Logging settings
                EditorGUILayout.LabelField("Logging", EditorStyles.boldLabel);

                EditorGUILayout.BeginHorizontal();
                _logDir = EditorGUILayout.TextField("Log Directory", _logDir);
                if (GUILayout.Button("Browse...", GUILayout.Width(80)))
                {
                    string path = EditorUtility.OpenFolderPanel("Select Log Directory", _logDir, "");
                    if (!string.IsNullOrEmpty(path))
                    {
                        _logDir = path;
                    }
                }
                if (GUILayout.Button("Clear", GUILayout.Width(50)))
                {
                    _logDir = "";
                }
                EditorGUILayout.EndHorizontal();

                _logRotation = EditorGUILayout.TextField(
                    new GUIContent("Log Rotation", "e.g. \"10 MB\", \"1 day\", \"12:00\""),
                    _logRotation);
                _logRetention = EditorGUILayout.TextField(
                    new GUIContent("Log Retention", "e.g. \"5\", \"1 week\", \"keep 10 files\""),
                    _logRetention);

                _logLevelIndex = EditorGUILayout.Popup("Console Log Level", _logLevelIndex, LogLevelOptions);
                _logLevelConsole = _logLevelIndex == 0 ? "" : LogLevelOptions[_logLevelIndex];

                _logJsonConsole = EditorGUILayout.Toggle("JSON Console Output", _logJsonConsole);

                EditorGUILayout.Space(8);

                // --- Command Preview ---
                EditorGUILayout.LabelField("Command Preview", EditorStyles.boldLabel);
                string command = BuildConfig().BuildCommand(_serverVersion);

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextArea(command, EditorStyles.wordWrappedLabel);
                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button("Copy Command"))
                {
                    EditorGUIUtility.systemCopyBuffer = command;
                    Debug.Log("STYLY NetSync: Command copied to clipboard.");
                }

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            EditorGUILayout.Space(12);

            // --- Action Buttons ---
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to Defaults"))
            {
                ResetToDefaults();
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Start Server", GUILayout.Width(120), GUILayout.Height(30)))
            {
                SaveSettings();
                StartPythonServer.LaunchServer(BuildConfig());
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        private static int PortField(string label, int value)
        {
            int newValue = EditorGUILayout.IntField(label, value);
            return Mathf.Clamp(newValue, 1, 65535);
        }

        private ServerLaunchConfig BuildConfig()
        {
            return new ServerLaunchConfig
            {
                DealerPort = _dealerPort,
                PubPort = _pubPort,
                ServerDiscoveryPort = _serverDiscoveryPort,
                RestApiPort = _restApiPort,
                DisableServerDiscovery = _disableServerDiscovery,
                ConfigFile = _configFile,
                LogDir = _logDir,
                LogRotation = _logRotation,
                LogRetention = _logRetention,
                LogJsonConsole = _logJsonConsole,
                LogLevelConsole = _logLevelConsole,
            };
        }

        private void LoadSettings()
        {
            _dealerPort = EditorPrefs.GetInt(PrefsPrefix + "DealerPort", 5555);
            _pubPort = EditorPrefs.GetInt(PrefsPrefix + "PubPort", 5556);
            _serverDiscoveryPort = EditorPrefs.GetInt(PrefsPrefix + "ServerDiscoveryPort",
                StartPythonServer.GetDefaultServerDiscoveryPort());
            _restApiPort = EditorPrefs.GetInt(PrefsPrefix + "RestApiPort", 8800);
            _disableServerDiscovery = EditorPrefs.GetBool(PrefsPrefix + "DisableServerDiscovery", false);
            _configFile = EditorPrefs.GetString(PrefsPrefix + "ConfigFile", "");

            _logDir = EditorPrefs.GetString(PrefsPrefix + "LogDir", "");
            _logRotation = EditorPrefs.GetString(PrefsPrefix + "LogRotation", "");
            _logRetention = EditorPrefs.GetString(PrefsPrefix + "LogRetention", "");
            _logJsonConsole = EditorPrefs.GetBool(PrefsPrefix + "LogJsonConsole", false);
            _logLevelConsole = EditorPrefs.GetString(PrefsPrefix + "LogLevelConsole", "");

            // Resolve log level index from stored string
            _logLevelIndex = 0;
            for (int i = 1; i < LogLevelOptions.Length; i++)
            {
                if (LogLevelOptions[i] == _logLevelConsole)
                {
                    _logLevelIndex = i;
                    break;
                }
            }
        }

        private void SaveSettings()
        {
            EditorPrefs.SetInt(PrefsPrefix + "DealerPort", _dealerPort);
            EditorPrefs.SetInt(PrefsPrefix + "PubPort", _pubPort);
            EditorPrefs.SetInt(PrefsPrefix + "ServerDiscoveryPort", _serverDiscoveryPort);
            EditorPrefs.SetInt(PrefsPrefix + "RestApiPort", _restApiPort);
            EditorPrefs.SetBool(PrefsPrefix + "DisableServerDiscovery", _disableServerDiscovery);
            EditorPrefs.SetString(PrefsPrefix + "ConfigFile", _configFile);

            EditorPrefs.SetString(PrefsPrefix + "LogDir", _logDir);
            EditorPrefs.SetString(PrefsPrefix + "LogRotation", _logRotation);
            EditorPrefs.SetString(PrefsPrefix + "LogRetention", _logRetention);
            EditorPrefs.SetBool(PrefsPrefix + "LogJsonConsole", _logJsonConsole);
            EditorPrefs.SetString(PrefsPrefix + "LogLevelConsole", _logLevelConsole);
        }

        private void ResetToDefaults()
        {
            _dealerPort = 5555;
            _pubPort = 5556;
            _serverDiscoveryPort = StartPythonServer.GetDefaultServerDiscoveryPort();
            _restApiPort = 8800;
            _disableServerDiscovery = false;
            _configFile = "";

            _logDir = "";
            _logRotation = "";
            _logRetention = "";
            _logJsonConsole = false;
            _logLevelConsole = "";
            _logLevelIndex = 0;

            SaveSettings();
        }
    }
}
