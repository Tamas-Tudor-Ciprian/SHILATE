using UnityEngine;

/// <summary>
/// Bridge between the Unity simulation and an Eclipse Leda instance via MQTT.
/// Publishes vehicle signals that flow through Mosquitto → Kuksa Feeder → Kuksa Databroker.
/// </summary>
public class LedaBroker : MonoBehaviour
{
    [Header("MQTT Broker (WSL / Leda)")]
    [SerializeField] string brokerHost = "localhost";
    [SerializeField] int brokerPort = 1883;
    [SerializeField] string clientId = "unity-shilate";

    [Header("Publish Settings")]
    [Tooltip("Seconds between signal publishes")]
    [SerializeField] float publishInterval = 0.1f;

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

    void OnEnable()
    {
        _mqtt = new MqttClient(brokerHost, brokerPort, clientId);
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

        if (!_connected) return;

        _publishTimer += Time.deltaTime;
        if (_publishTimer >= publishInterval)
        {
            _publishTimer = 0f;
            PublishSignals();
        }
    }

    void PublishSignals()
    {
        Publish("vehicle/speed", Speed);
        Publish("vehicle/signedSpeed", SignedSpeed);
        Publish("vehicle/rpm", RPM);
        Publish("vehicle/steering", SteeringAngle);
        Publish("vehicle/brake", BrakePedal);
        Publish("vehicle/throttle", ThrottlePosition);
        PublishString("vehicle/gear", GearToString(Gear));
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
        if (_connected) Publish("vehicle/speed", value);
    }

    /// <summary>Set RPM and immediately publish.</summary>
    public void SetRPM(float value)
    {
        RPM = value;
        if (_connected) Publish("vehicle/rpm", value);
    }

    /// <summary>Publish an arbitrary signal.</summary>
    public void PublishSignal(string topic, float value)
    {
        if (_connected) Publish(topic, value);
    }

    // ─── MQTT callbacks (dispatched on main thread) ───

    void HandleConnected()
    {
        _connected = true;
        Debug.Log($"[LedaBroker] Connected to MQTT broker at {brokerHost}:{brokerPort}");

        // Subscribe for future bidirectional commands from Leda
        _mqtt.Subscribe("leda/command/#");
    }

    void HandleDisconnected(string reason)
    {
        _connected = false;
        Debug.LogWarning($"[LedaBroker] Disconnected: {reason}");
    }

    void HandleMessage(string topic, string payload)
    {
        Debug.Log($"[LedaBroker] Received {topic}: {payload}");
        // Future: parse actuator commands from Leda here
    }
}
