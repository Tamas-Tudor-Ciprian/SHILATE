using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds the entire car scene at runtime, mirroring what CarBuilder does in the editor.
/// Runs automatically via [RuntimeInitializeOnLoadMethod] after the scene loads.
/// Uses SubsystemRegistration (earliest phase) to register a sceneLoaded callback,
/// ensuring the scene is built before StandaloneBootstrap (AfterSceneLoad) runs.
/// </summary>
public static class RuntimeSceneBuilder
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Init()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
    }

    static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;

        // Skip if the scene already has a VehicleController (e.g. objects were saved in editor)
        if (Object.FindFirstObjectByType<VehicleController>() != null)
        {
            Debug.Log("[RuntimeSceneBuilder] Scene already has a VehicleController, skipping build.");
            return;
        }

        Debug.Log("[RuntimeSceneBuilder] Scene is empty, building car scene at runtime...");
        BuildScene();
    }

    static Shader _cachedShader;

    static Shader GetSafeShader()
    {
        if (_cachedShader != null) return _cachedShader;

        // Create a temp primitive to grab the default shader Unity assigns.
        // This shader is always bundled in the build.
        GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var renderer = tmp.GetComponent<Renderer>();
        if (renderer != null && renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null)
            _cachedShader = renderer.sharedMaterial.shader;
        Object.DestroyImmediate(tmp);

        if (_cachedShader != null)
        {
            Debug.Log($"[RuntimeSceneBuilder] Got shader from primitive: {_cachedShader.name}");
            return _cachedShader;
        }

        // Last resort fallbacks
        var pipeline = GraphicsSettings.currentRenderPipeline;
        if (pipeline != null && pipeline.defaultMaterial != null)
            _cachedShader = pipeline.defaultMaterial.shader;
        else
            _cachedShader = Shader.Find("Standard");

        Debug.Log($"[RuntimeSceneBuilder] Shader fallback: {(_cachedShader != null ? _cachedShader.name : "NULL")}");
        return _cachedShader;
    }

    static Material MakeMaterial(Color color)
    {
        Shader shader = GetSafeShader();
        if (shader == null)
        {
            Debug.LogError("[RuntimeSceneBuilder] No shader available!");
            return null;
        }
        var mat = new Material(shader);
        mat.SetColor("_BaseColor", color);  // URP uses _BaseColor
        mat.color = color;                   // fallback for built-in
        return mat;
    }

    static void BuildScene()
    {
        // ── Ground Plane ──
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(50f, 1f, 50f);

        PhysicsMaterial groundMat = new PhysicsMaterial("Tarmac")
        {
            staticFriction = 0.8f,
            dynamicFriction = 0.6f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Average,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
        ground.GetComponent<Collider>().material = groundMat;
        ground.GetComponent<Renderer>().material = MakeMaterial(new Color(0.3f, 0.3f, 0.3f));
        Debug.Log("[RuntimeSceneBuilder] Ground created.");

        // ── Car Root ──
        GameObject car = new GameObject("Car");
        car.transform.position = new Vector3(32.5f, 0.5f, 0f);
        Rigidbody rb = car.AddComponent<Rigidbody>();
        rb.mass = 1500f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.inertiaTensor = new Vector3(1000, 1000, 1000);
        rb.centerOfMass = new Vector3(0, -0.2f, 0);

        // ── Car Body (visual) ──
        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(car.transform);
        body.transform.localPosition = new Vector3(0f, 0.35f, 0f);
        body.transform.localScale = new Vector3(1.8f, 0.6f, 4.2f);
        body.GetComponent<Renderer>().material = MakeMaterial(Color.green);

        // ── Visual Wheels Parent ──
        GameObject visualWheelsParent = new GameObject("VisualWheels");
        visualWheelsParent.transform.SetParent(car.transform);
        visualWheelsParent.transform.localPosition = Vector3.zero;

        // ── Physics Wheels Parent ──
        GameObject physicsWheelsParent = new GameObject("PhysicsWheels");
        physicsWheelsParent.transform.SetParent(car.transform);
        physicsWheelsParent.transform.localPosition = Vector3.zero;

        // ── Cabin ──
        GameObject cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cabin.name = "Cabin";
        cabin.transform.SetParent(car.transform);
        cabin.transform.localPosition = new Vector3(0f, 0.85f, -0.2f);
        cabin.transform.localScale = new Vector3(1.5f, 0.5f, 2.0f);
        Object.Destroy(cabin.GetComponent<Collider>());
        cabin.GetComponent<Renderer>().material = MakeMaterial(new Color(0.2f, 0.8f, 0.1f));

        // ── Wheel dimensions ──
        float wheelRadius = 0.35f;
        float suspensionDistance = 0.2f;
        float wheelX = 1.0f;
        float wheelFrontZ = 1.3f;
        float wheelRearZ = -1.3f;
        float wheelY = 0f;

        // ── WheelColliders ──
        WheelCollider wcFL = CreateWheelCollider(physicsWheelsParent, "WheelCollider_FL", new Vector3(-wheelX, wheelY, wheelFrontZ), wheelRadius, suspensionDistance);
        WheelCollider wcFR = CreateWheelCollider(physicsWheelsParent, "WheelCollider_FR", new Vector3(wheelX, wheelY, wheelFrontZ), wheelRadius, suspensionDistance);
        WheelCollider wcRL = CreateWheelCollider(physicsWheelsParent, "WheelCollider_RL", new Vector3(-wheelX, wheelY, wheelRearZ), wheelRadius, suspensionDistance);
        WheelCollider wcRR = CreateWheelCollider(physicsWheelsParent, "WheelCollider_RR", new Vector3(wheelX, wheelY, wheelRearZ), wheelRadius, suspensionDistance);

        // ── Visual Wheels ──
        Transform meshFL = CreateWheelMesh(visualWheelsParent, "WheelMesh_FL", new Vector3(-wheelX, wheelY, wheelFrontZ), wheelRadius);
        Transform meshFR = CreateWheelMesh(visualWheelsParent, "WheelMesh_FR", new Vector3(wheelX, wheelY, wheelFrontZ), wheelRadius);
        Transform meshRL = CreateWheelMesh(visualWheelsParent, "WheelMesh_RL", new Vector3(-wheelX, wheelY, wheelRearZ), wheelRadius);
        Transform meshRR = CreateWheelMesh(visualWheelsParent, "WheelMesh_RR", new Vector3(wheelX, wheelY, wheelRearZ), wheelRadius);

        // ── VehicleController ──
        VehicleController vc = car.AddComponent<VehicleController>();
        vc.wheelFL = wcFL;
        vc.wheelFR = wcFR;
        vc.wheelRL = wcRL;
        vc.wheelRR = wcRR;
        vc.wheelMeshFL = meshFL;
        vc.wheelMeshFR = meshFR;
        vc.wheelMeshRL = meshRL;
        vc.wheelMeshRR = meshRR;

        // ── ManualDriveInput ──
        ManualDriveInput manual = car.AddComponent<ManualDriveInput>();
        manual.vehicle = vc;

        // ── SimulationRunner ──
        SimulationRunner runner = car.AddComponent<SimulationRunner>();
        runner.vehicle = vc;
        runner.autoStart = false;
        manual.simulationRunner = runner;

        // ── LedaBroker ──
        LedaBroker broker = Object.FindFirstObjectByType<LedaBroker>();
        if (broker == null)
        {
            GameObject brokerGO = new GameObject("LedaBroker");
            broker = brokerGO.AddComponent<LedaBroker>();
        }

        // ── VehicleTelemetryBridge ──
        VehicleTelemetryBridge bridge = car.AddComponent<VehicleTelemetryBridge>();
        bridge.vehicle = vc;
        bridge.broker = broker;

        // ── RemoteDriveInput ──
        RemoteDriveInput remoteInput = car.AddComponent<RemoteDriveInput>();
        remoteInput.vehicle = vc;
        remoteInput.manualInput = manual;
        remoteInput.enabled = false;
        broker.remoteInput = remoteInput;
        manual.remoteInput = remoteInput;

        // ── TimeScaleController ──
        TimeScaleController tsController = car.AddComponent<TimeScaleController>();
        tsController.broker = broker;

        // ── RaycastSensor ──
        RaycastSensor raycastSensor = car.AddComponent<RaycastSensor>();
        raycastSensor.broker = broker;

        // ── ObstacleCourse ──
        GameObject courseGO = new GameObject("ObstacleCourse");
        ObstacleCourse obstacleCourse = courseGO.AddComponent<ObstacleCourse>();
        obstacleCourse.vehicle = vc;
        obstacleCourse.broker = broker;

        // ── TrainingBridge ──
        TrainingBridge trainingBridge = car.AddComponent<TrainingBridge>();
        trainingBridge.broker = broker;
        trainingBridge.vehicle = vc;
        trainingBridge.raycastSensor = raycastSensor;
        trainingBridge.obstacleCourse = obstacleCourse;

        // Wire obstacle course back to training bridge
        obstacleCourse.trainingBridge = trainingBridge;

        // ── Camera ──
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            CameraFollow follow = mainCam.gameObject.GetComponent<CameraFollow>();
            if (follow == null)
                follow = mainCam.gameObject.AddComponent<CameraFollow>();
            follow.target = car.transform;

            mainCam.transform.position = car.transform.TransformPoint(follow.offset);
            mainCam.transform.LookAt(car.transform);
        }

        Debug.Log("[RuntimeSceneBuilder] Car scene built successfully. " +
            $"Car at {car.transform.position}, Camera at {(mainCam != null ? mainCam.transform.position.ToString() : "N/A")}");
    }

    static WheelCollider CreateWheelCollider(GameObject parent, string name, Vector3 localPos, float radius, float suspDist)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent.transform);
        go.transform.localPosition = localPos;

        WheelCollider wc = go.AddComponent<WheelCollider>();
        wc.radius = radius;
        wc.suspensionDistance = suspDist;

        JointSpring spring = wc.suspensionSpring;
        spring.spring = 35000f;
        spring.damper = 5500f;
        spring.targetPosition = 0.35f;
        wc.suspensionSpring = spring;

        wc.mass = 20f;

        WheelFrictionCurve fwd = wc.forwardFriction;
        fwd.extremumSlip = 0.4f;
        fwd.extremumValue = 1f;
        fwd.asymptoteSlip = 0.8f;
        fwd.asymptoteValue = 0.5f;
        fwd.stiffness = 1f;
        wc.forwardFriction = fwd;

        WheelFrictionCurve side = wc.sidewaysFriction;
        side.extremumSlip = 0.25f;
        side.extremumValue = 1f;
        side.asymptoteSlip = 0.5f;
        side.asymptoteValue = 0.75f;
        side.stiffness = 1f;
        wc.sidewaysFriction = side;

        return wc;
    }

    static Transform CreateWheelMesh(GameObject parent, string name, Vector3 localPos, float radius)
    {
        GameObject pivot = new GameObject(name + "_Pivot");
        pivot.transform.SetParent(parent.transform);
        pivot.transform.localPosition = localPos;

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(pivot.transform);
        go.transform.localPosition = Vector3.zero;
        float diameter = radius * 2f;
        go.transform.localScale = new Vector3(diameter, 0.15f, diameter);
        go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
        Object.Destroy(go.GetComponent<Collider>());
        go.GetComponent<Renderer>().material = MakeMaterial(new Color(0.4f, 0.25f, 0.1f));
        return pivot.transform;
    }
}
