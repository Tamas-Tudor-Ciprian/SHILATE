#if UNITY_EDITOR
using UnityEngine;

/// <summary>
/// Lightweight in-game HUD shown during Editor Play mode while training is
/// active. Surfaces the locally-visible signals (MQTT status, recent obs,
/// current episode reward, timeout countdown) so problems are obvious without
/// switching to the Training Controller window.
///
/// Wired by <see cref="CarFactory.BuildCar"/> behind <c>#if UNITY_EDITOR</c>;
/// never present in builds.
/// </summary>
public class TrainingHUDOverlay : MonoBehaviour
{
    public LedaBroker broker;
    public VehicleController vehicle;
    public TrainingBridge trainingBridge;

    [Tooltip("Toggle the HUD on/off with this key.")]
    public KeyCode toggleKey = KeyCode.F8;

    [Tooltip("Seconds without a publish before we declare the broker stalled.")]
    public float stallThreshold = 5f;

    bool _visible = true;
    GUIStyle _bannerStyle;
    GUIStyle _labelStyle;
    GUIStyle _criticalStyle;

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            _visible = !_visible;
    }

    void OnGUI()
    {
        if (!_visible) return;

        EnsureStyles();

        const float pad = 8f;
        const float width = 260f;
        float height = trainingBridge != null ? 132f : 96f;
        var rect = new Rect(pad, pad, width, height);

        // Background
        var bgColor = ResolveBannerColor();
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.7f);
        GUI.Box(rect, GUIContent.none);
        GUI.color = bgColor;
        GUI.Box(new Rect(rect.x, rect.y, rect.width, 4f), GUIContent.none);
        GUI.color = prev;

        var inner = new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, rect.height - 16f);
        GUILayout.BeginArea(inner);

        GUILayout.Label("SHILATE Training", _bannerStyle);

        string mqttText = broker != null && broker.isActiveAndEnabled
            ? "MQTT: connected"
            : "MQTT: <missing>";
        GUILayout.Label(mqttText, _labelStyle);

        if (trainingBridge != null)
        {
            GUILayout.Label(
                $"Episode {trainingBridge.CurrentEpisode}  steps {trainingBridge.EpisodeSteps}",
                _labelStyle);
            GUILayout.Label(
                $"Reward: {trainingBridge.CumulativeReward:F2}",
                _labelStyle);

            float t = trainingBridge.EpisodeTime;
            float to = trainingBridge.EpisodeTimeout;
            GUILayout.Label($"Time: {t:F1}s / {to:F0}s", _labelStyle);
        }
        else
        {
            GUILayout.Label("TrainingBridge not wired", _criticalStyle);
        }

        if (vehicle != null)
        {
            GUILayout.Label(
                $"Speed: {vehicle.CurrentSpeed:F1} km/h   Gear: {vehicle.CurrentGear}",
                _labelStyle);
        }

        GUILayout.EndArea();
    }

    Color ResolveBannerColor()
    {
        if (broker == null || !broker.isActiveAndEnabled)
            return new Color(0.95f, 0.25f, 0.25f);
        if (trainingBridge == null)
            return new Color(0.95f, 0.6f, 0.1f);
        return new Color(0.2f, 0.8f, 0.3f);
    }

    void EnsureStyles()
    {
        if (_bannerStyle != null) return;
        _bannerStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 13,
            normal = { textColor = Color.white },
        };
        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal = { textColor = new Color(0.92f, 0.92f, 0.92f) },
        };
        _criticalStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 11,
            normal = { textColor = new Color(1f, 0.5f, 0.5f) },
        };
    }
}
#endif
