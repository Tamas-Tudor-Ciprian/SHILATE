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
    [SerializeField] float progressReward = 2f;

    [Tooltip("Penalty on collision")]
    [SerializeField] float collisionPenalty = -5f;

    [Tooltip("Bonus for completing a full lap")]
    [SerializeField] float finishBonus = 50f;

    [Header("Episode Settings")]
    [Tooltip("Max episode duration in sim-time seconds")]
    [SerializeField] float episodeTimeout = 60f;

    [Tooltip("Max speed for observation normalization (km/h)")]
    [SerializeField] float maxSpeed = 150f;

    float _episodeTimer;
    float _lastAngle;
    float _totalAngleProgress;
    float _cumulativeReward;
    bool _episodeDone;
    float _lastRealTime;
    bool _collidedThisStep;   // owned here to avoid FixedUpdate ordering race with RaycastSensor

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
            stepReward += collisionPenalty;
        }

        // Lap completion check
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

        // Publish observation
        PublishObservation();

        // Publish reward
        broker.PublishRaw("vehicle/training/reward",
            "{\"value\":" + stepReward.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "}");

        // Publish done
        if (_episodeDone)
        {
            string reason = collided ? "collision" : finished ? "finished" : "timeout";
            broker.PublishRaw("vehicle/training/done",
                "{\"value\":1,\"reason\":\"" + reason + "\",\"cumulative_reward\":" +
                _cumulativeReward.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}");
            Debug.Log($"[TrainingBridge] Episode done: {reason}, reward={_cumulativeReward:F2}, time={_episodeTimer:F1}s");
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
        _episodeDone = false;
        _lastRealTime = Time.unscaledTime;

        if (vehicle != null && obstacleCourse != null)
            _lastAngle = obstacleCourse.GetAngle(vehicle.transform.position);
    }
}
