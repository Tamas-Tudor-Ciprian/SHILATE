#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Single-environment training control window.
/// Access via menu: SHILATE → Training Controller.
///
/// Layout (top to bottom):
///   1. Toolbar — status badge + MQTT dot + duration
///   2. Health banner — coloured strip with the current evaluator state
///   3. Snapshot row — episode #, last reward, rolling reward, fps
///   4. Settings — collapsible
///   5. Metric graphs — reward, policy loss, value loss, KL
///   6. Issue log — chronological health transitions
///   7. Raw stdout — collapsible
///   8. Controls — single Start/Stop button (and Run Model)
/// </summary>
public class TrainingEditorWindow : EditorWindow
{
    TrainingSettings _settings;
    SerializedObject _serializedSettings;
    PythonProcessManager _processManager;
    TrainingMetricsParser _metricsParser;
    TrainingHealthEvaluator _health;
    EditorMqttListener _mqtt;

    readonly List<LogEntry> _logEntries = new();
    Vector2 _logScrollPos;
    Vector2 _scrollPos;
    Vector2 _issueScrollPos;
    bool _autoScroll = true;
    bool _settingsFoldout = false;     // collapsed by default — health is the priority
    bool _logFoldout = false;          // collapsed by default — raw log is noise
    bool _metricsFoldout = true;

    DateTime _trainingStartTime;
    bool _mqttConnected;
    double _lastMqttCheck;
    double _lastTick;

    int _heartbeatCount;
    int _episodeCount;
    float _lastEpisodeReward;
    float _rollingReward;

    bool _sleepHeld;

    // Persisted across play-mode transitions
    const string TrainingActiveKey = "TrainingController_TrainingActive";
    const string InferenceModeKey = "TrainingController_InferenceMode";

    bool TrainingActive
    {
        get => SessionState.GetBool(TrainingActiveKey, false);
        set => SessionState.SetBool(TrainingActiveKey, value);
    }

    bool InferenceMode
    {
        get => SessionState.GetBool(InferenceModeKey, false);
        set => SessionState.SetBool(InferenceModeKey, value);
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
        window.minSize = new Vector2(420, 600);
    }

    void OnEnable()
    {
        LoadOrCreateSettings();
        _processManager = new PythonProcessManager();
        _metricsParser = new TrainingMetricsParser();
        _health = new TrainingHealthEvaluator();
        _mqtt = new EditorMqttListener();

        _processManager.OnOutputLine += HandleOutput;
        _processManager.OnErrorLine += HandleError;
        _processManager.OnExited += HandleExited;

        _metricsParser.OnHealthMarker += s => _health.NotifyHealthMarker(s);
        _mqtt.OnEpisodeEnd += OnEpisodeEnd;
        _mqtt.OnHeartbeat += OnHeartbeat;
        _mqtt.OnConnectionChanged += OnMqttConnectionChanged;

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

        _mqtt?.Stop();
        _mqtt = null;

        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.update -= OnEditorUpdate;

        HoldSleep(false);
    }

    void OnEditorUpdate()
    {
        double now = EditorApplication.timeSinceStartup;

        _mqtt?.Pump();

        // Tick health evaluator and MQTT status every ~0.25s
        if (now - _lastTick > 0.25)
        {
            _lastTick = now;
            _health.Tick();

            if (_processManager != null && _processManager.IsRunning)
            {
                // Repaint frequently while running so the snapshot row stays live.
                Repaint();
            }
        }

        // Lightweight TCP probe every 2s when not actively listening
        if (now - _lastMqttCheck > 2.0)
        {
            _lastMqttCheck = now;
            CheckMqttConnection();
            _health.NotifyMqttConnected(_mqttConnected);
        }
    }

    void OnPlayModeChanged(PlayModeStateChange state)
    {
        bool isRunning = _processManager != null && _processManager.IsRunning;

        if (state == PlayModeStateChange.EnteredPlayMode && (TrainingActive || InferenceMode))
        {
            EditorApplication.delayCall += ConfigureSceneAndLaunch;
        }
        else if (state == PlayModeStateChange.ExitingPlayMode && (TrainingActive || InferenceMode))
        {
            AddLog("Exiting Play mode, stopping process...", LogType.Warning);
            if (_processManager != null && _processManager.IsRunning)
                _processManager.Stop();
            TrainingActive = false;
            InferenceMode = false;
            HoldSleep(false);
            _health.StopTraining();
            _mqtt?.Stop();
        }
        else if (state == PlayModeStateChange.ExitingEditMode && isRunning && !TrainingActive && !InferenceMode)
        {
            AddLog("Stopping training before entering Play mode...", LogType.Warning);
            _processManager.Stop();
        }
    }

