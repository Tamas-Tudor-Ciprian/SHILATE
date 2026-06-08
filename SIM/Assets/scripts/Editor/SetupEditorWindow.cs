#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// One-click dependency installer for the leda-controller Python environment.
/// Access via menu: SHILATE → Setup → Install Dependencies
///
/// What it does:
///   1. Locates (or creates) the Python venv defined in TrainingSettings.
///   2. Upgrades pip silently.
///   3. Runs pip install -r requirements.txt (which includes tensorboard).
///   4. Streams all output into the window — no terminal required.
/// </summary>
public class SetupEditorWindow : EditorWindow
{
    // ─── State ─────────────────────────────────────────────────────────

    enum SetupState { Idle, Running, Success, Failed }

    SetupState _state = SetupState.Idle;
    string _stateDetail = "";

    Process _process;
    Thread _stdoutThread;
    Thread _stderrThread;
    volatile bool _processRunning;

    readonly List<LogEntry> _log = new();
    Vector2 _scrollPos;
    bool _autoScroll = true;

    TrainingSettings _settings;

    const string SettingsAssetPath = "Assets/Settings/TrainingSettings.asset";
    const int MaxLogLines = 1000;

    struct LogEntry
    {
        public string Text;
        public bool IsError;
        public DateTime Time;
    }

    // ─── Menu ──────────────────────────────────────────────────────────

    [MenuItem("SHILATE/Setup/Install Dependencies")]
    public static void ShowWindow()
    {
        var w = GetWindow<SetupEditorWindow>("SHILATE Setup");
        w.minSize = new Vector2(500, 400);
    }

    // ─── Lifecycle ─────────────────────────────────────────────────────

    void OnEnable()
    {
        _settings = AssetDatabase.LoadAssetAtPath<TrainingSettings>(SettingsAssetPath);
    }

    void OnDisable()
    {
        KillProcess();
    }

    // ─── GUI ───────────────────────────────────────────────────────────

    void OnGUI()
    {
        DrawHeader();
        EditorGUILayout.Space(4);
        DrawStatusBanner();
        EditorGUILayout.Space(4);
        DrawInfo();
        EditorGUILayout.Space(4);
        DrawLog();
        EditorGUILayout.Space(4);
        DrawControls();
    }

