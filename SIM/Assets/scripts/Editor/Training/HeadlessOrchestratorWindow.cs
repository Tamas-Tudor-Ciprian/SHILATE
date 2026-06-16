#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Orchestrator window for launching and monitoring multiple headless Unity training instances.
/// Each environment runs as an independent headless Unity process + Python trainer pair,
/// communicating over separate MQTT topic prefixes (env0, env1, …).
///
/// Access via menu: SHILATE → Headless Orchestrator.
/// Build the Unity headless target first: File → Build Settings → Server Build (Linux x86_64).
/// </summary>
public class HeadlessOrchestratorWindow : EditorWindow
{
    // ─── Per-environment state ────────────────────────────────────────────────

    class EnvSlot
    {
        public readonly string Prefix;
        public readonly PythonProcessManager Python = new();
        public readonly EditorMqttListener Mqtt = new();
        public readonly TrainingHealthEvaluator Health = new();
        public readonly TrainingMetricsParser Metrics = new();
        public readonly List<LogEntry> Log = new();

        public Process UnityProcess;

        // Snapshot
        public int EpisodeCount;
        public float LastReward;
        public float RollingReward;
        public int HeartbeatCount;

        // UI
        public bool Expanded = true;
        public bool MetricsFoldout = true;
        public bool LogFoldout;
        public Vector2 IssueScroll;
        public Vector2 LogScroll;

        public bool UnityAlive  => UnityProcess != null && !UnityProcess.HasExited;
        public bool PythonAlive => Python.IsRunning;

        public EnvSlot(string prefix) => Prefix = prefix;
    }

    struct LogEntry
    {
        public string Message;
        public LogType Type;
        public DateTime Time;
    }

    // ─── Window state ─────────────────────────────────────────────────────────

    TrainingSettings _settings;
    readonly List<EnvSlot> _envs = new();
    Vector2 _scrollPos;
    double _lastTick;
    bool _settingsFoldout = true;
    bool _mqttConnected;
    bool _sleepHeld;

    const string SettingsAssetPath = "Assets/Settings/TrainingSettings.asset";
    const int MaxLogEntries = 200;