    void ConfigureSceneAndLaunch()
    {
        AddLog("Configuring scene for training...", LogType.Log);

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
            if (trainingBridge.vehicle == null)
                trainingBridge.vehicle = UnityEngine.Object.FindFirstObjectByType<VehicleController>();
            if (trainingBridge.raycastSensor == null)
                trainingBridge.raycastSensor = UnityEngine.Object.FindFirstObjectByType<RaycastSensor>();
            if (trainingBridge.obstacleCourse == null)
                trainingBridge.obstacleCourse = UnityEngine.Object.FindFirstObjectByType<ObstacleCourse>();
            trainingBridge.enabled = true;
            AddLog("TrainingBridge enabled", LogType.Log);
        }
        else
        {
            AddLog("Warning: TrainingBridge not found in scene!", LogType.Warning);
        }

        if (remoteInput != null)
        {
            if (broker != null && broker.remoteInput == null)
                broker.remoteInput = remoteInput;
            remoteInput.enabled = true;
            AddLog("RemoteDriveInput enabled", LogType.Log);
        }
        else
        {
            AddLog("Warning: RemoteDriveInput not found in scene!", LogType.Warning);
        }

        if (manualInput != null)
        {
            manualInput.enabled = false;
            AddLog("ManualDriveInput disabled", LogType.Log);
        }

        // Force real time — no acceleration, ever.
        Time.timeScale = 1f;
        Time.fixedDeltaTime = 0.02f;

        // Start the Editor MQTT listener now that the broker is up
        _mqtt.Start(_settings.mqttHost, _settings.mqttPort);

        if (InferenceMode)
        {
            // Inference: process was already started in StartInference()
            AddLog("Inference scene ready.", LogType.Log);
            return;
        }

        // Training: start Python AFTER domain reload (started by EnterPlaymode) finishes.
        AddLog("Starting Python training process...", LogType.Log);
        if (!_processManager.Start(_settings))
        {
            AddLog("Failed to start Python training process", LogType.Error);
            TrainingActive = false;
            HoldSleep(false);
            EditorApplication.ExitPlaymode();
            return;
        }
        _health.StartTraining();
        AddLog("Python process started. Waiting for environment connection...", LogType.Log);
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

    // ─── GUI ───────────────────────────────────────────────────────────

    void OnGUI()
    {
        DrawToolbar();
        DrawSetupWarning();
        DrawHealthBanner();
        DrawSnapshotRow();

        EditorGUILayout.Space(5);

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
        DrawSettingsSection();
        EditorGUILayout.Space(5);
        DrawMetricsSection();
        EditorGUILayout.Space(5);
        DrawIssueLog();
        EditorGUILayout.Space(5);
        DrawLogSection();
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(5);
        DrawControls();
    }

