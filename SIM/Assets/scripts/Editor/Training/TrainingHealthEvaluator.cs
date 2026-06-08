#if UNITY_EDITOR
using System;
using System.Collections.Generic;

/// <summary>
/// Aggregates training health signals from multiple sources (Python stdout,
/// Unity MQTT telemetry, process state) and computes a single
/// <see cref="HealthState"/> the Editor window can render prominently.
/// </summary>
public class TrainingHealthEvaluator
{
    public enum HealthState
    {
        Idle,           // no training in progress
        Healthy,        // everything looks fine
        Warning,        // degraded but training continues
        Critical,       // training is broken — needs attention NOW
        Disconnected,   // can't reach the broker
    }

    public struct Issue
    {
        public DateTime Time;
        public HealthState Level;
        public string Message;
    }

    public HealthState CurrentState { get; private set; } = HealthState.Idle;
    public string CurrentMessage { get; private set; } = "Idle";
    public IReadOnlyList<Issue> History => _history;

    readonly List<Issue> _history = new();
    const int MaxHistory = 100;

    // Health thresholds
    public float ObsTimeoutSeconds = 5f;
    public float EpisodeTimeoutSeconds = 60f;
    public float RewardCollapseRatio = 0.5f;
    public int RewardCollapseWindow = 10;

    // Time-tracked signals
    DateTime _lastObsTime;
    DateTime _lastEpisodeTime;
    DateTime _lastHeartbeatTime;
    bool _processRunning;
    bool _mqttConnected;
    bool _pythonAlive;
    DateTime _trainingStarted;

    // Reward tracking for collapse detection
    readonly Queue<float> _episodeRewards = new();

    // Sticky issues
    bool _stickyCrash;
    string _stickyCrashMessage;

    public void StartTraining()
    {
        _trainingStarted = DateTime.Now;
        _lastObsTime = _lastEpisodeTime = _lastHeartbeatTime = DateTime.Now;
        _processRunning = true;
        _pythonAlive = true;
        _stickyCrash = false;
        _stickyCrashMessage = null;
        _episodeRewards.Clear();
        SetState(HealthState.Healthy, "Training started");
    }

    public void StopTraining()
    {
        _processRunning = false;
        _pythonAlive = false;
        SetState(HealthState.Idle, "Idle");
    }

    public void NotifyMqttConnected(bool connected)
    {
        if (_mqttConnected == connected) return;
        _mqttConnected = connected;
        if (!connected && _processRunning)
            SetState(HealthState.Disconnected, "MQTT broker disconnected");
    }

    public void NotifyProcessExited(int exitCode)
    {
        _processRunning = false;
        _pythonAlive = false;
        if (exitCode != 0)
        {
            _stickyCrash = true;
            _stickyCrashMessage = $"Trainer crashed (exit {exitCode})";
            SetState(HealthState.Critical, _stickyCrashMessage);
        }
        else
        {
            SetState(HealthState.Idle, "Training completed");
        }
    }

    public void NotifyObs() => _lastObsTime = DateTime.Now;
    public void NotifyHeartbeat() => _lastHeartbeatTime = DateTime.Now;

    public void NotifyEpisodeEnd(float reward)
    {
        _lastEpisodeTime = DateTime.Now;
        _episodeRewards.Enqueue(reward);
        while (_episodeRewards.Count > RewardCollapseWindow * 2)
            _episodeRewards.Dequeue();
    }

    /// <summary>Consumes a SHILATE-HEALTH marker payload from train.py stdout.</summary>
    public void NotifyHealthMarker(string signal)
    {
        if (string.IsNullOrEmpty(signal)) return;

        if (signal.StartsWith("nan_loss"))
            SetState(HealthState.Critical, "NaN/Inf in loss — model diverged");
        else if (signal.StartsWith("mqtt_connect_failed"))
            SetState(HealthState.Critical, "Python could not connect to MQTT");
        else if (signal.StartsWith("obs_timeout"))
            SetState(HealthState.Warning, $"Observation timeout: {signal}");
        else if (signal.StartsWith("training_crashed"))
        {
            _stickyCrash = true;
            _stickyCrashMessage = $"Training crashed: {signal}";
            SetState(HealthState.Critical, _stickyCrashMessage);
        }
        else if (signal.StartsWith("mqtt_connected"))
            NotifyMqttConnected(true);
        else if (signal.StartsWith("training_started"))
            SetState(HealthState.Healthy, "Training started");
        else if (signal.StartsWith("training_complete"))
            SetState(HealthState.Idle, "Training complete");
        else if (signal.StartsWith("training_interrupted"))
            SetState(HealthState.Idle, "Training interrupted");
    }

    /// <summary>Call once per Editor update to re-evaluate time-based health rules.</summary>
    public void Tick()
    {
        if (_stickyCrash)
        {
            SetState(HealthState.Critical, _stickyCrashMessage ?? "Trainer crashed");
            return;
        }

        if (!_processRunning)
        {
            // Stay in whatever terminal state we have.
            return;
        }

        if (!_mqttConnected)
        {
            SetState(HealthState.Disconnected, "MQTT broker disconnected");
            return;
        }

        var now = DateTime.Now;

        // Only check obs/episode liveness after a small grace period so we don't
        // alarm during initial Unity startup.
        if ((now - _trainingStarted).TotalSeconds < 10)
        {
            SetState(HealthState.Healthy, "Warming up…");
            return;
        }

        if ((now - _lastObsTime).TotalSeconds > ObsTimeoutSeconds)
        {
            SetState(HealthState.Critical,
                $"env stalled — no observations for {(now - _lastObsTime).TotalSeconds:F0}s");
            return;
        }

        if ((now - _lastEpisodeTime).TotalSeconds > EpisodeTimeoutSeconds)
        {
            SetState(HealthState.Warning,
                $"agent stuck — no episode for {(now - _lastEpisodeTime).TotalSeconds:F0}s");
            return;
        }

        if (CheckRewardCollapse(out string collapseReason))
        {
            SetState(HealthState.Warning, collapseReason);
            return;
        }

        SetState(HealthState.Healthy, "Training healthy");
    }

    bool CheckRewardCollapse(out string reason)
    {
        reason = null;
        int w = RewardCollapseWindow;
        if (_episodeRewards.Count < w * 2) return false;

        var arr = _episodeRewards.ToArray();
        int n = arr.Length;

        float Mean(int start, int end)
        {
            float s = 0f;
            for (int i = start; i < end; i++) s += arr[i];
            return s / (end - start);
        }

        float priorMean = Mean(n - w * 2, n - w);
        float recentMean = Mean(n - w, n);

        if (priorMean <= 0f) return false;
        if (recentMean < priorMean * (1f - RewardCollapseRatio))
        {
            reason = $"Reward collapsing: {priorMean:F1} → {recentMean:F1}";
            return true;
        }
        return false;
    }

    void SetState(HealthState newState, string message)
    {
        if (CurrentState == newState && CurrentMessage == message) return;

        CurrentState = newState;
        CurrentMessage = message;

        _history.Add(new Issue
        {
            Time = DateTime.Now,
            Level = newState,
            Message = message,
        });
        while (_history.Count > MaxHistory)
            _history.RemoveAt(0);

        OnStateChanged?.Invoke(newState, message);
    }

    public event Action<HealthState, string> OnStateChanged;

    public void Clear()
    {
        _history.Clear();
        _episodeRewards.Clear();
        CurrentState = HealthState.Idle;
        CurrentMessage = "Idle";
        _stickyCrash = false;
        _stickyCrashMessage = null;
    }
}
#endif
