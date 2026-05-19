#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor window for controlling RL training from Unity.
/// Access via menu: SHILATE → Training Controller.
/// </summary>
public class TrainingEditorWindow : EditorWindow
{
    TrainingSettings _settings;
    SerializedObject _serializedSettings;
    PythonProcessManager _processManager;
    TrainingMetricsParser _metricsParser;

    readonly List<LogEntry> _logEntries = new();
    Vector2 _logScrollPos;
    Vector2 _settingsScrollPos;
    bool _autoScroll = true;
    bool _settingsFoldout = true;
    bool _metricsFoldout = true;
    bool _logFoldout = true;

    DateTime _trainingStartTime;
    bool _mqttConnected;

    // Use SessionState to persist across play mode transitions
    const string DebugModeKey = "TrainingController_DebugMode";
    const string ProcessRunningKey = "TrainingController_ProcessRunning";

    bool DebugMode
    {
        get => SessionState.GetBool(DebugModeKey, false);
        set => SessionState.SetBool(DebugModeKey, value);
    }

    bool ProcessWasRunning
    {
        get => SessionState.GetBool(ProcessRunningKey, false);
        set => SessionState.SetBool(ProcessRunningKey, value);
    }

    const int MaxLogEntries = 500;
    const string SettingsAssetPath = "Assets/Settings/TrainingSettings.asset";

    struct LogEntry
    {
        public string Message;
        public LogType Type;
        public DateTime Time;
    }

    [MenuItem("SHILATE/Training Controller")]
    public static void ShowWindow()
    {
        var window = GetWindow<TrainingEditorWindow>("Training Controller");
        window.minSize = new Vector2(400, 500);
    }

    void OnEnable()
    {
        LoadOrCreateSettings();
        _processManager = new PythonProcessManager();
        _metricsParser = new TrainingMetricsParser();

        _processManager.OnOutputLine += HandleOutput;
        _processManager.OnErrorLine += HandleError;
        _processManager.OnExited += HandleExited;

        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.update += OnEditorUpdate;

        CheckMqttConnection();
    }

    void OnDisable()
    {
        if (_processManager != null && _processManager.IsRunning)
        {
            if (EditorUtility.DisplayDialog("Training Running",
                "Training is still running. Stop it before closing?", "Stop", "Keep Running"))
            {
                _processManager.Stop();
            }
        }

        if (_processManager != null)
        {
            _processManager.OnOutputLine -= HandleOutput;
            _processManager.OnErrorLine -= HandleError;
            _processManager.OnExited -= HandleExited;
        }

        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.update -= OnEditorUpdate;
    }

    void OnEditorUpdate()
    {
        if (_processManager != null && _processManager.IsRunning)
            Repaint();
    }

    void OnPlayModeChanged(PlayModeStateChange state)
    {
        bool isRunning = _processManager != null && _processManager.IsRunning;

        if (state == PlayModeStateChange.EnteredPlayMode && DebugMode)
        {
            // Delay to ensure scene is fully loaded
            EditorApplication.delayCall += ConfigureSceneForDebugTraining;
        }
        else if (state == PlayModeStateChange.ExitingPlayMode && DebugMode)
        {
            AddLog("Exiting Play mode, stopping debug training...", LogType.Warning);
            if (_processManager != null && _processManager.IsRunning)
                _processManager.Stop();
            DebugMode = false;
            ProcessWasRunning = false;
        }
        else if (state == PlayModeStateChange.ExitingEditMode && isRunning && !DebugMode)
        {
            AddLog("Stopping training before entering Play mode...", LogType.Warning);
            _processManager.Stop();
        }
    }

