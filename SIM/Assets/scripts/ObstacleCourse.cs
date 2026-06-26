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

    /// <summary>Set by TrainingBridge before Reset() to control the curriculum obstacle count.</summary>
    public int TargetObstacleCount { get; set; } = 0;

    readonly List<GameObject> _obstacles = new List<GameObject>();
    readonly List<GameObject> _walls = new List<GameObject>();
    Material _obstacleMaterial;
    Material _wallMaterial;
    GameObject _roadSurface;

    /// <summary>
    /// Set to true by TrainingBridge when the previous episode ended with a completed lap.
    /// Reset() will only regenerate obstacles when this is true.
    /// </summary>
    public bool ShouldRespawnObstacles { get; set; } = false;

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
    /// Resets the episode. Only regenerates obstacles when <see cref="ShouldRespawnObstacles"/> is true
    /// (i.e. the previous episode ended with a completed lap). Otherwise the car is repositioned
    /// but the obstacle layout stays the same so the agent must learn to beat the same course.
    /// Called by Leda via leda/control/reset MQTT command.
    /// </summary>
    public void Reset()
    {
        if (!Application.isPlaying) return;

        if (ShouldRespawnObstacles)
        {
            ClearObstacles();
            SpawnObstacles();
            ShouldRespawnObstacles = false;
            Debug.Log("[ObstacleCourse] Lap completed — new obstacle layout generated");
        }

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
        if (_roadSurface != null) { Destroy(_roadSurface); _roadSurface = null; }
    }

    void SpawnWalls()
    {
        ClearWalls();

        // Inner wall: normals face outward (visible from the track)
        _walls.Add(CreateCylinderWall("InnerWall", innerRadius, true));
        // Outer wall: normals face inward (visible from the track)
        _walls.Add(CreateCylinderWall("OuterWall", outerRadius, false));

        SpawnRoadSurface();

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

    /// <summary>
    /// Generates a flat annular road-surface mesh (XZ plane) with a runtime-baked texture:
    /// solid edge stripes on the inner and outer sides, and a dashed centre line that scrolls
    /// past as the vehicle drives, giving a natural speed-gauging reference.
    ///
    /// Texture U axis = radial (0 = inner edge, 1 = outer edge).
    /// Texture V axis = one dash cycle; the material tiles it <c>tilesAround</c> times so each
    /// tile covers roughly (trackCircumference / tilesAround) metres.
    /// </summary>
    void SpawnRoadSurface()
    {
        const int   segs       = 128;   // ring smoothness
        const int   tilesAround = 30;   // dash pattern repeats around the track (~6.8 m per tile)
        const float yOffset    = 0.01f; // just above the ground plane to prevent z-fighting

        int vertCount = (segs + 1) * 2;
        var positions = new Vector3[vertCount];
        var uvs       = new Vector2[vertCount];
        var tris      = new int[segs * 6];

        for (int i = 0; i <= segs; i++)
        {
            float t     = (float)i / segs;
            float angle = t * Mathf.PI * 2f;
            float cos   = Mathf.Cos(angle);
            float sin   = Mathf.Sin(angle);

            int vi = i * 2;
            positions[vi]     = new Vector3(trackCenter.x + innerRadius * cos, yOffset, trackCenter.z + innerRadius * sin);
            positions[vi + 1] = new Vector3(trackCenter.x + outerRadius * cos, yOffset, trackCenter.z + outerRadius * sin);

            // u=0 at inner edge, u=1 at outer edge; v goes 0..1 once around the track
            uvs[vi]     = new Vector2(0f, t);
            uvs[vi + 1] = new Vector2(1f, t);
        }

        for (int i = 0; i < segs; i++)
        {
            int inner0 = i * 2;
            int outer0 = i * 2 + 1;
            int inner1 = (i + 1) * 2;
            int outer1 = (i + 1) * 2 + 1;
            int ti     = i * 6;

            // CCW winding → RecalculateNormals produces +Y (upward) normals
            tris[ti    ] = inner0;
            tris[ti + 1] = inner1;
            tris[ti + 2] = outer1;
            tris[ti + 3] = inner0;
            tris[ti + 4] = outer1;
            tris[ti + 5] = outer0;
        }

        var mesh = new Mesh { name = "RoadRingMesh" };
        mesh.vertices  = positions;
        mesh.uv        = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // ── Procedural road-marking texture ──────────────────────────────────
        // U = radial (inner→outer); V = one dash cycle (tiled tilesAround times)
        const int   texW       = 256;
        const int   texH       = 256;
        const float edgeW      = 0.07f;   // solid white stripe on each edge
        const float centreHalf = 0.025f;  // half-width of dashed centre line
        const float dashFrac   = 0.55f;   // 55% dash / 45% gap per tile

        var asphalt   = new Color(0.10f, 0.10f, 0.10f);
        var lineColor = new Color(0.92f, 0.90f, 0.82f); // warm white

        var tex = new Texture2D(texW, texH, TextureFormat.RGB24, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Repeat
        };

        for (int y = 0; y < texH; y++)
        {
            float v      = (float)y / texH;
            bool  dashOn = v < dashFrac;

            for (int x = 0; x < texW; x++)
            {
                float u = (float)x / texW;
                bool isLine = u < edgeW
                           || u > 1f - edgeW
                           || (dashOn && Mathf.Abs(u - 0.5f) < centreHalf);

                tex.SetPixel(x, y, isLine ? lineColor : asphalt);
            }
        }
        tex.Apply();

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "RoadMat" };
        mat.SetFloat("_Smoothness", 0.05f);
        mat.SetFloat("_Metallic",   0f);
        mat.mainTexture      = tex;
        mat.mainTextureScale = new Vector2(1f, tilesAround);

        _roadSurface = new GameObject("RoadSurface");
        _roadSurface.AddComponent<MeshFilter>().mesh = mesh;
        var mr = _roadSurface.AddComponent<MeshRenderer>();
        mr.material           = mat;
        mr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows     = true;

        Debug.Log($"[ObstacleCourse] Road surface spawned (inner R={innerRadius}, outer R={outerRadius}, {tilesAround} dash tiles)");
    }

    /// <summary>
    /// Spawns obstacles using stratified angular placement: the track is divided into equal
    /// angular sectors and exactly one obstacle is placed per sector (with random jitter within
    /// the sector and random radius). This guarantees even circumferential distribution and
    /// eliminates bunching. Sectors overlapping the car start zone are skipped.
    /// </summary>
    void SpawnObstacles()
    {
        if (TargetObstacleCount == 0)
        {
            Debug.Log("[ObstacleCourse] No obstacles for this curriculum stage");
            return;
        }

        float margin = 1.5f;
        float sectorDeg = 360f / TargetObstacleCount; // degrees per sector
        float carStartAngleDeg = 0f;            // car always starts at angle 0 (positive X)
        // Exclude sectors whose midpoint falls within 1.5 sectors of the start,
        // but cap at 90° so small counts (1-2) don't lose every sector.
        float excludeHalfArc = Mathf.Min(sectorDeg * 1.5f, 90f);

        var positions = new List<Vector3>();

        for (int i = 0; i < TargetObstacleCount; i++)
        {
            float sectorMid = i * sectorDeg + sectorDeg * 0.5f;

            // Skip sectors too close to the car start position
            if (Mathf.Abs(Mathf.DeltaAngle(sectorMid, carStartAngleDeg)) < excludeHalfArc)
                continue;

            // Try a few times to place within this sector (min-spacing guard)
            bool placed = false;
            for (int attempt = 0; attempt < 10 && !placed; attempt++)
            {
                // Jitter angle within [10%, 90%] of the sector to avoid clustering at edges
                float jitterDeg = Random.Range(sectorDeg * 0.1f, sectorDeg * 0.9f);
                float angleDeg  = i * sectorDeg + jitterDeg;
                float angleRad  = angleDeg * Mathf.Deg2Rad;

                float r = Random.Range(innerRadius + margin, outerRadius - margin);
                float x = trackCenter.x + r * Mathf.Cos(angleRad);
                float z = trackCenter.z + r * Mathf.Sin(angleRad);
                Vector3 pos = new Vector3(x, 0f, z);

                // Minimum spacing check against already-placed obstacles
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

                GameObject obs = GameObject.CreatePrimitive(
                    Random.value > 0.5f ? PrimitiveType.Cube : PrimitiveType.Cylinder);
                obs.name = $"Obstacle_{i}";

                float sx = Random.Range(obstacleMinSize.x, obstacleMaxSize.x);
                float sy = Random.Range(obstacleMinSize.y, obstacleMaxSize.y);
                float sz = Random.Range(obstacleMinSize.z, obstacleMaxSize.z);
                obs.transform.localScale = new Vector3(sx, sy, sz);
                obs.transform.position = new Vector3(pos.x, sy * 0.5f, pos.z);
                obs.GetComponent<Renderer>().material = _obstacleMaterial;

                _obstacles.Add(obs);
                placed = true;
            }
        }

        Debug.Log($"[ObstacleCourse] Spawned {_obstacles.Count}/{TargetObstacleCount} obstacles " +
                  $"(stratified, R={innerRadius}-{outerRadius})");
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

        // Put the car in Drive so the RL trainer can apply throttle immediately.
        // ResetInputs() leaves the car in Park; we must satisfy the brake safety
        // gate to shift out of Park before the Python gear-D command can arrive.
        vehicle.BrakeInput = 0.5f;
        vehicle.RequestGearChange(VehicleController.GearState.Drive);
        vehicle.BrakeInput = 0f;

        Debug.Log("[ObstacleCourse] Car reset to track start position");
    }
}
