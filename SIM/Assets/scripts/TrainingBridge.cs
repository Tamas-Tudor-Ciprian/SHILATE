using UnityEngine;

/// <summary>
/// Publishes training-specific signals (observation, reward, done) for RL training.
/// Computes reward based on forward progress, collision penalty, and course completion.
/// </summary>
public class TrainingBridge : MonoBehaviour
{
    [Header("References")]
    public LedaBroker broker;
    public VehicleController vehicle;
    public RaycastSensor raycastSensor;
    public ObstacleCourse obstacleCourse;

    [Header("Reward Settings")]
    [Tooltip("Reward per degree of forward progress around the track")]
    [SerializeField] float progressReward = 0.2f;

    [Tooltip("Penalty on collision")]
    [SerializeField] float collisionPenalty = -300f;

    [Tooltip("Bonus for completing a full lap")]
    [SerializeField] float finishBonus = 60f;

    [Header("Episode Settings")]
    [Tooltip("Max episode duration in sim-time seconds")]
    [SerializeField] float episodeTimeout = 60f;


    [Tooltip("One-time bonus awarded when vehicle heading first reaches 180 degrees (±10°)")]
    [SerializeField] float halfTurnBonus = 25f;


    [Tooltip("Max speed for observation normalization (km/h)")]
    [SerializeField] float maxSpeed = 150f;

    float _episodeTimer;
    float _lastAngle;
    float _totalAngleProgress;
    float _cumulativeReward;
    int _episodeSteps;
    int _episodeIndex;
    float _heartbeatTimer;
    bool _episodeDone;
    float _lastRealTime;
    bool _collidedThisStep;   // owned here to avoid FixedUpdate ordering race with RaycastSensor
    bool _halfTurnAwarded;

    const float HeartbeatInterval = 1f;

    void OnCollisionEnter(Collision collision)
    {
        _collidedThisStep = true;
    }

    void OnEnable()
    {
        ResetEpisodeState();
        // Also subscribe to reset events directly as a safety net
        if (broker != null)
            broker.OnResetRequested += ResetEpisodeState;
    }

    void OnDisable()
    {
        if (broker != null)
            broker.OnResetRequested -= ResetEpisodeState;
    }

    void FixedUpdate()
    {
        if (broker == null || vehicle == null || raycastSensor == null) return;
        //if (_episodeDone) return;

        _episodeTimer += Time.fixedDeltaTime;
        _episodeSteps += 1;

        // Heartbeat — published in unscaled real time so the Editor health check
        // can detect "env stalled" even if Time.timeScale somehow becomes 0.
        _heartbeatTimer += Time.unscaledDeltaTime;
        if (_heartbeatTimer >= HeartbeatInterval)
        {
            _heartbeatTimer = 0f;
            broker.PublishRaw("vehicle/training/heartbeat",
                "{\"value\":" + _episodeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"steps\":" + _episodeSteps.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"reward\":" + _cumulativeReward.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}");
        }

        // Compute angular progress around the circular track
        float currentAngle = obstacleCourse != null
            ? obstacleCourse.GetAngle(vehicle.transform.position)
            : 0f;
        float angleDelta = Mathf.DeltaAngle(_lastAngle, currentAngle);
        _lastAngle = currentAngle;
        _totalAngleProgress += angleDelta;

        float stepReward = angleDelta * progressReward;

        // Collision check — use flag set by OnCollisionEnter (not RaycastSensor's cleared flag)
        bool collided = _collidedThisStep;
        _collidedThisStep = false;
        if (collided)
        {
            stepReward = collisionPenalty;
        }

        // Lap completion check

        // 180-degree intermediary reward (one-time per episode)
        if (!_halfTurnAwarded)
        {
            if (_totalAngleProgress >= 180f)
            {
                stepReward += halfTurnBonus;
                _halfTurnAwarded = true;
                Debug.Log($"[TrainingBridge] 180° bonus awarded (totalAngleProgress={_totalAngleProgress:F1}°)");
            }
        }

        // Finish line check

        bool finished = false;
        if (obstacleCourse != null && _totalAngleProgress >= 360f)
        {
            stepReward += finishBonus;
            finished = true;
        }

        _cumulativeReward += stepReward;

        // Episode done conditions
        bool timeout = _episodeTimer >= episodeTimeout;
        _episodeDone = collided || finished || timeout;

        // Tell ObstacleCourse whether to regenerate the layout on the next reset.
        // Obstacles only change when the agent successfully completes a full lap.
        if (_episodeDone && obstacleCourse != null)
            obstacleCourse.ShouldRespawnObstacles = finished;

        // Publish observation
        PublishObservation();

        // Publish reward
        broker.PublishRaw("vehicle/training/reward",
            "{\"value\":" + stepReward.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "}");

        // Publish done
        if (_episodeDone)
        {
            string reason = collided ? "collision" : finished ? "finished" : timeout ? "timeout" : "halfturn";
            broker.PublishRaw("vehicle/training/done",
                "{\"value\":1,\"reason\":\"" + reason + "\",\"cumulative_reward\":" +
                _cumulativeReward.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}");

            // Rich episode_end payload consumed by the Editor health evaluator
            broker.PublishRaw("vehicle/training/episode_end",
                "{\"episode\":" + _episodeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"reward\":" + _cumulativeReward.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"steps\":" + _episodeSteps.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"duration\":" + _episodeTimer.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"reason\":\"" + reason + "\"}");

            Debug.Log($"[TrainingBridge] Episode {_episodeIndex} done: {reason}, reward={_cumulativeReward:F2}, time={_episodeTimer:F1}s");
            _episodeIndex += 1;

            // Reset immediately — car always resets, obstacles only regenerate on lap completion
            if (obstacleCourse != null)
                obstacleCourse.Reset();
        }
        else
        {
            broker.PublishRaw("vehicle/training/done", "{\"value\":0}");
        }
    }

    void PublishObservation()
    {
        float[] rays = raycastSensor.GetDistances();
        float normSpeed = Mathf.Clamp01(vehicle.CurrentSpeed / maxSpeed);
        float normSteer = (vehicle.SteerInput + 1f) * 0.5f; // map -1..1 to 0..1

        var sb = new System.Text.StringBuilder(256);
        sb.Append("{\"rays\":[");
        for (int i = 0; i < rays.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(rays[i].ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append("],\"speed\":");
        sb.Append(normSpeed.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(",\"steer\":");
        sb.Append(normSteer.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append('}');

        broker.PublishRaw("vehicle/training/obs", sb.ToString());
    }

    /// <summary>Called when ObstacleCourse.Reset() runs to prepare for a new episode.</summary>
    public void ResetEpisodeState()
    {
        _episodeTimer = 0f;
        _cumulativeReward = 0f;
        _totalAngleProgress = 0f;
        _episodeSteps = 0;
        _episodeDone = false;
        _lastRealTime = Time.unscaledTime;
        _halfTurnAwarded = false;

        if (vehicle != null && obstacleCourse != null)
            _lastAngle = obstacleCourse.GetAngle(vehicle.transform.position);
    }

    // ─── Editor HUD accessors (read-only snapshot) ───
    public int CurrentEpisode => _episodeIndex;
    public int EpisodeSteps => _episodeSteps;
    public float EpisodeTime => _episodeTimer;
    public float EpisodeTimeout => episodeTimeout;
    public float CumulativeReward => _cumulativeReward;
}