    void ConfigureSceneForDebugTraining()
    {
        AddLog("Configuring scene for debug training...", LogType.Log);

        // Use FindObjectsByType with FindObjectsInactive to find disabled components
        var brokers = UnityEngine.Object.FindObjectsByType<LedaBroker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var remoteInputs = UnityEngine.Object.FindObjectsByType<RemoteDriveInput>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var manualInputs = UnityEngine.Object.FindObjectsByType<ManualDriveInput>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var trainingBridges = UnityEngine.Object.FindObjectsByType<TrainingBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        var broker = brokers.Length > 0 ? brokers[0] : null;
        var remoteInput = remoteInputs.Length > 0 ? remoteInputs[0] : null;
        var manualInput = manualInputs.Length > 0 ? manualInputs[0] : null;
        var trainingBridge = trainingBridges.Length > 0 ? trainingBridges[0] : null;

        if (broker != null)
        {
            broker.Configure(_settings.mqttHost, _settings.mqttPort, "env0");
            AddLog($"LedaBroker configured: {_settings.mqttHost}:{_settings.mqttPort} (env0)", LogType.Log);
        }
        else
        {
            AddLog("Warning: LedaBroker not found in scene!", LogType.Warning);
        }

        if (trainingBridge != null)
        {
            if (trainingBridge.broker == null) trainingBridge.broker = broker;
            trainingBridge.enabled = true;
            AddLog("TrainingBridge enabled", LogType.Log);
        }
        else
        {
            AddLog("Warning: TrainingBridge not found in scene!", LogType.Warning);
        }

        if (remoteInput != null)
        {
            remoteInput.enabled = true;
            AddLog("RemoteDriveInput enabled", LogType.Log);
        }
        else
        {
            AddLog("Warning: RemoteDriveInput not found in scene! Make sure Car is in scene.", LogType.Warning);
        }

        if (manualInput != null)
        {
            manualInput.enabled = false;
            AddLog("ManualDriveInput disabled", LogType.Log);
        }

        Time.timeScale = _settings.debugTimescale;
        Time.fixedDeltaTime = 0.02f * _settings.debugTimescale;
        AddLog($"TimeScale set to {_settings.debugTimescale}x", LogType.Log);
    }

    void LoadOrCreateSettings()
    {
        _settings = AssetDatabase.LoadAssetAtPath<TrainingSettings>(SettingsAssetPath);

        if (_settings == null)
        {
            string dir = Path.GetDirectoryName(SettingsAssetPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _settings = CreateInstance<TrainingSettings>();
            AssetDatabase.CreateAsset(_settings, SettingsAssetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[TrainingController] Created settings at {SettingsAssetPath}");
        }

        _serializedSettings = new SerializedObject(_settings);
    }

    void OnGUI()
    {
        DrawHeader();
        EditorGUILayout.Space(5);

        _settingsScrollPos = EditorGUILayout.BeginScrollView(_settingsScrollPos);

        DrawSettingsSection();
        EditorGUILayout.Space(5);

        DrawMetricsSection();
        EditorGUILayout.Space(5);

        DrawLogSection();

        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(5);
        DrawControls();
    }

    void DrawHeader()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        bool isRunning = (_processManager != null && _processManager.IsRunning) || (DebugMode && EditorApplication.isPlaying);
        string status = isRunning ? (DebugMode ? "DEBUG" : "TRAINING") : "IDLE";
        Color statusColor = isRunning
            ? (DebugMode ? new Color(1f, 0.7f, 0.2f) : new Color(0.2f, 0.8f, 0.2f))
            : Color.gray;

        GUIStyle statusStyle = new(EditorStyles.boldLabel) { normal = { textColor = statusColor } };
        EditorGUILayout.LabelField($"[{status}]", statusStyle, GUILayout.Width(100));

        if (isRunning)
        {
            TimeSpan duration = DateTime.Now - _trainingStartTime;
            EditorGUILayout.LabelField($"Duration: {duration:hh\\:mm\\:ss}", GUILayout.Width(120));
        }

        GUILayout.FlexibleSpace();

        Color mqttColor = _mqttConnected ? new Color(0.2f, 0.8f, 0.2f) : Color.red;
        GUIStyle mqttStyle = new(EditorStyles.miniLabel) { normal = { textColor = mqttColor } };
        string mqttStatus = _mqttConnected ? "MQTT: OK" : "MQTT: X";
        EditorGUILayout.LabelField(mqttStatus, mqttStyle, GUILayout.Width(60));

        if (GUILayout.Button("Check", EditorStyles.toolbarButton, GUILayout.Width(50)))
            CheckMqttConnection();

        EditorGUILayout.EndHorizontal();
    }

    void DrawSettingsSection()
    {
        _settingsFoldout = EditorGUILayout.Foldout(_settingsFoldout, "Settings", true);
        if (!_settingsFoldout) return;

        EditorGUI.indentLevel++;
        _serializedSettings.Update();

        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("venvPath"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("trainScriptPath"));
        EditorGUILayout.Space(3);
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("numEnvs"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("timescale"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("totalTimesteps"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("learningRate"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("nSteps"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("batchSize"));

        // Show effective ray count
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("rayCount"));
        if (_settings.rayCount == 0)
        {
            int effectiveRays = _settings.GetEffectiveRayCount();
            EditorGUILayout.HelpBox($"Auto-detected from RaycastSensor: {effectiveRays} rays", MessageType.Info);
        }

        EditorGUILayout.Space(3);
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("debugTimescale"));
        EditorGUILayout.Space(3);
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("mqttHost"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("mqttPort"));
        EditorGUILayout.Space(3);
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("savePath"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("logDir"));

        _serializedSettings.ApplyModifiedProperties();
        EditorGUI.indentLevel--;
    }

    void DrawMetricsSection()
    {
        _metricsFoldout = EditorGUILayout.Foldout(_metricsFoldout, "Training Metrics", true);
        if (!_metricsFoldout || _metricsParser == null) return;

        EditorGUILayout.BeginHorizontal();
        DrawMetricGraph("Episode Reward", _metricsParser.RewardHistory, _metricsParser.LatestReward);
        DrawMetricGraph("Policy Loss", _metricsParser.LossHistory, _metricsParser.LatestLoss);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        DrawMetricGraph("Value Loss", _metricsParser.ValueLossHistory, _metricsParser.LatestValueLoss);
        DrawMetricGraph("KL Divergence", _metricsParser.KLHistory, _metricsParser.LatestKL);
        EditorGUILayout.EndHorizontal();
    }

    void DrawMetricGraph(string title, IReadOnlyList<TrainingMetricsParser.Metric> data, float? current)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MinWidth(180), GUILayout.Height(80));

        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

        string valueStr = current.HasValue ? current.Value.ToString("F4") : "waiting...";
        EditorGUILayout.LabelField($"Current: {valueStr}", EditorStyles.miniLabel);

        Rect graphRect = GUILayoutUtility.GetRect(160, 40);
        DrawGraph(graphRect, data);

        EditorGUILayout.EndVertical();
    }

    void DrawGraph(Rect rect, IReadOnlyList<TrainingMetricsParser.Metric> data)
    {
        if (Event.current.type != EventType.Repaint) return;

        // Background
        EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));

