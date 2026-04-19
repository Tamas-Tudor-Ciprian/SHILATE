using UnityEngine;
using UnityEngine.Rendering;
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

    [Tooltip("Number of mesh segments per wall cylinder (higher = smoother)")]
    [SerializeField] int wallSegments = 64;

    [Tooltip("Height of the track walls (meters)")]
    [SerializeField] float wallHeight = 3f;

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

        _obstacleMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _obstacleMaterial.SetColor("_BaseColor", new Color(0.2f, 0.4f, 0.9f));
        _obstacleMaterial.color = new Color(0.2f, 0.4f, 0.9f);

        _wallMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _wallMaterial.SetColor("_BaseColor", new Color(0.5f, 0.5f, 0.5f));
        _wallMaterial.color = new Color(0.5f, 0.5f, 0.5f);
    }

    void OnEnable()
    {
        if (broker != null)
            broker.OnResetRequested += Reset;

        SpawnWalls();
        SpawnObstacles();
        ResetCar();
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

        // Inner wall: normals face outward (visible from the track)
        _walls.Add(CreateCylinderWall("InnerWall", innerRadius, true));
        // Outer wall: normals face inward (visible from the track)
        _walls.Add(CreateCylinderWall("OuterWall", outerRadius, false));

        Debug.Log($"[ObstacleCourse] Spawned cylinder walls: inner R={innerRadius}, outer R={outerRadius}, {wallSegments} segments");
    }

    GameObject CreateCylinderWall(string wallName, float radius, bool normalsOutward)
    {
        int segs = wallSegments;
        int vertCount = (segs + 1) * 2;
        var vertices = new Vector3[vertCount];
        var normals  = new Vector3[vertCount];
        var uvs      = new Vector2[vertCount];
        var triangles = new int[segs * 6];

        float sign = normalsOutward ? 1f : -1f;

        for (int i = 0; i <= segs; i++)
        {
            float t = (float)i / segs;
            float angle = t * Mathf.PI * 2f;
            float cx = Mathf.Cos(angle);
            float cz = Mathf.Sin(angle);

            float x = trackCenter.x + radius * cx;
            float z = trackCenter.z + radius * cz;

            int bot = i * 2;
            int top = i * 2 + 1;

            vertices[bot] = new Vector3(x, 0f, z);
            vertices[top] = new Vector3(x, wallHeight, z);

            Vector3 n = new Vector3(cx * sign, 0f, cz * sign);
            normals[bot] = n;
            normals[top] = n;

            uvs[bot] = new Vector2(t, 0f);
            uvs[top] = new Vector2(t, 1f);
        }

        for (int i = 0; i < segs; i++)
        {
            int bl = i * 2;
            int tl = i * 2 + 1;
            int br = i * 2 + 2;
            int tr = i * 2 + 3;
            int ti = i * 6;

            if (normalsOutward)
            {
                triangles[ti    ] = bl;
                triangles[ti + 1] = tl;
                triangles[ti + 2] = br;
                triangles[ti + 3] = br;
                triangles[ti + 4] = tl;
                triangles[ti + 5] = tr;
            }
            else
            {
                triangles[ti    ] = bl;
                triangles[ti + 1] = br;
                triangles[ti + 2] = tl;
                triangles[ti + 3] = br;
                triangles[ti + 4] = tr;
                triangles[ti + 5] = tl;
            }
        }

        var mesh = new Mesh { name = wallName + "Mesh" };
        mesh.vertices  = vertices;
        mesh.normals   = normals;
        mesh.uv        = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();

        var go = new GameObject(wallName);
        go.AddComponent<MeshFilter>().mesh = mesh;
        go.AddComponent<MeshRenderer>().material = _wallMaterial;
        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;
	
	//we scale the height to by 2 units high here
	go.transform.localScale = new Vector3(1f,2f,1f);

	//return the created game object
        return go;
    }

    void SpawnObstacles()
    {
        int maxAttempts = obstacleCount * 10;
        int placed = 0;
        float margin = 1.5f;
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
