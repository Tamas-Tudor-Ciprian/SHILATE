#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Manages Python training process lifecycle with cross-platform venv activation.
/// Handles Windows (cmd.exe) and Linux/macOS (bash).
/// </summary>
public class PythonProcessManager
{
    Process _process;
    Thread _stdoutThread;
    Thread _stderrThread;
    volatile bool _isRunning;

    public bool IsRunning => _isRunning && _process != null && !_process.HasExited;

    public event Action<string> OnOutputLine;
    public event Action<string> OnErrorLine;
    public event Action<int> OnExited;

    /// <summary>
    /// Starts the Python training process with the given settings.
    /// </summary>
    public bool Start(TrainingSettings settings, bool debugMode = false)
    {
        return StartScript(
            settings.GetAbsoluteVenvPath(),
            settings.GetAbsoluteTrainScriptPath(),
            settings.GetWorkingDirectory(),
            settings.BuildCommandLineArgs(debugMode)
        );
    }

    /// <summary>
    /// Starts a Python script with explicit paths and arguments.
    /// </summary>
    public bool StartScript(string venvPath, string scriptPath, string workingDir, string args)
    {
        if (IsRunning)
        {
            Debug.LogWarning("[PythonProcessManager] Process already running");
            return false;
        }

        if (!Directory.Exists(venvPath))
        {
            Debug.LogError($"[PythonProcessManager] Venv not found: {venvPath}");
            return false;
        }

        if (!File.Exists(scriptPath))
        {
            Debug.LogError($"[PythonProcessManager] Script not found: {scriptPath}");
            return false;
        }

        string shellCommand = BuildShellCommand(venvPath, scriptPath, args);
        Debug.Log($"[PythonProcessManager] Starting: {shellCommand}");

        try
        {
            _process = new Process();
            _process.StartInfo = CreateStartInfo(shellCommand, workingDir);
            _process.EnableRaisingEvents = true;
            _process.Exited += HandleProcessExited;

            _process.Start();
            _isRunning = true;

            _stdoutThread = new Thread(ReadStdout) { IsBackground = true };
            _stderrThread = new Thread(ReadStderr) { IsBackground = true };
            _stdoutThread.Start();
            _stderrThread.Start();

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PythonProcessManager] Failed to start: {ex.Message}");
            _isRunning = false;
            return false;
        }
    }

    /// <summary>
    /// Stops the Python process gracefully (sends interrupt signal).
    /// </summary>
    public void Stop()
    {
        if (!IsRunning) return;

        Debug.Log("[PythonProcessManager] Stopping training...");

        try
        {
            if (IsWindows())
            {
                SendCtrlCWindows(_process);
            }
            else
            {
                SendSigintUnix(_process);
            }

            if (!_process.WaitForExit(5000))
            {
                Debug.LogWarning("[PythonProcessManager] Process did not exit gracefully, killing...");
                _process.Kill();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[PythonProcessManager] Error stopping: {ex.Message}");
            try { _process?.Kill(); } catch { }
        }
        finally
        {
            Cleanup();
        }
    }

    void HandleProcessExited(object sender, EventArgs e)
    {
        int exitCode = 0;
        try { exitCode = _process?.ExitCode ?? 0; } catch { }

        EditorApplication.delayCall += () =>
        {
            OnExited?.Invoke(exitCode);
        };

        Cleanup();
    }

    void Cleanup()
    {
        _isRunning = false;
        _stdoutThread = null;
        _stderrThread = null;
    }

    void ReadStdout()
    {
        try
        {
            while (_isRunning && _process != null && !_process.StandardOutput.EndOfStream)
            {
                string line = _process.StandardOutput.ReadLine();
                if (line != null)
                {
                    EditorApplication.delayCall += () => OnOutputLine?.Invoke(line);
                }
            }
        }
        catch (Exception) { }
    }

    void ReadStderr()
    {
        try
        {
            while (_isRunning && _process != null && !_process.StandardError.EndOfStream)
            {
                string line = _process.StandardError.ReadLine();
                if (line != null)
                {
                    EditorApplication.delayCall += () => OnErrorLine?.Invoke(line);
                }
            }
        }
        catch (Exception) { }
    }

    string BuildShellCommand(string venvPath, string scriptPath, string args)
    {
        string scriptName = Path.GetFileName(scriptPath);

        if (IsWindows())
        {
            string activatePath = Path.Combine(venvPath, "Scripts", "activate.bat");
            // -u flag forces unbuffered stdout/stderr for real-time output
            return $"\"{activatePath}\" && python -u \"{scriptName}\" {args}";
        }
        else
        {
            string activatePath = Path.Combine(venvPath, "bin", "activate");
            return $"source \"{activatePath}\" && python -u \"{scriptName}\" {args}";
        }
    }

    ProcessStartInfo CreateStartInfo(string command, string workingDir)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        if (IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/c \"{command}\"";
        }
        else
        {
            startInfo.FileName = "/bin/bash";
            startInfo.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
        }

        return startInfo;
    }

    static bool IsWindows()
    {
        return Application.platform == RuntimePlatform.WindowsEditor;
    }

    #region Windows Ctrl+C

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(IntPtr HandlerRoutine, bool Add);

    const uint CTRL_C_EVENT = 0;

    static void SendCtrlCWindows(Process process)
    {
        FreeConsole();

        if (AttachConsole((uint)process.Id))
        {
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
            Thread.Sleep(500);
            FreeConsole();
            SetConsoleCtrlHandler(IntPtr.Zero, false);
        }
        else
        {
            process.Kill();
        }
    }

    #endregion

    #region Unix SIGINT

    [DllImport("libc", EntryPoint = "kill")]
    static extern int UnixKill(int pid, int sig);

    const int SIGINT = 2;

    static void SendSigintUnix(Process process)
    {
        try
        {
            UnixKill(process.Id, SIGINT);
        }
        catch
        {
            process.Kill();
        }
    }

    #endregion
}
#endif
