using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Spawns a circular-track obstacle course at runtime.
/// The track is an annular ring between an inner and outer wall.
/// Random obstacles are placed in the drivable area between the walls.
/// Supports reset via LedaBroker MQTT commands for RL training episodes.
/// </summary>
public class ObstacleCourse : MonoBehaviour
{
    [Header("Circular Track")]
    [Tooltip("Center of the circular track")]
    [SerializeField] Vector3 trackCenter = Vector3.zero;

    [Tooltip("Inner wall radius (meters)")]
    [SerializeField] float innerRadius = 25f;

    [Tooltip("Outer wall radius (meters)")]
    [SerializeField] float outerRadius = 40f;

    [Tooltip("Number of wall segments per circle (higher = smoother)")]
    [SerializeField] int wallSegments = 64;

    [Tooltip("Height of the track walls (meters)")]
    [SerializeField] float wallHeight = 3f;

    [Tooltip("Thickness of wall segments (meters)")]
    [SerializeField] float wallThickness = 0.5f;

    [Header("Obstacles")]
    [Tooltip("Number of obstacles to spawn on the track")]
    [SerializeField] int obstacleCount = 12;

    [Tooltip("Minimum spacing between obstacles (meters)")]
    [SerializeField] float minSpacing = 3f;

    [SerializeField] Vector3 obstacleMinSize = new Vector3(1f, 2f, 1f);
    [SerializeField] Vector3 obstacleMaxSize = new Vector3(3f, 3f, 3f);

    [Header("Car Reset")]
    [Tooltip("The vehicle to reset on new episodes")]
    public VehicleController vehicle;

    [Header("References")]
    public LedaBroker broker;
    public TrainingBridge trainingBridge;

    readonly List<GameObject> _obstacles = new List<GameObject>();
    readonly List<GameObject> _walls = new List<GameObject>();
    Material _obstacleMaterial;
    Material _wallMaterial;

    /// <summary>Center of the circular track.</summary>
    public Vector3 TrackCenter => trackCenter;

    /// <summary>Inner wall radius.</summary>
    public float InnerRadius => innerRadius;

    /// <summary>Outer wall radius.</summary>
    public float OuterRadius => outerRadius;

    /// <summary>Radius of the track centerline.</summary>
    public float TrackCenterRadius => (innerRadius + outerRadius) * 0.5f;

    /// <summary>
    /// Returns the angle (degrees, 0-360) of a world position relative to track center.
    /// 0 degrees = positive X direction, increasing counter-clockwise.
    /// </summary>
    public float GetAngle(Vector3 worldPos)
    {
        Vector3 offset = worldPos - trackCenter;
        float angle = Mathf.Atan2(offset.z, offset.x) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        return angle;
    }