    [MenuItem("SHILATE/Headless Orchestrator")]
    public static void ShowWindow()
    {
        var w = GetWindow<HeadlessOrchestratorWindow>("Headless Orchestrator");
        w.minSize = new Vector2(520, 640);
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    void OnEnable()
    {
        LoadSettings();
        EditorApplication.update += OnUpdate;
    }

    void OnDisable()
    {
        EditorApplication.update -= OnUpdate;
        foreach (var env in _envs)
            TeardownSlot(env, log: false);
        _envs.Clear();
        HoldSleep(false);
    }

    void OnUpdate()
    {
        double now = EditorApplication.timeSinceStartup;

        foreach (var env in _envs)
            env.Mqtt.Pump();

        if (now - _lastTick > 0.25)
        {
            _lastTick = now;
            foreach (var env in _envs)
                env.Health.Tick();
            if (AnyRunning())
                Repaint();
        }
    }

    bool AnyRunning() => _envs.Exists(e => e.PythonAlive || e.UnityAlive);

    // ─── GUI ──────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        DrawToolbar();
        EditorGUILayout.Space(4);

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        DrawSettingsSection();
        EditorGUILayout.Space(6);

        if (_envs.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "No environments running.\nConfigure settings above and press Launch All.",
                MessageType.Info);
        }
        else
        {
            foreach (var env in _envs)
                DrawEnvPanel(env);
        }

        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(4);
        DrawControls();
    }

    void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        bool running = AnyRunning();
        string label = running ? $"RUNNING  ({_envs.Count} env{(_envs.Count != 1 ? "s" : "")})" : "IDLE";
        Color labelColor = running ? new Color(0.2f, 0.85f, 0.3f) : Color.gray;
        var labelStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = labelColor } };
        EditorGUILayout.LabelField($"[{label}]", labelStyle, GUILayout.Width(180));

        GUILayout.FlexibleSpace();

        Color mqttColor = _mqttConnected ? new Color(0.2f, 0.8f, 0.3f) : Color.red;
        var mqttStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = mqttColor } };
        EditorGUILayout.LabelField(_mqttConnected ? "MQTT ●" : "MQTT ●", mqttStyle, GUILayout.Width(60));

        if (GUILayout.Button("Check", EditorStyles.toolbarButton, GUILayout.Width(50)))
            CheckMqtt();

        EditorGUILayout.EndHorizontal();
    }

    void DrawSettingsSection()
    {
        _settingsFoldout = EditorGUILayout.Foldout(_settingsFoldout, "Settings", true);
        if (!_settingsFoldout || _settings == null) return;

        EditorGUI.indentLevel++;
        var so = new SerializedObject(_settings);
        so.Update();

        EditorGUILayout.LabelField("Headless Build", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PropertyField(so.FindProperty("unityBuildPath"),
            new GUIContent("Unity Executable", "Path to the headless Unity build (.x86_64 on Linux)"));
        if (GUILayout.Button("Browse…", GUILayout.Width(70)))
        {
            string picked = EditorUtility.OpenFilePanel("Select Unity headless build", "", "");
            if (!string.IsNullOrEmpty(picked))
            {
                _settings.unityBuildPath = picked;
                EditorUtility.SetDirty(_settings);
            }
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.PropertyField(so.FindProperty("numHeadlessEnvs"),
            new GUIContent("Num Environments"));

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Python", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("venvPath"));
        EditorGUILayout.PropertyField(so.FindProperty("trainScriptPath"));
        EditorGUILayout.PropertyField(so.FindProperty("learningRate"));
        EditorGUILayout.PropertyField(so.FindProperty("nSteps"));
        EditorGUILayout.PropertyField(so.FindProperty("batchSize"));

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("MQTT & Output", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("mqttHost"));
        EditorGUILayout.PropertyField(so.FindProperty("mqttPort"));
        EditorGUILayout.PropertyField(so.FindProperty("savePath"));
        EditorGUILayout.PropertyField(so.FindProperty("logDir"));

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Resume Training (all envs)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PropertyField(so.FindProperty("headlessResumeModelPath"),
            new GUIContent("Model File (.zip)"));
        if (GUILayout.Button("Browse…", GUILayout.Width(70)))
        {
            string picked = EditorUtility.OpenFilePanel("Select Model .zip", "", "zip");
            if (!string.IsNullOrEmpty(picked))
            {
                _settings.headlessResumeModelPath = picked;
                EditorUtility.SetDirty(_settings);
            }
        }
        if (GUILayout.Button("Clear", GUILayout.Width(44)))
        {
            _settings.headlessResumeModelPath = "";
            EditorUtility.SetDirty(_settings);
        }
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(_settings.headlessResumeModelPath))
        {
            if (!File.Exists(_settings.headlessResumeModelPath))
                EditorGUILayout.HelpBox("Model file not found.", MessageType.Error);
            else
                EditorGUILayout.HelpBox(
                    $"Resuming from: {Path.GetFileName(_settings.headlessResumeModelPath)}",
                    MessageType.Info);
        }

        so.ApplyModifiedProperties();
        EditorGUI.indentLevel--;
    }

    void DrawEnvPanel(EnvSlot env)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        // ── Header row ──
        EditorGUILayout.BeginHorizontal();

        Color healthColor = HealthColor(env.Health.CurrentState);
        string pyDot    = env.PythonAlive ? "●" : "○";
        string unityDot = env.UnityAlive  ? "●" : "○";
        string headerText = $"{env.Prefix}   Python {pyDot}   Unity {unityDot}";

        env.Expanded = EditorGUILayout.Foldout(env.Expanded, headerText, true,
            new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold });

        GUILayout.FlexibleSpace();

        var badgeStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            normal    = { textColor = healthColor },
            fontStyle = FontStyle.Bold,
        };
        EditorGUILayout.LabelField(
            env.Health.CurrentState.ToString().ToUpperInvariant(),
            badgeStyle, GUILayout.Width(90));

        if (env.PythonAlive || env.UnityAlive)
        {
            if (GUILayout.Button("Stop", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                TeardownSlot(env);
                Repaint();
            }
        }
        EditorGUILayout.EndHorizontal();

        if (!env.Expanded)
        {
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
            return;
        }

        // ── Health banner ──
        Rect bannerRect = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(bannerRect, healthColor);
        GUI.Label(bannerRect, env.Health.CurrentMessage,
            new GUIStyle(EditorStyles.miniLabel)
            {
                alignment  = TextAnchor.MiddleCenter,
                fontStyle  = FontStyle.Bold,
                normal     = { textColor = Color.white },
            });

        EditorGUILayout.Space(2);

        // ── Snapshot row ──
        EditorGUILayout.BeginHorizontal();
        DrawSnap("Episodes",    env.EpisodeCount.ToString());
        DrawSnap("Last Reward", env.LastReward.ToString("F2"));
        DrawSnap("Rolling",     env.RollingReward.ToString("F2"));
        DrawSnap("Heartbeats",  env.HeartbeatCount.ToString());
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(2);

        // ── Metric graphs (collapsible) ──
        EditorGUILayout.BeginHorizontal();
        env.MetricsFoldout = EditorGUILayout.Foldout(env.MetricsFoldout, "Metrics", true);
        EditorGUILayout.EndHorizontal();
        if (env.MetricsFoldout)
        {
            EditorGUILayout.BeginHorizontal();
            DrawMiniGraph("Episode Reward",  env.Metrics.RewardHistory,     env.Metrics.LatestReward);
            DrawMiniGraph("Policy Loss",     env.Metrics.LossHistory,       env.Metrics.LatestLoss);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            DrawMiniGraph("Value Loss",      env.Metrics.ValueLossHistory,  env.Metrics.LatestValueLoss);
            DrawMiniGraph("KL Divergence",   env.Metrics.KLHistory,         env.Metrics.LatestKL);
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(2);

        // ── Issue log ──
        if (env.Health.History.Count > 0)
        {
            env.IssueScroll = EditorGUILayout.BeginScrollView(
                env.IssueScroll, GUILayout.Height(54));
            for (int i = env.Health.History.Count - 1; i >= 0; i--)
            {
                var issue = env.Health.History[i];
                var s = new GUIStyle(EditorStyles.miniLabel)
                    { normal = { textColor = HealthColor(issue.Level) } };
                EditorGUILayout.LabelField(
                    $"[{issue.Time:HH:mm:ss}] {issue.Level,-12} {issue.Message}", s);
            }
            EditorGUILayout.EndScrollView();
        }

        EditorGUILayout.Space(2);

        // ── Raw stdout (collapsed) ──
        EditorGUILayout.BeginHorizontal();
        env.LogFoldout = EditorGUILayout.Foldout(
            env.LogFoldout, $"stdout ({env.Log.Count})", true);
        if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(44)))
        {
            env.Log.Clear();
            env.Metrics.Clear();
        }
        EditorGUILayout.EndHorizontal();

        if (env.LogFoldout)
        {
            env.LogScroll = EditorGUILayout.BeginScrollView(
                env.LogScroll, GUILayout.Height(110));
            foreach (var entry in env.Log)
            {
                Color c = entry.Type switch
                {
                    LogType.Error   => new Color(1f, 0.4f, 0.4f),
                    LogType.Warning => new Color(1f, 0.9f, 0.4f),
                    _               => new Color(0.85f, 0.85f, 0.85f),
                };
                EditorGUILayout.LabelField(
                    $"[{entry.Time:HH:mm:ss}] {entry.Message}",
                    new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = c } });
            }
            EditorGUILayout.EndScrollView();
            env.LogScroll.y = float.MaxValue;
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(3);
    }

    void DrawSnap(string label, string value)
    {
        EditorGUILayout.BeginVertical(GUILayout.MinWidth(80));
        EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
        EditorGUILayout.LabelField(value, EditorStyles.boldLabel);
        EditorGUILayout.EndVertical();
    }

    void DrawMiniGraph(string title, IReadOnlyList<TrainingMetricsParser.Metric> data, float? current)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(62));
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel, GUILayout.Width(120));
        EditorGUILayout.LabelField(
            current.HasValue ? current.Value.ToString("F3") : "—",
            EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        Rect r = GUILayoutUtility.GetRect(100, 36, GUILayout.ExpandWidth(true));
        DrawGraph(r, data);
        EditorGUILayout.EndVertical();
    }

    void DrawGraph(Rect rect, IReadOnlyList<TrainingMetricsParser.Metric> data)
    {
        if (Event.current.type != EventType.Repaint) return;

        EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
        if (data == null || data.Count < 2) return;

        float min = float.MaxValue, max = float.MinValue;
        foreach (var m in data) { min = Mathf.Min(min, m.Value); max = Mathf.Max(max, m.Value); }
        float range = Mathf.Max(max - min, 0.0001f);

        Handles.BeginGUI();
        Handles.color = new Color(0.3f, 0.7f, 1f);

        int step = Mathf.Max(1, data.Count / (int)Mathf.Max(1, rect.width));
        Vector3 prev = Vector3.zero;
        bool first = true;

        for (int i = 0; i < data.Count; i += step)
        {
            float x = rect.x + (float)i / (data.Count - 1) * rect.width;
            float y = rect.y + rect.height - (data[i].Value - min) / range * rect.height;
            var pt = new Vector3(x, Mathf.Clamp(y, rect.y, rect.y + rect.height), 0);
            if (!first) Handles.DrawLine(prev, pt);
            prev = pt;
            first = false;
        }

        Handles.EndGUI();
    }

    void DrawControls()
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        bool running = AnyRunning();

        GUI.enabled = !running && _settings != null;
        if (GUILayout.Button("Launch All", GUILayout.Width(160), GUILayout.Height(34)))
            LaunchAll();

        GUI.enabled = running;
        if (GUILayout.Button("Stop All", GUILayout.Width(120), GUILayout.Height(34)))
            StopAll();

        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    // ─── Launch / Stop ────────────────────────────────────────────────────────

    void LaunchAll()
    {
        if (_settings == null) return;

        int numEnvs = Mathf.Max(1, _settings.numHeadlessEnvs);
        bool hasUnityBuild = !string.IsNullOrEmpty(_settings.unityBuildPath)
                             && File.Exists(_settings.unityBuildPath);

        if (!hasUnityBuild)
        {
            if (!EditorUtility.DisplayDialog("No Unity Build Configured",
                "Unity build executable not found or not set.\n\n" +
                "Only Python trainers will be launched — start the Unity headless " +
                "instances manually (e.g. via run-headless.sh) before pressing Launch All.\n\n" +
                "Continue launching Python only?",
                "Launch Python Only", "Cancel"))
                return;
        }

        if (!CheckMqtt())
        {
            if (!EditorUtility.DisplayDialog("MQTT Unreachable",
                $"Cannot connect to broker at {_settings.mqttHost}:{_settings.mqttPort}.\n\n" +
                "Continue anyway?", "Continue", "Cancel"))
                return;
        }

        foreach (var e in _envs) TeardownSlot(e, log: false);
        _envs.Clear();

        string absVenv   = _settings.GetAbsoluteVenvPath();
        string absScript = _settings.GetAbsoluteTrainScriptPath();
        string workDir   = _settings.GetWorkingDirectory();
        string projRoot  = Path.GetDirectoryName(Application.dataPath);

        for (int i = 0; i < numEnvs; i++)
        {
            string prefix = $"env{i}";
            var slot = new EnvSlot(prefix);
            _envs.Add(slot);

            // Wire events
            slot.Python.OnOutputLine += line  => HandleOutput(slot, line);
            slot.Python.OnErrorLine  += line  => HandleError(slot, line);
            slot.Python.OnExited     += code  => HandleExited(slot, code);
            slot.Mqtt.OnEpisodeEnd   += (ep, rew, steps, reason)
                                              => HandleEpisodeEnd(slot, ep, rew, steps, reason);
            slot.Mqtt.OnHeartbeat    += (ep, steps, rew)
                                              => HandleHeartbeat(slot);
            slot.Mqtt.OnConnectionChanged += conn =>
            {
                slot.Health.NotifyMqttConnected(conn);
                Repaint();
            };
            slot.Metrics.OnHealthMarker += sig => slot.Health.NotifyHealthMarker(sig);

            // Unity headless process
            if (hasUnityBuild)
                LaunchUnity(slot);

            // Python trainer — stagger by 2 s per env so Unity has time to bind
            string args = BuildTrainArgs(prefix, projRoot);
            int delay   = hasUnityBuild ? 2000 * (i + 1) : 500 * i;
            SchedulePython(slot, absVenv, absScript, workDir, args, delay);

            // MQTT listener
            slot.Mqtt.Start(_settings.mqttHost, _settings.mqttPort, prefix);
            slot.Health.StartTraining();
        }

        HoldSleep(true);
        Debug.Log($"[Orchestrator] Launching {numEnvs} environment(s)...");
        Repaint();
    }

    void LaunchUnity(EnvSlot slot)
    {
        string logPath = GetEnvLogPath(slot.Prefix, "unity.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath));

        try
        {
            slot.UnityProcess = Process.Start(new ProcessStartInfo
            {
                FileName        = _settings.unityBuildPath,
                Arguments       = $"-batchmode -nographics -logFile \"{logPath}\" -envPrefix {slot.Prefix}",
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
            AddLog(slot, $"Unity started (PID {slot.UnityProcess.Id}) → {logPath}", LogType.Log);
        }
        catch (Exception ex)
        {
            AddLog(slot, $"Failed to launch Unity: {ex.Message}", LogType.Error);
            slot.Health.NotifyProcessExited(1);
        }
    }

    void SchedulePython(EnvSlot slot, string venv, string script, string workDir, string args, int delayMs)
    {
        if (delayMs <= 0)
        {
            DoLaunchPython(slot, venv, script, workDir, args);
            return;
        }
        System.Threading.Tasks.Task.Delay(delayMs).ContinueWith(_ =>
            EditorApplication.delayCall += () =>
                DoLaunchPython(slot, venv, script, workDir, args));
    }

    void DoLaunchPython(EnvSlot slot, string venv, string script, string workDir, string args)
    {
        if (!slot.Python.StartScript(venv, script, workDir, args))
        {
            AddLog(slot, "Failed to start Python trainer", LogType.Error);
            slot.Health.NotifyProcessExited(1);
        }
        else
        {
            AddLog(slot, $"Python started — {slot.Prefix}", LogType.Log);
        }
        Repaint();
    }

    string BuildTrainArgs(string prefix, string projectRoot)
    {
        string absSave = Path.GetFullPath(Path.Combine(
            projectRoot, _settings.savePath.Replace('/', Path.DirectorySeparatorChar), prefix));
        string absLog  = Path.GetFullPath(Path.Combine(
            projectRoot, _settings.logDir.Replace('/', Path.DirectorySeparatorChar), prefix));

        return $"--env-prefix {prefix} " +
               $"--learning-rate {_settings.learningRate.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
               $"--n-steps {_settings.nSteps} " +
               $"--batch-size {_settings.batchSize} " +
               $"--mqtt-host {_settings.mqttHost} " +
               $"--mqtt-port {_settings.mqttPort} " +
               $"--save-path \"{absSave}\" " +
               $"--log-dir \"{absLog}\"" +
               (string.IsNullOrEmpty(_settings.headlessResumeModelPath)
                   ? ""
                   : $" --resume-model \"{_settings.headlessResumeModelPath}\"");
    }

    string GetEnvLogPath(string prefix, string filename)
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        return Path.Combine(projectRoot, "logs", "headless", prefix, filename);
    }

    void StopAll()
    {
        foreach (var env in _envs)
            TeardownSlot(env);
        HoldSleep(false);
        Repaint();
    }

    void TeardownSlot(EnvSlot slot, bool log = true)
    {
        if (slot.Python.IsRunning)
            slot.Python.Stop();

        if (slot.UnityAlive)
        {
            try { slot.UnityProcess.Kill(); } catch { }
            slot.UnityProcess = null;
        }

        slot.Mqtt.Stop();
        slot.Health.StopTraining();

        if (log) AddLog(slot, "Stopped", LogType.Log);
    }

    // ─── Event handlers ───────────────────────────────────────────────────────

    void HandleOutput(EnvSlot slot, string line)
    {
        AddLog(slot, line, LogType.Log);
        slot.Metrics.ParseLine(line);
        Repaint();
    }

    void HandleError(EnvSlot slot, string line)
    {
        AddLog(slot, line, line.Contains("WARNING") ? LogType.Warning : LogType.Error);
        Repaint();
    }

    void HandleExited(EnvSlot slot, int code)
    {
        slot.Health.NotifyProcessExited(code);
        AddLog(slot, $"Python exited (code {code})", code == 0 ? LogType.Log : LogType.Error);
        if (code != 0) EditorApplication.Beep();
        Repaint();
    }

    void HandleEpisodeEnd(EnvSlot slot, int episode, float reward, int steps, string reason)
    {
        slot.EpisodeCount = Math.Max(slot.EpisodeCount, episode + 1);
        slot.LastReward   = reward;
        slot.RollingReward = slot.RollingReward == 0f
            ? reward : slot.RollingReward * 0.9f + reward * 0.1f;
        slot.Health.NotifyEpisodeEnd(reward);
        Repaint();
    }

    void HandleHeartbeat(EnvSlot slot)
    {
        slot.HeartbeatCount++;
        slot.Health.NotifyHeartbeat();
        slot.Health.NotifyObs();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    void AddLog(EnvSlot slot, string msg, LogType type)
    {
        slot.Log.Add(new LogEntry { Message = msg, Type = type, Time = DateTime.Now });
        while (slot.Log.Count > MaxLogEntries)
            slot.Log.RemoveAt(0);
    }

    bool CheckMqtt()
    {
        if (_settings == null) return false;
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            var r = c.BeginConnect(_settings.mqttHost, _settings.mqttPort, null, null);
            _mqttConnected = r.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500)) && c.Connected;
            c.Close();
        }
        catch { _mqttConnected = false; }
        Repaint();
        return _mqttConnected;
    }

    void LoadSettings()
    {
        _settings = AssetDatabase.LoadAssetAtPath<TrainingSettings>(SettingsAssetPath);
        if (_settings == null)
            Debug.LogWarning("[Orchestrator] TrainingSettings not found at " + SettingsAssetPath +
                             " — open SHILATE → Training Controller first to create it.");
    }

    void HoldSleep(bool hold)
    {
        if (hold && !_sleepHeld)  { SleepPreventer.Acquire("headless-training"); _sleepHeld = true; }
        if (!hold && _sleepHeld)  { SleepPreventer.Release(); _sleepHeld = false; }
    }

    static Color HealthColor(TrainingHealthEvaluator.HealthState s) => s switch
    {
        TrainingHealthEvaluator.HealthState.Healthy      => new Color(0.18f, 0.62f, 0.28f),
        TrainingHealthEvaluator.HealthState.Warning      => new Color(0.95f, 0.78f, 0.18f),
        TrainingHealthEvaluator.HealthState.Critical     => new Color(0.85f, 0.22f, 0.22f),
        TrainingHealthEvaluator.HealthState.Disconnected => new Color(0.50f, 0.50f, 0.50f),
        _                                                => new Color(0.30f, 0.30f, 0.30f),
    };
}
#endif