        if (data == null || data.Count < 2)
        {
            // Draw placeholder text
            GUI.Label(rect, "No data", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        var (min, max) = _metricsParser.GetRange(data);
        float range = max - min;
        if (range < 0.0001f) range = 1f;

        Handles.BeginGUI();
        Handles.color = new Color(0.3f, 0.7f, 1f);

        int pointCount = Mathf.Min(data.Count, (int)rect.width);
        int step = Mathf.Max(1, data.Count / pointCount);

        Vector3 prevPoint = Vector3.zero;
        bool first = true;
        for (int i = 0; i < data.Count; i += step)
        {
            float x = rect.x + (float)i / Mathf.Max(1, data.Count - 1) * rect.width;
            float normalizedY = (data[i].Value - min) / range;
            float y = rect.y + rect.height - normalizedY * rect.height;

            Vector3 point = new(x, Mathf.Clamp(y, rect.y, rect.y + rect.height), 0);
            if (!first)
                Handles.DrawLine(prevPoint, point);
            prevPoint = point;
            first = false;
        }

        Handles.EndGUI();
    }

    void DrawLogSection()
    {
        EditorGUILayout.BeginHorizontal();
        _logFoldout = EditorGUILayout.Foldout(_logFoldout, $"Output Log ({_logEntries.Count})", true);
        GUILayout.FlexibleSpace();
        _autoScroll = GUILayout.Toggle(_autoScroll, "Auto-scroll", GUILayout.Width(80));
        if (GUILayout.Button("Clear", GUILayout.Width(50)))
        {
            _logEntries.Clear();
            _metricsParser.Clear();
        }
        EditorGUILayout.EndHorizontal();

        if (!_logFoldout) return;

        // Draw log box with background
        Rect boxRect = EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(200));
        _logScrollPos = EditorGUILayout.BeginScrollView(_logScrollPos, GUILayout.ExpandHeight(true));

        if (_logEntries.Count == 0)
        {
            EditorGUILayout.LabelField("No log entries yet. Start training to see output.", EditorStyles.centeredGreyMiniLabel);
        }
        else
        {
            foreach (var entry in _logEntries)
            {
                Color color = entry.Type switch
                {
                    LogType.Error => new Color(1f, 0.4f, 0.4f),
                    LogType.Warning => new Color(1f, 0.9f, 0.4f),
                    _ => new Color(0.9f, 0.9f, 0.9f)
                };

                var prevColor = GUI.contentColor;
                GUI.contentColor = color;

                string timeStr = entry.Time.ToString("HH:mm:ss");
                EditorGUILayout.SelectableLabel($"[{timeStr}] {entry.Message}",
                    EditorStyles.miniLabel, GUILayout.Height(14));

                GUI.contentColor = prevColor;
            }
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        if (_autoScroll && _logEntries.Count > 0)
            _logScrollPos.y = float.MaxValue;
    }

    void DrawControls()
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        bool processRunning = _processManager != null && _processManager.IsRunning;
        bool debugActive = DebugMode && EditorApplication.isPlaying;
        bool canStart = !processRunning && !EditorApplication.isPlaying && !DebugMode;
        bool canStop = processRunning || debugActive;

        GUI.enabled = canStart;
        if (GUILayout.Button("Start Training", GUILayout.Width(120), GUILayout.Height(30)))
            StartTraining(debugMode: false);

        if (GUILayout.Button("Debug (1 env)", GUILayout.Width(100), GUILayout.Height(30)))
            StartTraining(debugMode: true);

        GUI.enabled = canStop;
        if (GUILayout.Button("Stop", GUILayout.Width(80), GUILayout.Height(30)))
        {
            if (_processManager != null && _processManager.IsRunning)
                _processManager.Stop();
            if (DebugMode && EditorApplication.isPlaying)
                EditorApplication.ExitPlaymode();
            DebugMode = false;
            ProcessWasRunning = false;
        }

        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        if (DebugMode && (processRunning || debugActive))
        {
            EditorGUILayout.HelpBox("Debug mode: Training with 1 env in Editor Play mode", MessageType.Info);
        }
    }

