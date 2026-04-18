using UnityEngine;

/// <summary>
/// Controls Time.timeScale from Leda via MQTT or command-line args.
/// Subscribes to LedaBroker.OnTimeScaleRequested events.
/// Clamps to a safe range for WheelCollider stability.
/// </summary>
public class TimeScaleController : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Minimum allowed timeScale")]
    [SerializeField] float minScale = 1f;

    [Tooltip("Maximum allowed timeScale (WheelColliders unstable above ~8x)")]
    [SerializeField] float maxScale = 10f;

    [Tooltip("The LedaBroker to listen for timescale commands")]
    public LedaBroker broker;

    float _baseFixedDeltaTime;

    void Awake()
    {
        _baseFixedDeltaTime = Time.fixedDeltaTime;

        // Check command-line args for --timescale
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--timescale")
            {
                if (float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float scale))
                {
                    ApplyTimeScale(scale);
                }
                break;
            }
        }
    }

    void OnEnable()
    {
        if (broker != null)
            broker.OnTimeScaleRequested += ApplyTimeScale;
    }

    void OnDisable()
    {
        if (broker != null)
            broker.OnTimeScaleRequested -= ApplyTimeScale;

        ResetTimeScale();
    }

    void OnApplicationQuit()
    {
        ResetTimeScale();
    }

    void ApplyTimeScale(float scale)
    {
        float clamped = Mathf.Clamp(scale, minScale, maxScale);
        Time.timeScale = clamped;
        Time.fixedDeltaTime = _baseFixedDeltaTime * clamped;
        Debug.Log($"[TimeScaleController] TimeScale set to {clamped:F1}x (requested {scale:F1}x)");
    }

    void ResetTimeScale()
    {
        Time.timeScale = 1f;
        Time.fixedDeltaTime = _baseFixedDeltaTime;
    }
}
