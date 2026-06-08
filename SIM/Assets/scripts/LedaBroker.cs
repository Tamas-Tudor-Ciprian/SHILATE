using UnityEngine;

/// <summary>
/// Bridge between the Unity simulation and an Eclipse Leda instance via MQTT.
/// Publishes vehicle signals that flow through Mosquitto → Kuksa Feeder → Kuksa Databroker.
/// Receives control commands from Leda on {envPrefix}/leda/control/* topics.
/// </summary>
public class LedaBroker : MonoBehaviour
{
    [Header("MQTT Broker (WSL / Leda)")]
    [SerializeField] string brokerHost = "localhost";
    [SerializeField] int brokerPort = 1883;
    [SerializeField] string clientId = "unity-shilate";

    [Header("Environment Prefix (for parallel training)")]
    [Tooltip("Prefix for all MQTT topics, e.g. 'env0'. Set per instance for parallel envs.")]
    [SerializeField] string envPrefix = "env0";

    [Header("Publish Settings")]
    [Tooltip("Seconds between signal publishes")]
    [SerializeField] float publishInterval = 0.1f;

    [Header("Remote Control")]
    [Tooltip("Remote input receiver for Leda control commands")]
    public RemoteDriveInput remoteInput;

    [Header("Vehicle Signals (set from other scripts or Inspector)")]
    public float Speed;
    public float SignedSpeed;
    public float RPM;
    public float SteeringAngle;
    public float BrakePedal;
    public float ThrottlePosition;
    public VehicleController.GearState Gear;

    MqttClient _mqtt;
    float _publishTimer;
    bool _connected;
    float _reconnectTimer;
    const float ReconnectInterval = 3f;

    /// <summary>Fired when a reset command is received from Leda.</summary>
    public event System.Action OnResetRequested;

    void OnEnable()
    {
        _mqtt = new MqttClient(brokerHost, brokerPort, $"unity-shilate-{envPrefix}");
        _mqtt.OnConnected += HandleConnected;
        _mqtt.OnDisconnected += HandleDisconnected;
        _mqtt.OnMessageReceived += HandleMessage;
        _mqtt.Connect();

        Debug.Log($"[LedaBroker] Connecting to MQTT broker at {brokerHost}:{brokerPort}...");
    }

    void OnDisable()
    {
        if (_mqtt != null)
        {
            _mqtt.Disconnect();
            _mqtt.OnConnected -= HandleConnected;
            _mqtt.OnDisconnected -= HandleDisconnected;
            _mqtt.OnMessageReceived -= HandleMessage;
            _mqtt = null;
        }
        _connected = false;
    }

    void Update()
    {
        _mqtt?.ProcessMessages();

        if (!_connected)
        {
            _reconnectTimer += Time.unscaledDeltaTime;
            if (_reconnectTimer >= ReconnectInterval)
            {
                _reconnectTimer = 0f;
                Debug.Log($"[LedaBroker] Attempting reconnect to {brokerHost}:{brokerPort}...");
                _mqtt?.Dispose();
                _mqtt = new MqttClient(brokerHost, brokerPort, $"unity-shilate-{envPrefix}");
                _mqtt.OnConnected += HandleConnected;
                _mqtt.OnDisconnected += HandleDisconnected;
                _mqtt.OnMessageReceived += HandleMessage;
                _mqtt.Connect();
            }
            return;
        }
        _reconnectTimer = 0f;

        _publishTimer += Time.unscaledDeltaTime;
        if (_publishTimer >= publishInterval)
        {
            _publishTimer = 0f;
            PublishSignals();
        }
    }

    void PublishSignals()
    {
        string p = envPrefix + "/";
        Publish(p + "vehicle/speed", Speed);
        Publish(p + "vehicle/signedSpeed", SignedSpeed);
        Publish(p + "vehicle/rpm", RPM);
        Publish(p + "vehicle/steering", SteeringAngle);
        Publish(p + "vehicle/brake", BrakePedal);
        Publish(p + "vehicle/throttle", ThrottlePosition);
        PublishString(p + "vehicle/gear", GearToString(Gear));
    }