    void StartTraining(bool debugMode)
    {
        if (_processManager == null || _settings == null)
        {
            Debug.LogError("[TrainingController] Not initialized properly");
            return;
        }

        if (!_mqttConnected)
        {
            CheckMqttConnection();
            if (!_mqttConnected)
            {
                EditorUtility.DisplayDialog("MQTT Not Available",
                    $"Cannot connect to MQTT broker at {_settings.mqttHost}:{_settings.mqttPort}.\n\n" +
                    "Please ensure Mosquitto is running.", "OK");
                return;
            }
        }

        DebugMode = debugMode;
        ProcessWasRunning = true;
        _logEntries.Clear();
        if (_metricsParser != null) _metricsParser.Clear();
        _trainingStartTime = DateTime.Now;

        int envCount = debugMode ? 1 : _settings.numEnvs;
        float scale = debugMode ? _settings.debugTimescale : _settings.timescale;
        int rayCount = _settings.GetEffectiveRayCount();

        AddLog($"Starting training with {envCount} environment(s)...", LogType.Log);
        AddLog($"Timescale: {scale}x, Ray count: {rayCount}", LogType.Log);
        AddLog($"Total timesteps: {_settings.totalTimesteps}", LogType.Log);

        if (debugMode)
            AddLog("Debug mode: Will enter Play mode after Python starts", LogType.Log);

        if (!_processManager.Start(_settings, debugMode))
        {
            AddLog("Failed to start training process", LogType.Error);
            DebugMode = false;
            ProcessWasRunning = false;
            return;
        }

        if (debugMode)
        {
            EditorApplication.delayCall += () =>
            {
                AddLog("Entering Play mode...", LogType.Log);
                EditorApplication.EnterPlaymode();
            };
        }
    }

    void HandleOutput(string line)
    {
        AddLog(line, LogType.Log);
        _metricsParser.ParseLine(line);
    }

    void HandleError(string line)
    {
        if (line.Contains("WARNING") || line.Contains("warn"))
            AddLog(line, LogType.Warning);
        else
            AddLog(line, LogType.Error);
    }

    void HandleExited(int exitCode)
    {
        string msg = exitCode == 0
            ? "Training completed successfully"
            : $"Training exited with code {exitCode}";

        AddLog(msg, exitCode == 0 ? LogType.Log : LogType.Warning);
    }

    void AddLog(string message, LogType type)
    {
        _logEntries.Add(new LogEntry
        {
            Message = message,
            Type = type,
            Time = DateTime.Now
        });

        while (_logEntries.Count > MaxLogEntries)
            _logEntries.RemoveAt(0);

        Repaint();
    }

    void CheckMqttConnection()
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(_settings.mqttHost, _settings.mqttPort, null, null);
            bool connected = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
            _mqttConnected = connected && client.Connected;
            client.Close();
        }
        catch
        {
            _mqttConnected = false;
        }
    }
}
#endif