    /// <summary>
    /// Shows a one-line warning bar when tensorboard is missing from the venv,
    /// with a button that opens the Setup window directly.
    /// </summary>
    void DrawSetupWarning()
    {
        if (_settings == null) return;
        string venvPath = _settings.GetAbsoluteVenvPath();
        if (SetupEditorWindow.IsTensorboardInstalled(venvPath)) return;

        var rect = GUILayoutUtility.GetRect(0f, 26f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, new Color(0.75f, 0.40f, 0.10f));

        float btnW = 130f;
        var labelRect = new Rect(rect.x + 8, rect.y + 4, rect.width - btnW - 16, rect.height - 8);
        var btnRect   = new Rect(rect.xMax - btnW - 6, rect.y + 3, btnW, rect.height - 6);

        GUI.Label(labelRect, "⚠  Python dependencies missing — training will crash.",
            new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.white } });

        if (GUI.Button(btnRect, "Install Now", EditorStyles.miniButton))
            SetupEditorWindow.ShowWindow();
    }

    void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        bool isRunning = IsBusy();
        string label = isRunning ? (InferenceMode ? "INFERENCE" : "TRAINING") : "IDLE";
        Color color = isRunning
            ? (InferenceMode ? new Color(0.4f, 0.8f, 1f) : new Color(0.2f, 0.85f, 0.3f))
            : Color.gray;
        var style = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = color } };
        EditorGUILayout.LabelField($"[{label}]", style, GUILayout.Width(100));

        if (isRunning)
        {
            TimeSpan duration = DateTime.Now - _trainingStartTime;
            EditorGUILayout.LabelField($"Duration: {duration:hh\\:mm\\:ss}", GUILayout.Width(140));
        }

        GUILayout.FlexibleSpace();

        Color mqttColor = _mqttConnected ? new Color(0.2f, 0.8f, 0.3f) : Color.red;
        var mqttStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = mqttColor } };
        EditorGUILayout.LabelField(_mqttConnected ? "MQTT ●" : "MQTT ●", mqttStyle, GUILayout.Width(60));

        if (GUILayout.Button("Check", EditorStyles.toolbarButton, GUILayout.Width(50)))
            CheckMqttConnection();

        EditorGUILayout.EndHorizontal();
    }

    void DrawHealthBanner()
    {
        Color bg = HealthColor(_health.CurrentState);
        Color text = _health.CurrentState == TrainingHealthEvaluator.HealthState.Warning
            ? Color.black : Color.white;

        var bannerRect = GUILayoutUtility.GetRect(0f, 36f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(bannerRect, bg);

        var labelStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 13,
            normal = { textColor = text },
        };

        string text2 = $"{_health.CurrentState.ToString().ToUpperInvariant()} — {_health.CurrentMessage}";
        GUI.Label(bannerRect, text2, labelStyle);
    }

    static Color HealthColor(TrainingHealthEvaluator.HealthState s) => s switch
    {
        TrainingHealthEvaluator.HealthState.Healthy => new Color(0.18f, 0.62f, 0.28f),
        TrainingHealthEvaluator.HealthState.Warning => new Color(0.95f, 0.78f, 0.18f),
        TrainingHealthEvaluator.HealthState.Critical => new Color(0.85f, 0.22f, 0.22f),
        TrainingHealthEvaluator.HealthState.Disconnected => new Color(0.50f, 0.50f, 0.50f),
        _ => new Color(0.30f, 0.30f, 0.30f),
    };

    void DrawSnapshotRow()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

        DrawSnapshot("Episodes", _episodeCount.ToString());
        DrawSnapshot("Last reward", _lastEpisodeReward.ToString("F2"));
        DrawSnapshot("Rolling reward", _rollingReward.ToString("F2"));
        DrawSnapshot("Heartbeats", _heartbeatCount.ToString());

        EditorGUILayout.EndHorizontal();
    }

    void DrawSnapshot(string label, string value)
    {
        EditorGUILayout.BeginVertical(GUILayout.MinWidth(80));
        EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
        EditorGUILayout.LabelField(value, EditorStyles.boldLabel);
        EditorGUILayout.EndVertical();
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
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("totalTimesteps"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("learningRate"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("nSteps"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("batchSize"));
        EditorGUILayout.Space(3);
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("mqttHost"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("mqttPort"));
        EditorGUILayout.Space(3);
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("savePath"));
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("logDir"));

        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("Resume Training", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PropertyField(_serializedSettings.FindProperty("resumeModelPath"),
            new GUIContent("Model File (.zip)"));
        if (GUILayout.Button("Browse...", GUILayout.Width(70)))
        {
            string picked = EditorUtility.OpenFilePanel("Select Model .zip", "", "zip");
            if (!string.IsNullOrEmpty(picked))
            {
                _settings.resumeModelPath = picked;
                EditorUtility.SetDirty(_settings);
            }
        }
        if (GUILayout.Button("Clear", GUILayout.Width(50)))
        {
            _settings.resumeModelPath = "";
            EditorUtility.SetDirty(_settings);
        }
        EditorGUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_settings.resumeModelPath))
        {
            if (!File.Exists(_settings.resumeModelPath))
                EditorGUILayout.HelpBox("Model file not found at the specified path.", MessageType.Error);
            else
                EditorGUILayout.HelpBox($"Will resume from: {Path.GetFileName(_settings.resumeModelPath)}", MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox("No model selected — will train from scratch.", MessageType.None);
        }

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

        EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));

        if (data == null || data.Count < 2)
        {
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

    void DrawIssueLog()
    {
        EditorGUILayout.LabelField($"Issue Log ({_health.History.Count})", EditorStyles.boldLabel);

        if (_health.History.Count == 0)
        {
            EditorGUILayout.HelpBox("No health events yet.", MessageType.None);
            return;
        }

        Rect boxRect = EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(120));
        _issueScrollPos = EditorGUILayout.BeginScrollView(_issueScrollPos);

        for (int i = _health.History.Count - 1; i >= 0; i--)
        {
            var issue = _health.History[i];
            Color c = HealthColor(issue.Level);
            var s = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = c } };
            EditorGUILayout.LabelField(
                $"[{issue.Time:HH:mm:ss}] {issue.Level,-12} {issue.Message}", s);
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    void DrawLogSection()
    {
        EditorGUILayout.BeginHorizontal();
        _logFoldout = EditorGUILayout.Foldout(_logFoldout, $"Raw stdout ({_logEntries.Count})", true);
        GUILayout.FlexibleSpace();
        _autoScroll = GUILayout.Toggle(_autoScroll, "Auto-scroll", GUILayout.Width(80));
        if (GUILayout.Button("Clear", GUILayout.Width(50)))
        {
            _logEntries.Clear();
            _metricsParser.Clear();
        }
        EditorGUILayout.EndHorizontal();

        if (!_logFoldout) return;

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

        bool busy = IsBusy();

        GUI.enabled = !busy;
        if (GUILayout.Button("Start Training", GUILayout.Width(160), GUILayout.Height(34)))
            StartTraining();

        if (GUILayout.Button("Run Model", GUILayout.Width(120), GUILayout.Height(34)))
            StartInference();

        GUI.enabled = busy;
        if (GUILayout.Button("Stop", GUILayout.Width(100), GUILayout.Height(34)))
            StopAll();

        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    bool IsBusy()
    {
        bool processRunning = _processManager != null && _processManager.IsRunning;
        bool playing = EditorApplication.isPlaying;
        return processRunning || ((TrainingActive || InferenceMode) && playing);
    }

    // ─── Actions ───────────────────────────────────────────────────────

    void StartTraining()
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

        if (!string.IsNullOrEmpty(_settings.resumeModelPath) && !File.Exists(_settings.resumeModelPath))
        {
            EditorUtility.DisplayDialog("Model File Not Found",
                $"The resume model file could not be found:\n{_settings.resumeModelPath}", "OK");
            return;
        }

        TrainingActive = true;
        InferenceMode = false;
        HoldSleep(true, "training");
        _logEntries.Clear();
        _metricsParser.Clear();
        _health.Clear();
        _heartbeatCount = 0;
        _episodeCount = 0;
        _lastEpisodeReward = 0f;
        _rollingReward = 0f;
        _trainingStartTime = DateTime.Now;

        AddLog(_settings.totalTimesteps > 0
            ? $"Starting single-env training, {_settings.totalTimesteps} timesteps"
            : "Starting single-env training, unlimited timesteps", LogType.Log);
        if (!string.IsNullOrEmpty(_settings.resumeModelPath))
            AddLog($"Resuming from: {Path.GetFileName(_settings.resumeModelPath)}", LogType.Log);
        AddLog("Entering Play mode — Python will start after scene loads...", LogType.Log);

        EditorApplication.delayCall += EditorApplication.EnterPlaymode;
    }

    void StartInference()
    {
        if (_processManager == null || _settings == null)
        {
            Debug.LogError("[TrainingController] Not initialized properly");
            return;
        }

        string modelPath = EditorUtility.OpenFilePanel("Select Trained Model", "", "zip");
        if (string.IsNullOrEmpty(modelPath))
            return;

        if (!File.Exists(modelPath))
        {
            EditorUtility.DisplayDialog("Model Not Found", $"Model file not found:\n{modelPath}", "OK");
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

        InferenceMode = true;
        TrainingActive = false;
        HoldSleep(true, "inference");
        _logEntries.Clear();
        _metricsParser.Clear();
        _health.Clear();
        _trainingStartTime = DateTime.Now;

        AddLog($"Running model: {Path.GetFileName(modelPath)}", LogType.Log);

        string venvPath = _settings.GetAbsoluteVenvPath();
        string scriptPath = _settings.GetAbsoluteAiDriverPath();
        string workingDir = _settings.GetWorkingDirectory();
        string args = _settings.BuildInferenceArgs(modelPath);

        if (!_processManager.StartScript(venvPath, scriptPath, workingDir, args))
        {
            AddLog("Failed to start inference process", LogType.Error);
            InferenceMode = false;
            HoldSleep(false);
            return;
        }

        EditorApplication.delayCall += () =>
        {
            AddLog("Entering Play mode...", LogType.Log);
            EditorApplication.EnterPlaymode();
        };
    }

    void StopAll()
    {
        if (_processManager != null && _processManager.IsRunning)
            _processManager.Stop();
        if ((TrainingActive || InferenceMode) && EditorApplication.isPlaying)
            EditorApplication.ExitPlaymode();
        TrainingActive = false;
        InferenceMode = false;
        HoldSleep(false);
        _health.StopTraining();
        _mqtt?.Stop();
    }

    // ─── Process callbacks ─────────────────────────────────────────────

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
            ? "Training process exited cleanly"
            : $"Training process exited with code {exitCode}";

        AddLog(msg, exitCode == 0 ? LogType.Log : LogType.Error);
        _health.NotifyProcessExited(exitCode);

        // Auto-restart: if training was still active and this wasn't a deliberate
        // clean stop (exit 0 via the Stop button sets TrainingActive=false first),
        // relaunch Python so training continues after a crash or heartbeat-triggered kill.
        if (TrainingActive && !InferenceMode && exitCode != 0)
        {
            AddLog("Python process died unexpectedly — restarting in 2 s...", LogType.Warning);
            EditorApplication.delayCall += () =>
            {
                if (!TrainingActive || InferenceMode) return; // user stopped in the meantime
                System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
                    EditorApplication.delayCall += () =>
                    {
                        if (!TrainingActive || InferenceMode || _processManager.IsRunning) return;
                        AddLog("Restarting Python training process...", LogType.Log);
                        if (!_processManager.Start(_settings))
                        {
                            AddLog("Auto-restart failed — stopping training.", LogType.Error);
                            TrainingActive = false;
                            HoldSleep(false);
                            EditorApplication.ExitPlaymode();
                        }
                        else
                        {
                            HoldSleep(true);
                            _health.StartTraining();
                            AddLog("Python process restarted.", LogType.Log);
                        }
                    }
                );
            };
        }
        else
        {
            HoldSleep(false);
        }

        // Beep on a non-zero exit so the user notices even if focused elsewhere.
        if (exitCode != 0)
            EditorApplication.Beep();
    }

    void OnEpisodeEnd(int episode, float reward, int steps, string reason)
    {
        _episodeCount = Math.Max(_episodeCount, episode + 1);
        _lastEpisodeReward = reward;
        // EMA over ~last 20 episodes
        _rollingReward = _rollingReward == 0f ? reward : _rollingReward * 0.9f + reward * 0.1f;
        _health.NotifyEpisodeEnd(reward);
        Repaint();
    }

    void OnHeartbeat(int episode, int steps, float reward)
    {
        _heartbeatCount += 1;
        _health.NotifyHeartbeat();
        // Heartbeat counts as an "obs received" signal too.
        _health.NotifyObs();
    }

    void OnMqttConnectionChanged(bool connected)
    {
        _mqttConnected = connected;
        _health.NotifyMqttConnected(connected);
        Repaint();
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    void AddLog(string message, LogType type)
    {
        _logEntries.Add(new LogEntry
        {
            Message = message,
            Type = type,
            Time = DateTime.Now,
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

    void HoldSleep(bool hold, string reason = null)
    {
        if (hold && !_sleepHeld)
        {
            SleepPreventer.Acquire(reason ?? "training");
            _sleepHeld = true;
        }
        else if (!hold && _sleepHeld)
        {
            SleepPreventer.Release();
            _sleepHeld = false;
        }
    }
}
#endif