    void DrawHeader()
    {
        EditorGUILayout.LabelField("SHILATE — Python Environment Setup",
            new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });
        EditorGUILayout.LabelField(
            "Installs all Python dependencies (including tensorboard) into the venv.",
            EditorStyles.wordWrappedMiniLabel);
    }

    void DrawStatusBanner()
    {
        Color bg = _state switch
        {
            SetupState.Running => new Color(0.20f, 0.55f, 0.90f),
            SetupState.Success => new Color(0.18f, 0.62f, 0.28f),
            SetupState.Failed  => new Color(0.85f, 0.22f, 0.22f),
            _                  => new Color(0.30f, 0.30f, 0.30f),
        };

        string label = _state switch
        {
            SetupState.Running => $"RUNNING — {_stateDetail}",
            SetupState.Success => "SUCCESS — All dependencies installed.",
            SetupState.Failed  => $"FAILED — {_stateDetail}",
            _                  => "IDLE — Press Install to begin.",
        };

        var rect = GUILayoutUtility.GetRect(0f, 30f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, bg);
        GUI.Label(rect, label, new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white }
        });
    }

    void DrawInfo()
    {
        if (_settings == null)
        {
            EditorGUILayout.HelpBox(
                "TrainingSettings asset not found. Open the Training Controller window first (SHILATE → Training Controller).",
                MessageType.Warning);
            return;
        }

        string venvPath = _settings.GetAbsoluteVenvPath();
        string workDir  = _settings.GetWorkingDirectory();
        string reqFile  = Path.Combine(workDir, "requirements.txt");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        DrawField("Venv path",         venvPath);
        DrawField("Requirements file", reqFile);
        DrawField("Venv exists",       Directory.Exists(venvPath) ? "Yes" : "No — will be created");
        DrawField("tensorboard",       IsTensorboardInstalled(venvPath) ? "Installed ✓" : "Missing ✗");
        EditorGUILayout.EndVertical();
    }

    static void DrawField(string label, string value)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(140));
        EditorGUILayout.SelectableLabel(value, EditorStyles.miniLabel,
            GUILayout.Height(EditorGUIUtility.singleLineHeight));
        EditorGUILayout.EndHorizontal();
    }

    void DrawLog()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"Output ({_log.Count} lines)", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        _autoScroll = GUILayout.Toggle(_autoScroll, "Auto-scroll", GUILayout.Width(80));
        if (GUILayout.Button("Clear", GUILayout.Width(50)))
            _log.Clear();
        EditorGUILayout.EndHorizontal();

        var boxRect = EditorGUILayout.BeginVertical(EditorStyles.helpBox,
            GUILayout.ExpandHeight(true));
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        if (_log.Count == 0)
        {
            EditorGUILayout.LabelField("No output yet. Press Install to begin.",
                EditorStyles.centeredGreyMiniLabel);
        }
        else
        {
            foreach (var entry in _log)
            {
                Color c = entry.IsError ? new Color(1f, 0.4f, 0.4f) : new Color(0.9f, 0.9f, 0.9f);
                var prevColor = GUI.contentColor;
                GUI.contentColor = c;
                EditorGUILayout.SelectableLabel(
                    $"[{entry.Time:HH:mm:ss}] {entry.Text}",
                    EditorStyles.miniLabel,
                    GUILayout.Height(14));
                GUI.contentColor = prevColor;
            }
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        if (_autoScroll && _log.Count > 0)
            _scrollPos.y = float.MaxValue;
    }

    void DrawControls()
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        GUI.enabled = !_processRunning && _settings != null;
        if (GUILayout.Button("Install Dependencies", GUILayout.Width(180), GUILayout.Height(34)))
            RunSetup();

        GUI.enabled = _processRunning;
        if (GUILayout.Button("Cancel", GUILayout.Width(100), GUILayout.Height(34)))
            KillProcess();

        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    // ─── Setup execution ───────────────────────────────────────────────

    void RunSetup()
    {
        if (_settings == null) return;

        _log.Clear();
        _state = SetupState.Running;
        _stateDetail = "Checking environment...";

        string venvPath = _settings.GetAbsoluteVenvPath();
        string workDir  = _settings.GetWorkingDirectory();
        string reqFile  = Path.Combine(workDir, "requirements.txt");

        if (!File.Exists(reqFile))
        {
            SetFailed($"requirements.txt not found at: {reqFile}");
            return;
        }

        // If venv is missing, create it first
        if (!Directory.Exists(venvPath))
        {
            AddLog($"Venv not found at {venvPath} — creating...");
            if (!CreateVenv(venvPath))
                return;   // error already logged
        }

        string pipExe = GetPipExecutable(venvPath);
        if (!File.Exists(pipExe))
        {
            SetFailed($"pip not found in venv: {pipExe}");
            return;
        }

        // Step 1 — upgrade pip (blocking, short). Run pip directly without a
        // shell wrapper so quoting is never an issue regardless of path content.
        _stateDetail = "Upgrading pip...";
        Repaint();
        AddLog("Step 1/2: Upgrading pip...");
        if (!RunBlocking(pipExe, "install --upgrade pip", workDir, out string pipUpgradeErr))
        {
            SetFailed($"pip upgrade failed: {pipUpgradeErr}");
            return;
        }
        AddLog("pip upgrade OK.");

        // Step 2 — install requirements (async, output streamed into the window).
        // Pass the requirements file path as a direct argument to pip — no shell
        // intermediary, so there is no && quoting problem on any platform.
        _stateDetail = "Installing packages...";
        AddLog($"Step 2/2: pip install -r \"{reqFile}\"");

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = pipExe,
                    Arguments              = $"install -r \"{reqFile}\"",
                    WorkingDirectory       = workDir,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                },
                EnableRaisingEvents = true
            };

            _process.Exited += HandleExited;
            _process.Start();
            _processRunning = true;

            _stdoutThread = new Thread(ReadStdout) { IsBackground = true };
            _stderrThread = new Thread(ReadStderr) { IsBackground = true };
            _stdoutThread.Start();
            _stderrThread.Start();
        }
        catch (Exception ex)
        {
            SetFailed($"Failed to start pip: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs an executable with the given arguments synchronously.
    /// Captures stdout+stderr and appends them to the log.
    /// Returns true on exit code 0; sets errorMessage on failure.
    /// </summary>
    bool RunBlocking(string exe, string args, string workDir, out string errorMessage)
    {
        errorMessage = null;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = exe,
                    Arguments              = args,
                    WorkingDirectory       = workDir,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
                foreach (var line in stdout.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) AddLog(line.TrimEnd());

            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (var line in stderr.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) AddLog(line.TrimEnd(), isError: proc.ExitCode != 0);

            if (proc.ExitCode != 0)
            {
                errorMessage = $"exit code {proc.ExitCode}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>Creates a new venv using the system Python. Blocks until done.</summary>
    bool CreateVenv(string venvPath)
    {
        _stateDetail = "Creating venv...";
        Repaint();

        string python = FindSystemPython();
        if (python == null)
        {
            SetFailed("Python 3 not found on PATH. Install it from https://python.org.");
            return false;
        }

        AddLog($"Using Python: {python}");
        AddLog($"Creating venv at: {venvPath}");

        // Call python -m venv directly — no shell wrapper needed.
        if (!RunBlocking(python, $"-m venv \"{venvPath}\"", Path.GetTempPath(), out string err))
        {
            SetFailed($"venv creation failed: {err}");
            return false;
        }

        AddLog("Venv created successfully.");
        return true;
    }

    // ─── Process callbacks ─────────────────────────────────────────────

    void HandleExited(object sender, EventArgs e)
    {
        int code = 0;
        try { code = _process?.ExitCode ?? 0; } catch { }

        EditorApplication.delayCall += () =>
        {
            _processRunning = false;
            if (code == 0)
            {
                _state       = SetupState.Success;
                _stateDetail = "";
                AddLog("Setup completed successfully.");
            }
            else
            {
                SetFailed($"pip exited with code {code}");
            }
            Repaint();
        };
    }

    void ReadStdout()
    {
        try
        {
            while (!_process.StandardOutput.EndOfStream)
            {
                string line = _process.StandardOutput.ReadLine();
                if (line != null)
                    EditorApplication.delayCall += () => { AddLog(line); Repaint(); };
            }
        }
        catch { }
    }

    void ReadStderr()
    {
        try
        {
            while (!_process.StandardError.EndOfStream)
            {
                string line = _process.StandardError.ReadLine();
                if (line != null)
                    EditorApplication.delayCall += () => { AddLog(line, isError: true); Repaint(); };
            }
        }
        catch { }
    }

    void KillProcess()
    {
        if (_process != null && _processRunning)
        {
            try { _process.Kill(); } catch { }
            _processRunning = false;
            _state = SetupState.Idle;
            _stateDetail = "";
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    void AddLog(string text, bool isError = false)
    {
        _log.Add(new LogEntry { Text = text, IsError = isError, Time = DateTime.Now });
        while (_log.Count > MaxLogLines)
            _log.RemoveAt(0);
    }

    void SetFailed(string reason)
    {
        _state       = SetupState.Failed;
        _stateDetail = reason;
        _processRunning = false;
        AddLog($"ERROR: {reason}", isError: true);
        Repaint();
        Debug.LogError($"[SHILATESetup] {reason}");
    }

    static string GetPipExecutable(string venvPath)
    {
        bool isWindows = Application.platform == RuntimePlatform.WindowsEditor;
        string rel = isWindows ? Path.Combine("Scripts", "pip.exe")
                               : Path.Combine("bin", "pip");
        return Path.Combine(venvPath, rel);
    }

    /// <summary>
    /// Returns true if the tensorboard package directory exists inside the venv.
    /// This is a fast filesystem check — no process launch required.
    /// </summary>
    public static bool IsTensorboardInstalled(string venvPath)
    {
        try
        {
            bool isWindows = Application.platform == RuntimePlatform.WindowsEditor;
            string sitePackages = isWindows
                ? Path.Combine(venvPath, "Lib", "site-packages")
                : Path.Combine(venvPath, "lib");

            if (isWindows)
                return Directory.Exists(Path.Combine(sitePackages, "tensorboard"));

            // On Linux/Mac the actual path is lib/pythonX.Y/site-packages/
            foreach (string pyDir in Directory.GetDirectories(sitePackages))
            {
                string candidate = Path.Combine(pyDir, "site-packages", "tensorboard");
                if (Directory.Exists(candidate)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Tries common Python executable names and returns the first found.</summary>
    static string FindSystemPython()
    {
        string[] candidates = Application.platform == RuntimePlatform.WindowsEditor
            ? new[] { "python.exe", "python3.exe", "py.exe" }
            : new[] { "python3", "python" };

        foreach (string name in candidates)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName               = Application.platform == RuntimePlatform.WindowsEditor ? "where" : "which",
                    Arguments              = name,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow         = true,
                });
                string output = proc?.StandardOutput.ReadLine()?.Trim();
                proc?.WaitForExit();
                if (!string.IsNullOrEmpty(output) && File.Exists(output))
                    return output;
            }
            catch { }
        }
        return null;
    }
}
#endif
