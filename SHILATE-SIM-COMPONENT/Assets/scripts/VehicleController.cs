using UnityEngine;

/// <summary>
/// Tesla Model 3–style electric vehicle physics using Unity WheelColliders.
/// Single-speed RWD drivetrain with P/R/N/D gear selector, regenerative braking,
/// and creep mode. Inputs are set externally by SimulationRunner or ManualDriveInput.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class VehicleController : MonoBehaviour
{
    // ─── Gear State ───

    public enum GearState { Park, Reverse, Neutral, Drive }

    [Header("Wheel Colliders")]
    public WheelCollider wheelFL;
    public WheelCollider wheelFR;
    public WheelCollider wheelRL;
    public WheelCollider wheelRR;

    [Header("Wheel Meshes (visual)")]
    public Transform wheelMeshFL;
    public Transform wheelMeshFR;
    public Transform wheelMeshRL;
    public Transform wheelMeshRR;

    [Header("Drivetrain (Tesla Model 3 RWD)")]
    [Tooltip("Peak motor torque applied to rear wheels (Nm)")]
    public float maxMotorTorque = 450f;

    [Tooltip("Maximum steering angle for front wheels (degrees)")]
    public float maxSteerAngle = 35f;

    [Tooltip("Maximum brake torque applied to all wheels (Nm)")]
    public float maxBrakeTorque = 3000f;

    [Tooltip("Single-speed reduction gear ratio (Tesla ≈ 9:1)")]
    public float finalDriveRatio = 9.0f;

    [Tooltip("Maximum motor RPM (Tesla permanent-magnet motor)")]
    public float maxMotorRPM = 18000f;

    [Header("Gear Transition Thresholds")]
    [Tooltip("Max speed (km/h) to shift into Park")]
    public float parkSpeedThreshold = 2f;

    [Tooltip("Max speed (km/h) to shift between Drive and Reverse")]
    public float gearShiftSpeedThreshold = 5f;

    [Tooltip("Reverse speed limit (km/h) — Tesla caps at ~25 km/h")]
    public float reverseSpeedLimit = 25f;

    [Header("Regenerative Braking")]
    [Tooltip("Regen brake torque when throttle is released (Nm, ~0.2 g)")]
    public float regenBrakeTorque = 150f;

    [Tooltip("Speed (km/h) below which regen fades to zero to avoid oscillation")]
    public float regenFadeSpeed = 5f;

    [Header("Creep")]
    [Tooltip("Enable Tesla-style creep in Drive/Reverse")]
    public bool creepEnabled = true;

    [Tooltip("Torque applied during creep (Nm)")]
    public float creepTorque = 30f;

    [Tooltip("Creep only active below this speed (km/h)")]
    public float creepMaxSpeed = 10f;

    [Header("Park Brake")]
    [Tooltip("Brake torque applied in Park to lock wheels (Nm)")]
    public float parkBrakeTorque = 10000f;

    [Header("Center of Mass")]
    [Tooltip("Local center of mass position for stability (lower = more stable)")]
    public Vector3 centerOfMassOffset = new Vector3(0f, -0.2f, 0.15f);

    // ─── Inputs (written by input scripts or SimulationRunner) ───

    /// <summary>Throttle input: 0 (none) to 1 (full throttle).</summary>
    [HideInInspector] public float ThrottleInput;

    /// <summary>Steering input: -1 (full left) to 1 (full right).</summary>
    [HideInInspector] public float SteerInput;

    /// <summary>Brake input: 0 (none) to 1 (full brake).</summary>
    [HideInInspector] public float BrakeInput;

    // ─── Read-only telemetry ───

    /// <summary>Current speed magnitude in km/h.</summary>
    public float CurrentSpeed { get; private set; }

    /// <summary>Signed speed in km/h (positive = forward, negative = backward).</summary>
    public float SignedSpeed { get; private set; }

    /// <summary>Motor RPM (0 when stationary — EV has no idle).</summary>
    public float CurrentRPM { get; private set; }

    /// <summary>Current front wheel steering angle in degrees.</summary>
    public float CurrentSteerAngle { get; private set; }

    /// <summary>Active gear (Park / Reverse / Neutral / Drive).</summary>
    public GearState CurrentGear { get; private set; } = GearState.Park;

    Rigidbody _rb;

    void Start()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.centerOfMass = centerOfMassOffset;
    }

    // ─── Gear Shift API ───

    /// <summary>
    /// Request a gear change. Returns true if the transition was accepted.
    /// Transition rules enforce Tesla-realistic constraints.
    /// </summary>
    public bool RequestGearChange(GearState requested)
    {
        if (requested == CurrentGear) return true;

        float absSpeed = Mathf.Abs(SignedSpeed);

        switch (requested)
        {
            case GearState.Park:
                if (absSpeed > parkSpeedThreshold)
                {
                    Debug.LogWarning($"[Vehicle] Cannot shift to Park at {absSpeed:F1} km/h (limit {parkSpeedThreshold} km/h).");
                    return false;
                }
                break;

            case GearState.Drive:
                if (CurrentGear == GearState.Park && BrakeInput < 0.1f)
                {
                    Debug.LogWarning("[Vehicle] Hold brake to shift out of Park.");
                    return false;
                }
                if (CurrentGear == GearState.Reverse)
                {
                    if (absSpeed > gearShiftSpeedThreshold)
                    {
                        Debug.LogWarning($"[Vehicle] Cannot shift R→D at {absSpeed:F1} km/h (limit {gearShiftSpeedThreshold} km/h).");
                        return false;
                    }
                    if (BrakeInput < 0.1f)
                    {
                        Debug.LogWarning("[Vehicle] Hold brake to shift from Reverse to Drive.");
                        return false;
                    }
                }
                break;

            case GearState.Reverse:
                if (CurrentGear == GearState.Park && BrakeInput < 0.1f)
                {
                    Debug.LogWarning("[Vehicle] Hold brake to shift out of Park.");
                    return false;
                }
                if (CurrentGear == GearState.Drive)
                {
                    if (absSpeed > gearShiftSpeedThreshold)
                    {
                        Debug.LogWarning($"[Vehicle] Cannot shift D→R at {absSpeed:F1} km/h (limit {gearShiftSpeedThreshold} km/h).");
                        return false;
                    }
                    if (BrakeInput < 0.1f)
                    {
                        Debug.LogWarning("[Vehicle] Hold brake to shift from Drive to Reverse.");
                        return false;
                    }
                }
                break;

            case GearState.Neutral:
                if (CurrentGear == GearState.Park && BrakeInput < 0.1f)
                {
                    Debug.LogWarning("[Vehicle] Hold brake to shift out of Park.");
                    return false;
                }
                break;
        }

        Debug.Log($"[Vehicle] Gear: {CurrentGear} → {requested}");
        CurrentGear = requested;
        return true;
    }

    // ─── Physics ───

    void FixedUpdate()
    {
        ApplySteering();
        ApplyMotor();
        ApplyBrakes();
        UpdateTelemetry();
    }

    void LateUpdate()
    {
        SyncWheelMeshes();
    }

    void ApplySteering()
    {
        CurrentSteerAngle = SteerInput * maxSteerAngle;
        wheelFL.steerAngle = CurrentSteerAngle;
        wheelFR.steerAngle = CurrentSteerAngle;
    }

    void ApplyMotor()
    {
        float absSpeed = Mathf.Abs(SignedSpeed);

        switch (CurrentGear)
        {
            case GearState.Park:
                // No motor torque in park — parking brake handled in ApplyBrakes
                wheelRL.motorTorque = 0f;
                wheelRR.motorTorque = 0f;
                return;

            case GearState.Neutral:
                // Free-rolling: no motor torque, no regen
                wheelRL.motorTorque = 0f;
                wheelRR.motorTorque = 0f;
                return;

            case GearState.Drive:
                ApplyDriveTorque(absSpeed, direction: 1f);
                return;

            case GearState.Reverse:
                ApplyDriveTorque(absSpeed, direction: -1f);
                return;
        }
    }

    void ApplyDriveTorque(float absSpeed, float direction)
    {
        float torque = 0f;

        if (ThrottleInput > 0.01f)
        {
            // Enforce reverse speed limit
            if (direction < 0f && absSpeed >= reverseSpeedLimit)
            {
                torque = 0f;
            }
            else
            {
                torque = ThrottleInput * maxMotorTorque * direction;
            }
        }
        else
        {
            // No throttle — apply regen braking or creep
            if (absSpeed > regenFadeSpeed)
            {
                // Regenerative braking: oppose wheel rotation
                float regenDirection = (SignedSpeed > 0f) ? -1f : 1f;
                torque = regenBrakeTorque * regenDirection;
            }
            else if (absSpeed > 0.5f && absSpeed <= regenFadeSpeed)
            {
                // Fade regen linearly to zero to avoid oscillation
                float fade = absSpeed / regenFadeSpeed;
                float regenDirection = (SignedSpeed > 0f) ? -1f : 1f;
                torque = regenBrakeTorque * regenDirection * fade;
            }

            // Creep mode: apply small torque in gear direction at low speed
            if (creepEnabled && BrakeInput < 0.01f && absSpeed < creepMaxSpeed)
            {
                float creepFade = 1f - (absSpeed / creepMaxSpeed);
                torque += creepTorque * direction * creepFade;
            }
        }

        wheelRL.motorTorque = torque;
        wheelRR.motorTorque = torque;
    }

    void ApplyBrakes()
    {
        float brakeTorque;

        if (CurrentGear == GearState.Park)
        {
            // Park brake: lock all wheels
            brakeTorque = parkBrakeTorque;
        }
        else
        {
            brakeTorque = BrakeInput * maxBrakeTorque;
        }

        wheelFL.brakeTorque = brakeTorque;
        wheelFR.brakeTorque = brakeTorque;
        wheelRL.brakeTorque = brakeTorque;
        wheelRR.brakeTorque = brakeTorque;
    }

    void UpdateTelemetry()
    {
        // Signed speed: project velocity onto the car's forward axis
        float forwardSpeed = Vector3.Dot(_rb.linearVelocity, transform.forward);
        SignedSpeed = forwardSpeed * 3.6f; // m/s → km/h
        CurrentSpeed = Mathf.Abs(SignedSpeed);

        // Motor RPM from average rear wheel angular velocity (EV: 0 at rest)
        float avgWheelRPM = (Mathf.Abs(wheelRL.rpm) + Mathf.Abs(wheelRR.rpm)) * 0.5f;
        float motorRPM = avgWheelRPM * finalDriveRatio;
        CurrentRPM = Mathf.Clamp(motorRPM, 0f, maxMotorRPM);
    }

    void SyncWheelMeshes()
    {
        SyncWheel(wheelFL, wheelMeshFL);
        SyncWheel(wheelFR, wheelMeshFR);
        SyncWheel(wheelRL, wheelMeshRL);
        SyncWheel(wheelRR, wheelMeshRR);
    }

    void SyncWheel(WheelCollider collider, Transform mesh)
    {
        if (collider == null || mesh == null) return;
        collider.GetWorldPose(out Vector3 pos, out Quaternion rot);
        mesh.position = pos;
        mesh.rotation = rot;
    }

    /// <summary>Reset all inputs to zero and shift to Park.</summary>
    public void ResetInputs()
    {
        ThrottleInput = 0f;
        SteerInput = 0f;
        BrakeInput = 0f;
        CurrentGear = GearState.Park;
    }
}
