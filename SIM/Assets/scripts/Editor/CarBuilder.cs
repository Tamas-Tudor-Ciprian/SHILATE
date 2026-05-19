using UnityEngine;
using UnityEditor;

/// <summary>
/// Editor utility to build a complete primitive-placeholder car with WheelColliders,
/// a ground plane, and all required scripts wired up.
/// Access via menu: SHILATE → Build Car Scene.
/// </summary>
public static class CarBuilder
{
    [MenuItem("SHILATE/Build Car Scene")]
    static void BuildCarScene()
    {
        // ── Ground Plane ──
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(50f, 1f, 50f);

        // Tarmac-like physics material
        PhysicsMaterial groundMat = new PhysicsMaterial("Tarmac")
        {
            staticFriction = 0.8f,
            dynamicFriction = 0.6f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Average,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
        ground.GetComponent<Collider>().material = groundMat;

        // ── Car Root ──
        // Spawn at Y=0.5 so WheelCollider rays reach ground immediately
        // (ray length = suspensionDistance + radius = 0.55, from Y=0.5 reaches Y=-0.05)
        // This avoids the free-fall → spring explosion on first contact.
        GameObject car = new GameObject("Car");
        car.transform.position = new Vector3(32.5f, 0.5f, 0f); // on track centerline (inner=25 + outer=40) / 2
        Rigidbody rb = car.AddComponent<Rigidbody>();    
        rb.mass = 1500f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.inertiaTensor = new Vector3(1000,1000,1000); //this should instruct the wheel coliders to atach to a shape an not explode
        rb.centerOfMass = new Vector3(0,-0.2f, 0); //low center of mass to prevent flipping

        // ── Car Body (visual) ──
        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(car.transform);
        body.transform.localPosition = new Vector3(0f, 0.35f, 0f);
        body.transform.localScale = new Vector3(1.8f, 0.6f, 4.2f);
        body.GetComponent<Renderer>().material = new Material(Shader.Find("Universal Render Pipeline/Lit")){color = Color.green};
       // Object.DestroyImmediate(body.GetComponent<Collider>()); // physics handled by WheelColliders

        //here I will create a parent for the visual wheels

        GameObject visualWheelsParent = new GameObject("VisualWheels");
        visualWheelsParent.transform.SetParent(car.transform);
        visualWheelsParent.transform.localPosition = Vector3.zero;

        //here I will create a parent for the physics wheels

        GameObject physicsWheelsParent = new GameObject("PhysicsWheels");
        physicsWheelsParent.transform.SetParent(car.transform);
        physicsWheelsParent.transform.localPosition = Vector3.zero;

        // Cabin (upper box) 
             
        GameObject cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cabin.name = "Cabin";
        cabin.transform.SetParent(car.transform);
        cabin.transform.localPosition = new Vector3(0f, 0.85f, -0.2f);
        cabin.transform.localScale = new Vector3(1.5f, 0.5f, 2.0f);
        Object.DestroyImmediate(cabin.GetComponent<Collider>());
        cabin.GetComponent<Renderer>().material = new Material(Shader.Find("Universal Render Pipeline/Lit")){color = new Color(0.2f, 0.8f, 0.1f)}; //this should color the wheel
        
        // ── Wheel dimensions ──
        float wheelRadius = 0.35f;
        float suspensionDistance = 0.2f;
        float wheelX = 1.0f;
        float wheelFrontZ = 1.3f;
        float wheelRearZ = -1.3f;
        float wheelY = 0f; // relative to car root (WheelCollider handles suspension)

        // ── Create WheelColliders + Visual Wheels ──
        WheelCollider wcFL = CreateWheelCollider(physicsWheelsParent, "WheelCollider_FL", new Vector3(-wheelX, wheelY, wheelFrontZ), wheelRadius, suspensionDistance);
        WheelCollider wcFR = CreateWheelCollider(physicsWheelsParent, "WheelCollider_FR", new Vector3(wheelX, wheelY, wheelFrontZ), wheelRadius, suspensionDistance);
        WheelCollider wcRL = CreateWheelCollider(physicsWheelsParent, "WheelCollider_RL", new Vector3(-wheelX, wheelY, wheelRearZ), wheelRadius, suspensionDistance);
        WheelCollider wcRR = CreateWheelCollider(physicsWheelsParent, "WheelCollider_RR", new Vector3(wheelX, wheelY, wheelRearZ), wheelRadius, suspensionDistance);

        

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
        runner.autoStart = false; // manual by default until a scenario is assigned
        manual.simulationRunner = runner;

        // ── Find or create LedaBroker and wire VehicleTelemetryBridge ──
        LedaBroker broker = Object.FindAnyObjectByType<LedaBroker>();
        if (broker == null)
        {
            GameObject brokerGO = new GameObject("LedaBroker");
            broker = brokerGO.AddComponent<LedaBroker>();
        }

        VehicleTelemetryBridge bridge = car.AddComponent<VehicleTelemetryBridge>();
        bridge.vehicle = vc;
        bridge.broker = broker;

        // ── RemoteDriveInput (Leda → Unity control) ──
        RemoteDriveInput remoteInput = car.AddComponent<RemoteDriveInput>();
        remoteInput.vehicle = vc;
        remoteInput.manualInput = manual;
        remoteInput.enabled = false; // disabled by default; TrainingBootstrap or Inspector enables it
        broker.remoteInput = remoteInput;
        manual.remoteInput = remoteInput;   // so enabling ManualDriveInput disables RemoteDriveInput

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

        // ── Camera ──
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            CameraFollow follow = mainCam.gameObject.AddComponent<CameraFollow>();
            follow.target = car.transform;

            // Place camera behind the car immediately so there's no lerp from origin
            mainCam.transform.position = car.transform.TransformPoint(follow.offset);
            mainCam.transform.LookAt(car.transform);
        }

        // ── Select the car so user sees it ──
        Selection.activeGameObject = car;
        Debug.Log("[CarBuilder] Car scene built with full Leda integration. " +
            "Manual drive (WASD) active by default. " +
            "Enable RemoteDriveInput for Leda control, or use TrainingBootstrap for headless mode.");
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

        // Semi-realistic friction curves
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
        // Cylinder default height=2, radius=0.5 → scale to match wheel radius
        float diameter = radius * 2f;
        go.transform.localScale = new Vector3(diameter, 0.15f, diameter);
        go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
        Object.DestroyImmediate(go.GetComponent<Collider>()); // WheelCollider handles physics
        go.GetComponent<Renderer>().material = new Material(Shader.Find("Universal Render Pipeline/Lit")){color = Color.brown}; //this should color the wheel
        return pivot.transform;
    }
}
