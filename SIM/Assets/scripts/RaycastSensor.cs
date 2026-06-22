using UnityEngine;

/// <summary>
/// Casts a configurable fan of rays from the car's front and publishes
/// normalized distances via LedaBroker for RL training.
/// Also detects collisions and reports car position/heading.
/// </summary>
public class RaycastSensor : MonoBehaviour
{
    [Header("Raycast Configuration")]
    [Tooltip("Number of rays in the fan")]
    [SerializeField] int rayCount = 21;

    [Tooltip("Total angular spread of the ray fan (degrees)")]
    [SerializeField] float raySpreadAngle = 120f;

    [Tooltip("Maximum ray distance (meters)")]
    [SerializeField] float rayLength = 15f;

    [Tooltip("Layer mask for raycast hits")]
    [SerializeField] LayerMask hitLayers = ~0;

    [Tooltip("Local offset from this transform for ray origin (e.g. front bumper)")]
    [SerializeField] Vector3 rayOriginOffset = new Vector3(0f, 0.5f, 2.2f);

    [Header("References")]
    public LedaBroker broker;

    [Header("Collision Detection")]
    [SerializeField] bool detectCollisions = true;

    float[] _distances;
    bool _collided;

    void Awake()
    {
        _distances = new float[rayCount];
    }

    void FixedUpdate()
    {
        CastRays();
        PublishSensorData();
    }

    void CastRays()
    {
        Vector3 origin = transform.TransformPoint(rayOriginOffset);
        float halfSpread = raySpreadAngle * 0.5f;
        float step = rayCount > 1 ? raySpreadAngle / (rayCount - 1) : 0f;

        for (int i = 0; i < rayCount; i++)
        {
            float angle = -halfSpread + step * i;
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * transform.forward;

            if (Physics.Raycast(origin, direction, out RaycastHit hit, rayLength, hitLayers))
            {
                _distances[i] = 1 - (hit.distance / rayLength); // 0 = close, 1 = max range
            }
            else
            {
                _distances[i] = 0f; // No hit = clear
            }
        }
    }

    void PublishSensorData()
    {
        if (broker == null) return;

        // Rays — publish as JSON array
        var sb = new System.Text.StringBuilder(128);
        sb.Append("{\"value\":[");
        for (int i = 0; i < _distances.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(_distances[i].ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append("]}");
        broker.PublishRaw("vehicle/sensor/rays", sb.ToString());

        // Position and heading
        Vector3 pos = transform.position;
        float heading = transform.eulerAngles.y;
        string posJson = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{{\"value\":{{\"x\":{0:F2},\"z\":{1:F2},\"heading\":{2:F1}}}}}",
            pos.x, pos.z, heading);
        broker.PublishRaw("vehicle/sensor/position", posJson);

        // Collision flag
        if (_collided)
        {
            broker.PublishRaw("vehicle/sensor/collision", "{\"value\":1}");
            _collided = false;
        }
        else
        {
            broker.PublishRaw("vehicle/sensor/collision", "{\"value\":0}");
        }
    }

    void OnCollisionEnter(Collision collision)
    {
        if (detectCollisions)
            _collided = true;
    }

    /// <summary>Read the latest ray distances (normalized 0-1).</summary>
    public float[] GetDistances() => _distances;

    /// <summary>True if a collision occurred since last physics step.</summary>
    public bool HasCollided => _collided;

    // ─── Editor gizmos ───

    void OnDrawGizmos()
    {
        if (_distances == null || _distances.Length != rayCount) return;

        Vector3 origin = transform.TransformPoint(rayOriginOffset);
        float halfSpread = raySpreadAngle * 0.5f;
        float step = rayCount > 1 ? raySpreadAngle / (rayCount - 1) : 0f;

        for (int i = 0; i < rayCount; i++)
        {
            float angle = -halfSpread + step * i;
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * transform.forward;
            float dist = (1f - _distances[i]) * rayLength;

            Gizmos.color = Color.Lerp(Color.green, Color.red, _distances[i]);
            Gizmos.DrawRay(origin, direction * dist);
        }
    }
}
