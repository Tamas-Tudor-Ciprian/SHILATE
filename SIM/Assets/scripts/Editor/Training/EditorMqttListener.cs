#if UNITY_EDITOR
using System;
using UnityEngine;

/// <summary>
/// Thin Editor-side wrapper around <see cref="MqttClient"/> that subscribes
/// to the Unity-published training topics (<c>env0/training/episode_end</c>
/// and <c>env0/training/heartbeat</c>) and emits parsed events for the
/// <see cref="TrainingEditorWindow"/>.
///
/// Runs in-process — independent of the LedaBroker MQTT client living inside
/// Unity Play mode (different client-id, separate session).
/// </summary>
public class EditorMqttListener
{
    public event Action<int, float, int, string> OnEpisodeEnd; // episode, reward, steps, reason
    public event Action<int, int, float> OnHeartbeat;          // episode, steps, reward
    public event Action<bool> OnConnectionChanged;

    MqttClient _mqtt;
    bool _connected;
    string _prefix = "env0";

    public bool IsConnected => _connected;

    public void Start(string host, int port, string envPrefix = "env0")
    {
        _prefix = envPrefix;
        Stop();

        _mqtt = new MqttClient(host, port, $"shilate-editor-listener-{Guid.NewGuid():N}");
        _mqtt.OnConnected += HandleConnected;
        _mqtt.OnDisconnected += HandleDisconnected;
        _mqtt.OnMessageReceived += HandleMessage;
        _mqtt.Connect();
    }

    public void Stop()
    {
        if (_mqtt == null) return;
        try
        {
            _mqtt.OnConnected -= HandleConnected;
            _mqtt.OnDisconnected -= HandleDisconnected;
            _mqtt.OnMessageReceived -= HandleMessage;
            _mqtt.Disconnect();
        }
        catch { /* tolerate */ }
        _mqtt = null;
        if (_connected)
        {
            _connected = false;
            OnConnectionChanged?.Invoke(false);
        }
    }

    /// <summary>Must be called from the Editor update loop to drain queued messages.</summary>
    public void Pump()
    {
        _mqtt?.ProcessMessages();
    }

    void HandleConnected()
    {
        _connected = true;
        OnConnectionChanged?.Invoke(true);
        _mqtt.Subscribe($"{_prefix}/vehicle/training/episode_end");
        _mqtt.Subscribe($"{_prefix}/vehicle/training/heartbeat");
    }

    void HandleDisconnected(string reason)
    {
        if (!_connected) return;
        _connected = false;
        OnConnectionChanged?.Invoke(false);
    }

    void HandleMessage(string topic, string payload)
    {
        if (topic.EndsWith("/episode_end"))
        {
            int episode = ExtractInt(payload, "episode");
            float reward = ExtractFloat(payload, "reward");
            int steps = ExtractInt(payload, "steps");
            string reason = ExtractString(payload, "reason");
            OnEpisodeEnd?.Invoke(episode, reward, steps, reason);
        }
        else if (topic.EndsWith("/heartbeat"))
        {
            int episode = ExtractInt(payload, "value");
            int steps = ExtractInt(payload, "steps");
            float reward = ExtractFloat(payload, "reward");
            OnHeartbeat?.Invoke(episode, steps, reward);
        }
    }

    // ─── Tiny string-based JSON extractors (avoids pulling in a parser) ───

    static int ExtractInt(string json, string key)
    {
        string s = ExtractRaw(json, key);
        return int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;
    }

    static float ExtractFloat(string json, string key)
    {
        string s = ExtractRaw(json, key);
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }

    static string ExtractString(string json, string key)
    {
        string s = ExtractRaw(json, key);
        return s.Trim('"');
    }

    static string ExtractRaw(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return "";
        int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (k < 0) return "";
        int colon = json.IndexOf(':', k);
        if (colon < 0) return "";
        int start = colon + 1;
        while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
        int end = start;
        while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != '\n') end++;
        return json.Substring(start, end - start).Trim();
    }
}
#endif
