using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Spawns a randomized obstacle course at runtime.
/// Supports reset via LedaBroker MQTT commands for RL training episodes.
/// </summary>
public class ObstacleCourse : MonoBehaviour
{
    [Header("Course Layout")]
    [Tooltip("Number of obstacles to spawn")]
    [SerializeField] int obstacleCount = 12;

    [Tooltip("Length of the course area along Z axis (meters)")]
    [SerializeField] float areaLength = 80f;

    [Tooltip("Width of the course area along X axis (meters)")]
    [SerializeField] float areaWidth = 20f;

    [Tooltip("Minimum spacing between obstacles (meters)")]
    [SerializeField] float minSpacing = 3f;

    [Tooltip("Start of obstacle area offset from car start (meters ahead)")]
    [SerializeField] float startOffset = 10f;

    [Header("Obstacle Appearance")]
    [SerializeField] Vector3 obstacleMinSize = new Vector3(1f, 2f, 1f);
    [SerializeField] Vector3 obstacleMaxSize = new Vector3(3f, 3f, 3f);

    [Header("Car Reset")]
    [Tooltip("The vehicle to reset on new episodes")]
    public VehicleController vehicle;

    [Tooltip("Start position for the car")]
    [SerializeField] Vector3 carStartPosition = Vector3.zero;

    [Tooltip("Start rotation for the car (euler angles)")]
    [SerializeField] Vector3 carStartRotation = Vector3.zero;

    [Header("References")]
    public LedaBroker broker;
    public TrainingBridge trainingBridge;

    readonly List<GameObject> _obstacles = new List<GameObject>();
    Material _obstacleMaterial;

    /// <summary>Z position of the finish line (end of obstacle area).</summary>
    public float FinishLineZ => carStartPosition.z + startOffset + areaLength;

    /// <summary>Start Z position of the car.</summary>
    public float StartZ => carStartPosition.z;

    void Awake()
    {
        _obstacleMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = new Color(0.2f, 0.4f, 0.9f)
        };
    }

    void OnEnable()
    {
        if (broker != null)
            broker.OnResetRequested += Reset;

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

    void SpawnObstacles()
    {
        float halfWidth = areaWidth * 0.5f;
        float areaStartZ = carStartPosition.z + startOffset;
        int maxAttempts = obstacleCount * 10;
        int placed = 0;

        List<Vector3> positions = new List<Vector3>();

        for (int attempt = 0; attempt < maxAttempts && placed < obstacleCount; attempt++)
        {
            float x = Random.Range(-halfWidth, halfWidth);
            float z = Random.Range(areaStartZ, areaStartZ + areaLength);
            Vector3 pos = new Vector3(x, 0f, z);

            // Check minimum spacing
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

            positions.Add(pos);

            // Create obstacle
            GameObject obs = GameObject.CreatePrimitive(
                Random.value > 0.5f ? PrimitiveType.Cube : PrimitiveType.Cylinder);
            obs.name = $"Obstacle_{placed}";

            float sx = Random.Range(obstacleMinSize.x, obstacleMaxSize.x);
            float sy = Random.Range(obstacleMinSize.y, obstacleMaxSize.y);
            float sz = Random.Range(obstacleMinSize.z, obstacleMaxSize.z);
            obs.transform.localScale = new Vector3(sx, sy, sz);

            obs.transform.position = new Vector3(pos.x, sy * 0.5f, pos.z);
            obs.GetComponent<Renderer>().material = _obstacleMaterial;

            // Make sure it has a collider (primitives do by default)
            _obstacles.Add(obs);
            placed++;
        }

        Debug.Log($"[ObstacleCourse] Spawned {placed} obstacles in {areaLength}m × {areaWidth}m area");
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

        vehicle.transform.position = carStartPosition;
        vehicle.transform.eulerAngles = carStartRotation;
        vehicle.ResetInputs();

        Debug.Log("[ObstacleCourse] Car reset to start position");
    }
}