    void Publish(string topic, float value)
    {
        string json = "{\"value\":" + value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}";
        _mqtt.Publish(topic, json);
    }

    void PublishString(string topic, string value)
    {
        string json = "{\"value\":\"" + value + "\"}";
        _mqtt.Publish(topic, json);
    }

    static string GearToString(VehicleController.GearState gear)
    {
        switch (gear)
        {
            case VehicleController.GearState.Park:    return "P";
            case VehicleController.GearState.Reverse:  return "R";
            case VehicleController.GearState.Neutral:  return "N";
            case VehicleController.GearState.Drive:    return "D";
            default: return "P";
        }
    }

    // ─── Public API for other scripts ───

    /// <summary>Set speed and immediately publish.</summary>
    public void SetSpeed(float value)
    {
        Speed = value;
        if (_connected) Publish(envPrefix + "/vehicle/speed", value);
    }

    /// <summary>Set RPM and immediately publish.</summary>
    public void SetRPM(float value)
    {
        RPM = value;
        if (_connected) Publish(envPrefix + "/vehicle/rpm", value);
    }

    /// <summary>Publish an arbitrary signal (auto-prefixed with envPrefix).</summary>
    public void PublishSignal(string topic, float value)
    {
        if (_connected) Publish(envPrefix + "/" + topic, value);
    }

    /// <summary>Publish a raw JSON string to an arbitrary topic (auto-prefixed).</summary>
    public void PublishRaw(string topic, string json)
    {
        if (_connected) _mqtt.Publish(envPrefix + "/" + topic, json);
    }

    /// <summary>The environment prefix for this instance (e.g. "env0").</summary>
    public string EnvPrefix => envPrefix;

    /// <summary>Configure broker settings at runtime (used by the Editor training window).</summary>
    public void Configure(string host, int port, string prefix)
    {
        brokerHost = host;
        brokerPort = port;
        envPrefix = prefix;

        // Disconnect and reconnect with new settings.
        // OnEnable() already connected with inspector defaults; we must reconnect
        // to the actual broker host and adopt the correct per-instance client ID.
        if (_mqtt != null)
        {
            _mqtt.OnConnected -= HandleConnected;
            _mqtt.OnDisconnected -= HandleDisconnected;
            _mqtt.OnMessageReceived -= HandleMessage;
            _mqtt.Disconnect();
        }
        _connected = false;

        _mqtt = new MqttClient(brokerHost, brokerPort, $"unity-shilate-{prefix}");
        _mqtt.OnConnected += HandleConnected;
        _mqtt.OnDisconnected += HandleDisconnected;
        _mqtt.OnMessageReceived += HandleMessage;
        _mqtt.Connect();

        Debug.Log($"[LedaBroker] Reconnecting to {brokerHost}:{brokerPort} (prefix: {envPrefix})");
    }

    // ─── MQTT callbacks (dispatched on main thread) ───

    void HandleConnected()
    {
        _connected = true;
        Debug.Log($"[LedaBroker] Connected to MQTT broker at {brokerHost}:{brokerPort} (prefix: {envPrefix})");

        _mqtt.Subscribe(envPrefix + "/leda/control/#");
        _mqtt.Subscribe(envPrefix + "/leda/command/#");
    }

    void HandleDisconnected(string reason)
    {
        _connected = false;
        Debug.LogWarning($"[LedaBroker] Disconnected: {reason}");
    }

    void HandleMessage(string topic, string payload)
    {
        // Strip the env prefix to get the logical topic
        string prefix = envPrefix + "/";
        string localTopic = topic.StartsWith(prefix) ? topic.Substring(prefix.Length) : topic;

        // Parse control commands from Leda
        if (localTopic.StartsWith("leda/control/"))
        {
            string command = localTopic.Substring("leda/control/".Length);
            float value = ParseFloat(payload);

            switch (command)
            {
                case "throttle":
                    if (remoteInput != null) remoteInput.Throttle = Mathf.Clamp01(value);
                    break;
                case "steer":
                    if (remoteInput != null) remoteInput.Steer = Mathf.Clamp(value, -1f, 1f);
                    break;
                case "brake":
                    if (remoteInput != null) remoteInput.Brake = Mathf.Clamp01(value);
                    break;
                case "gear":
                    if (remoteInput != null)
                    {
                        remoteInput.RequestedGear = ParseGear(payload);
                        remoteInput.GearChangeRequested = true;
                    }
                    break;
                case "reset":
                    OnResetRequested?.Invoke();
                    break;
                case "timescale":
                    // Timescale control removed: training always runs at real time.
                    // Accept and ignore so legacy clients don't error.
                    break;
                default:
                    Debug.Log($"[LedaBroker] Unknown control command: {command}");
                    break;
            }
        }
        else
        {
            Debug.Log($"[LedaBroker] Received {topic}: {payload}");
        }
    }

    static float ParseFloat(string json)
    {
        // Parse {"value": 0.5} — simple extraction without allocating a JSON parser
        int idx = json.IndexOf(':');
        if (idx < 0) return 0f;
        string raw = json.Substring(idx + 1).TrimEnd('}', ' ', '\n', '\r');
        // Remove quotes if present (for string values like gear)
        raw = raw.Trim('"', ' ');
        float.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float result);
        return result;
    }

    static VehicleController.GearState ParseGear(string json)
    {
        string upper = json.ToUpperInvariant();
        if (upper.Contains("\"R\"")) return VehicleController.GearState.Reverse;
        if (upper.Contains("\"N\"")) return VehicleController.GearState.Neutral;
        if (upper.Contains("\"D\"")) return VehicleController.GearState.Drive;
        return VehicleController.GearState.Park;
    }
}