    void Awake()
    {
        _obstacleMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = new Color(0.2f, 0.4f, 0.9f)
        };
        _wallMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = new Color(0.5f, 0.5f, 0.5f)
        };
    }

    void OnEnable()
    {
        if (broker != null)
            broker.OnResetRequested += Reset;

        SpawnWalls();
        SpawnObstacles();
    }

    void OnDisable()
    {
        if (broker != null)
            broker.OnResetRequested -= Reset;
    }

    /// <summary>
    /// Destroy all obstacles, spawn new random layout, reset car to start.
    /// Called by Leda via leda/control/reset MQTT command.
    /// </summary>
    public void Reset()
    {
        if (!Application.isPlaying) return;
        ClearObstacles();
        SpawnObstacles();
        ResetCar();

        if (trainingBridge != null)
            trainingBridge.ResetEpisodeState();
    }

    void ClearObstacles()
    {
        foreach (var obs in _obstacles)
        {
            if (obs != null) Destroy(obs);
        }
        _obstacles.Clear();
    }

    void ClearWalls()
    {
        foreach (var wall in _walls)
        {
            if (wall != null) Destroy(wall);
        }
        _walls.Clear();
    }

    void SpawnWalls()
    {
        ClearWalls();

        float angleStep = 360f / wallSegments;
        float innerArc = 2f * innerRadius * Mathf.Sin(angleStep * 0.5f * Mathf.Deg2Rad);
        float outerArc = 2f * outerRadius * Mathf.Sin(angleStep * 0.5f * Mathf.Deg2Rad);

        for (int i = 0; i < wallSegments; i++)
        {
            float midAngle = (i + 0.5f) * angleStep * Mathf.Deg2Rad;

            CreateWallSegment(innerRadius, midAngle, innerArc, $"InnerWall_{i}");
            CreateWallSegment(outerRadius, midAngle, outerArc, $"OuterWall_{i}");
        }

        Debug.Log($"[ObstacleCourse] Spawned circular walls: inner R={innerRadius}, outer R={outerRadius}, {wallSegments} segments each");
    }

    void CreateWallSegment(float radius, float angle, float arcLength, string segmentName)
    {
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = segmentName;

        float x = trackCenter.x + radius * Mathf.Cos(angle);
        float z = trackCenter.z + radius * Mathf.Sin(angle);
        wall.transform.position = new Vector3(x, wallHeight * 0.5f, z);
        wall.transform.localScale = new Vector3(wallThickness, wallHeight, arcLength);

        // Rotate so the long side (Z) is tangent to the circle
        float angleDeg = angle * Mathf.Rad2Deg;
        wall.transform.rotation = Quaternion.Euler(0f, -angleDeg + 90f, 0f);

        wall.GetComponent<Renderer>().material = _wallMaterial;
        _walls.Add(wall);
    }

    void SpawnObstacles()
    {
        int maxAttempts = obstacleCount * 10;
        int placed = 0;
        float margin = wallThickness + 0.5f;
        List<Vector3> positions = new List<Vector3>();

        // Car start position for exclusion zone
        float centerR = TrackCenterRadius;
        Vector3 carStart = new Vector3(trackCenter.x + centerR, 0f, trackCenter.z);

        for (int attempt = 0; attempt < maxAttempts && placed < obstacleCount; attempt++)
        {
            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float r = Random.Range(innerRadius + margin, outerRadius - margin);

            float x = trackCenter.x + r * Mathf.Cos(angle);
            float z = trackCenter.z + r * Mathf.Sin(angle);
            Vector3 pos = new Vector3(x, 0f, z);

            // Check minimum spacing against existing obstacles
            bool tooClose = false;
            foreach (var existing in positions)
            {
                if (Vector3.Distance(pos, existing) < minSpacing)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose) continue;

            // Keep area near car start clear
            if (Vector3.Distance(pos, carStart) < minSpacing * 2f)
                continue;

            positions.Add(pos);

            GameObject obs = GameObject.CreatePrimitive(
                Random.value > 0.5f ? PrimitiveType.Cube : PrimitiveType.Cylinder);
            obs.name = $"Obstacle_{placed}";

            float sx = Random.Range(obstacleMinSize.x, obstacleMaxSize.x);
            float sy = Random.Range(obstacleMinSize.y, obstacleMaxSize.y);
            float sz = Random.Range(obstacleMinSize.z, obstacleMaxSize.z);
            obs.transform.localScale = new Vector3(sx, sy, sz);
            obs.transform.position = new Vector3(pos.x, sy * 0.5f, pos.z);
            obs.GetComponent<Renderer>().material = _obstacleMaterial;

            _obstacles.Add(obs);
            placed++;
        }

        Debug.Log($"[ObstacleCourse] Spawned {placed} obstacles on circular track (R={innerRadius}-{outerRadius})");
    }

    void ResetCar()
    {
        if (vehicle == null) return;

        var rb = vehicle.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // Place car on the track centerline at angle 0 (positive X from center)
        float centerR = TrackCenterRadius;
        vehicle.transform.position = new Vector3(trackCenter.x + centerR, 0f, trackCenter.z);

        // At angle 0, the tangent going counter-clockwise is +Z
        vehicle.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        vehicle.ResetInputs();

        Debug.Log("[ObstacleCourse] Car reset to track start position");
    }
}
