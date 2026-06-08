#if UNITY_EDITOR
using System;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
#endif

/// <summary>
/// Prevents the host OS from sleeping while RL training is active.
/// Refcounted: callers must pair each Acquire() with a Release().
///
/// Backends:
///   * Windows: SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED).
///              Display is intentionally allowed to sleep (no ES_DISPLAY_REQUIRED).
///   * Linux:   spawns `systemd-inhibit --what=sleep --mode=block sleep infinity`
///              as a child process and kills it on Release(). Falls back to a
///              warning (no-op) if systemd-inhibit is not on PATH.
///   * macOS / other: no-op with a one-time info log.
///
/// Editor-only on purpose: never compiled into a player build.
/// </summary>
public static class SleepPreventer
{
    static int _refCount;
    static bool _domainReloadHookInstalled;

#if UNITY_EDITOR_LINUX
    static Process _inhibitProcess;
#endif

    /// <summary>
    /// Request that the OS not enter sleep. Safe to call multiple times; each
    /// call must be paired with a Release().
    /// </summary>
    public static void Acquire(string reason)
    {
        EnsureDomainReloadHook();

        _refCount++;
        if (_refCount > 1)
            return; // Already inhibiting.

#if UNITY_EDITOR_WIN
        var result = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
        if (result == 0)
        {
            Debug.LogWarning("[SleepPreventer] SetThreadExecutionState returned 0; sleep prevention may not be active.");
        }
        else
        {
            Debug.Log($"[SleepPreventer] System sleep blocked while training runs ({reason}).");
        }
#elif UNITY_EDITOR_LINUX
        TryStartSystemdInhibit(reason);
#else
        Debug.Log($"[SleepPreventer] No sleep-prevention backend on this platform ({reason}). Training will run but OS sleep is not blocked.");
#endif
    }

    /// <summary>
    /// Release one Acquire() reference. When the refcount hits zero the OS
    /// sleep policy is restored.
    /// </summary>
    public static void Release()
    {
        if (_refCount == 0)
            return;

        _refCount--;
        if (_refCount > 0)
            return; // Other holders still active.

        ReleaseInternal();
    }

    /// <summary>
    /// Force-release all references. Used by domain-reload / window-close
    /// safety hooks so inhibitors never leak.
    /// </summary>
    public static void ForceReleaseAll()
    {
        if (_refCount == 0)
            return;

        _refCount = 0;
        ReleaseInternal();
    }

    static void ReleaseInternal()
    {
#if UNITY_EDITOR_WIN
        SetThreadExecutionState(ES_CONTINUOUS);
        Debug.Log("[SleepPreventer] System sleep policy restored.");
#elif UNITY_EDITOR_LINUX
        StopSystemdInhibit();
#endif
    }

    static void EnsureDomainReloadHook()
    {
        if (_domainReloadHookInstalled)
            return;
        _domainReloadHookInstalled = true;
        AssemblyReloadEvents.beforeAssemblyReload += ForceReleaseAll;
        EditorApplication.quitting += ForceReleaseAll;
    }

#if UNITY_EDITOR_WIN
    [Flags]
    enum ExecutionState : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
    }

    const uint ES_CONTINUOUS = (uint)ExecutionState.ES_CONTINUOUS;
    const uint ES_SYSTEM_REQUIRED = (uint)ExecutionState.ES_SYSTEM_REQUIRED;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint SetThreadExecutionState(uint esFlags);
#endif

#if UNITY_EDITOR_LINUX
    static void TryStartSystemdInhibit(string reason)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemd-inhibit",
                Arguments = $"--what=sleep --who=\"Unity\" --why=\"SHILATE training ({reason})\" --mode=block sleep infinity",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _inhibitProcess = Process.Start(psi);
            if (_inhibitProcess != null && !_inhibitProcess.HasExited)
            {
                Debug.Log($"[SleepPreventer] systemd-inhibit started (pid={_inhibitProcess.Id}, reason={reason}).");
            }
            else
            {
                Debug.LogWarning("[SleepPreventer] systemd-inhibit exited immediately; sleep prevention not active.");
                _inhibitProcess = null;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SleepPreventer] Could not start systemd-inhibit ({e.GetType().Name}: {e.Message}). " +
                             "Training will run but OS sleep is not blocked.");
            _inhibitProcess = null;
        }
    }

    static void StopSystemdInhibit()
    {
        if (_inhibitProcess == null)
            return;
        try
        {
            if (!_inhibitProcess.HasExited)
                _inhibitProcess.Kill();
            _inhibitProcess.Dispose();
            Debug.Log("[SleepPreventer] systemd-inhibit stopped.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SleepPreventer] Error stopping systemd-inhibit: {e.Message}");
        }
        finally
        {
            _inhibitProcess = null;
        }
    }
#endif
}
#endif
