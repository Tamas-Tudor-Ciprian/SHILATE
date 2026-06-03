using UnityEngine;

/// <summary>
/// Single source of truth for spawning the car and all its wired-up components.
/// Called by both CarBuilder (editor menu) and RuntimeSceneBuilder (runtime auto-spawn).
/// </summary>
public static class CarFactory
{
    // ── Public entry points ──────────────────────────────────────────────────

    public static GameObject BuildGround()
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(50f, 1f, 50f);

        PhysicsMaterial groundMat = new PhysicsMaterial("Tarmac")
        {
            staticFriction   = 0.8f,
            dynamicFriction  = 0.6f,
            bounciness       = 0f,
            frictionCombine  = PhysicsMaterialCombine.Average,
            bounceCombine    = PhysicsMaterialCombine.Minimum
        };
        ground.GetComponent<Collider>().material = groundMat;
        ground.GetComponent<Renderer>().material = MakeMaterial(new Color(0.3f, 0.3f, 0.3f));
        return ground;
    }

    /// <summary>
    /// Builds the complete car hierarchy, wires all components, and optionally
    /// attaches a BMW GLB visual if one is present in Resources/.
    /// Returns the car root GameObject.
    /// </summary>
    public static GameObject BuildCar(Vector3 position)
    {
        // ── Car Root ──
        GameObject car = new GameObject("Car");
        car.transform.position = position;

        Rigidbody rb = car.AddComponent<Rigidbody>();
        rb.mass          = 1500f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.inertiaTensor = new Vector3(1000f, 1000f, 1000f);
        rb.centerOfMass  = new Vector3(0f, -0.2f, 0f);

        // ── Body collider (invisible — BMW GLB provides the visual) ──
        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(car.transform);
        body.transform.localPosition = new Vector3(0f, 0.35f, 0f);
        body.transform.localScale    = new Vector3(1.8f, 0.6f, 4.2f);
        body.GetComponent<Renderer>().enabled = false;

        // ── Wheel parents ──
        GameObject visualWheelsParent  = new GameObject("VisualWheels");
        visualWheelsParent.transform.SetParent(car.transform);
        visualWheelsParent.transform.localPosition = Vector3.zero;

        GameObject physicsWheelsParent = new GameObject("PhysicsWheels");
        physicsWheelsParent.transform.SetParent(car.transform);
        physicsWheelsParent.transform.localPosition = Vector3.zero;

        // ── Cabin (no collider, no renderer — BMW GLB handles visuals) ──
        GameObject cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cabin.name = "Cabin";
        cabin.transform.SetParent(car.transform);
        cabin.transform.localPosition = new Vector3(0f, 0.85f, -0.2f);
        cabin.transform.localScale    = new Vector3(1.5f, 0.5f, 2.0f);
        DestroyCollider(cabin.GetComponent<Collider>());
        cabin.GetComponent<Renderer>().enabled = false;

        // ── BMW GLB visual ──
        AttachBMWVisual(car);

        // ── Wheel dimensions ──
        const float wheelRadius         = 0.35f;
        const float suspensionDistance  = 0.2f;
        const float wheelX              = 1.0f;
        const float wheelFrontZ         =  1.3f;
        const float wheelRearZ          = -1.3f;
        const float wheelY              =  0f;

        // ── WheelColliders ──
        WheelCollider wcFL = CreateWheelCollider(physicsWheelsParent, "WheelCollider_FL", new Vector3(-wheelX, wheelY,  wheelFrontZ), wheelRadius, suspensionDistance);
        WheelCollider wcFR = CreateWheelCollider(physicsWheelsParent, "WheelCollider_FR", new Vector3( wheelX, wheelY,  wheelFrontZ), wheelRadius, suspensionDistance);
        WheelCollider wcRL = CreateWheelCollider(physicsWheelsParent, "WheelCollider_RL", new Vector3(-wheelX, wheelY,  wheelRearZ),  wheelRadius, suspensionDistance);
        WheelCollider wcRR = CreateWheelCollider(physicsWheelsParent, "WheelCollider_RR", new Vector3( wheelX, wheelY,  wheelRearZ),  wheelRadius, suspensionDistance);

        // ── Visual wheel meshes (hidden — BMW GLB handles wheel visuals) ──
        Transform meshFL = CreateWheelMesh(visualWheelsParent, "WheelMesh_FL", new Vector3(-wheelX, wheelY,  wheelFrontZ), wheelRadius);
        Transform meshFR = CreateWheelMesh(visualWheelsParent, "WheelMesh_FR", new Vector3( wheelX, wheelY,  wheelFrontZ), wheelRadius);
        Transform meshRL = CreateWheelMesh(visualWheelsParent, "WheelMesh_RL", new Vector3(-wheelX, wheelY,  wheelRearZ),  wheelRadius);
        Transform meshRR = CreateWheelMesh(visualWheelsParent, "WheelMesh_RR", new Vector3( wheelX, wheelY,  wheelRearZ),  wheelRadius);

        // ── VehicleController ──
        VehicleController vc = car.AddComponent<VehicleController>();
        vc.wheelFL    = wcFL;   vc.wheelFR    = wcFR;
        vc.wheelRL    = wcRL;   vc.wheelRR    = wcRR;
        vc.wheelMeshFL = meshFL; vc.wheelMeshFR = meshFR;
        vc.wheelMeshRL = meshRL; vc.wheelMeshRR = meshRR;

        // ── ManualDriveInput ──
        ManualDriveInput manual = car.AddComponent<ManualDriveInput>();
        manual.vehicle = vc;

        // ── SimulationRunner ──
        SimulationRunner runner = car.AddComponent<SimulationRunner>();
        runner.vehicle   = vc;
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
        bridge.broker  = broker;

        // ── RemoteDriveInput ──
        RemoteDriveInput remoteInput = car.AddComponent<RemoteDriveInput>();
        remoteInput.vehicle      = vc;
        remoteInput.manualInput  = manual;
        remoteInput.enabled      = false;
        broker.remoteInput       = remoteInput;
        manual.remoteInput       = remoteInput;

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
        obstacleCourse.broker  = broker;

        // ── TrainingBridge ──
        TrainingBridge trainingBridge = car.AddComponent<TrainingBridge>();
        trainingBridge.broker          = broker;
        trainingBridge.vehicle         = vc;
        trainingBridge.raycastSensor   = raycastSensor;
        trainingBridge.obstacleCourse  = obstacleCourse;

        // Cross-wire obstacle course back to training bridge
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

        Debug.Log($"[CarFactory] Car built at {position}.");
        return car;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    static void AttachBMWVisual(GameObject car)
    {
        GameObject bmwPrefab = Resources.Load<GameObject>("green_bmw");
        if (bmwPrefab != null)
        {
            GameObject bmwInstance = Object.Instantiate(bmwPrefab, car.transform);
            bmwInstance.name = "BMWVisual";
            bmwInstance.transform.localPosition = new Vector3(-0.2f, -0.5f, 0f);
            bmwInstance.transform.localRotation = Quaternion.Euler(-90f, 90f, 270f);
            bmwInstance.transform.localScale    = new Vector3(0.3f, 0.3f, 0.3f);
        }
        else
        {
            Debug.LogWarning("[CarFactory] bmw.glb not found in Resources — using invisible collision body only. " +
                             "Place it in Assets/Resources/ after installing the glTFast package.");
        }
    }

    static Shader GetURPShader() => Shader.Find("Universal Render Pipeline/Lit");

    static Material MakeMaterial(Color color)
    {
        Shader shader = GetURPShader();
        if (shader == null)
        {
            Debug.LogError("[CarFactory] URP Lit shader not found.");
            return null;
        }
        var mat = new Material(shader);
        mat.SetColor("_BaseColor", color);
        mat.color = color;
        return mat;
    }

    static WheelCollider CreateWheelCollider(GameObject parent, string name, Vector3 localPos, float radius, float suspDist)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent.transform);
        go.transform.localPosition = localPos;

        WheelCollider wc      = go.AddComponent<WheelCollider>();
        wc.radius             = radius;
        wc.suspensionDistance = suspDist;
        wc.mass               = 20f;

        JointSpring spring      = wc.suspensionSpring;
        spring.spring           = 35000f;
        spring.damper           = 5500f;
        spring.targetPosition   = 0.35f;
        wc.suspensionSpring     = spring;

        WheelFrictionCurve fwd = wc.forwardFriction;
        fwd.extremumSlip  = 0.4f;  fwd.extremumValue  = 1f;
        fwd.asymptoteSlip = 0.8f;  fwd.asymptoteValue = 0.5f;
        fwd.stiffness = 1f;
        wc.forwardFriction = fwd;

        WheelFrictionCurve side = wc.sidewaysFriction;
        side.extremumSlip  = 0.25f; side.extremumValue  = 1f;
        side.asymptoteSlip = 0.5f;  side.asymptoteValue = 0.75f;
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
        go.transform.localScale    = new Vector3(diameter, 0.15f, diameter);
        go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);

        DestroyCollider(go.GetComponent<Collider>());
        go.GetComponent<Renderer>().enabled = false; // BMW GLB provides wheel visuals

        return pivot.transform;
    }

    // Handles DestroyImmediate (editor) vs Destroy (runtime) transparently.
    static void DestroyCollider(Collider c)
    {
        if (c == null) return;
#if UNITY_EDITOR
        Object.DestroyImmediate(c);
#else
        Object.Destroy(c);
#endif
    }
}
