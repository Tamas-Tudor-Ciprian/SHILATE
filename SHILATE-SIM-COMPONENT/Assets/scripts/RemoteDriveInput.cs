using UnityEngine;

/// <summary>
/// Applies control commands received from Leda via MQTT to VehicleController.
/// LedaBroker sets the public fields from MQTT callbacks (main-thread dispatched).
/// When this component is enabled, ManualDriveInput should be suppressed.
/// </summary>
public class RemoteDriveInput : MonoBehaviour
{
    [Tooltip("The vehicle to control")]
    public VehicleController vehicle;

    [Tooltip("Optional — manual input to suppress while remote control is active")]
    public ManualDriveInput manualInput;

    // ─── Control values set by LedaBroker.HandleMessage() ───

    [HideInInspector] public float Throttle;
    [HideInInspector] public float Steer;
    [HideInInspector] public float Brake;
    [HideInInspector] public VehicleController.GearState RequestedGear;
    [HideInInspector] public bool GearChangeRequested;

    void OnEnable()
    {
        if (manualInput != null)
            manualInput.enabled = false;
    }

    void OnDisable()
    {
        if (manualInput != null)
            manualInput.enabled = true;

        if (vehicle != null)
            vehicle.ResetInputs();
    }

    void Update()
    {
        if (vehicle == null) return;

        vehicle.ThrottleInput = Throttle;
        vehicle.SteerInput = Steer;
        vehicle.BrakeInput = Brake;

        if (GearChangeRequested)
        {
            vehicle.RequestGearChange(RequestedGear);
            GearChangeRequested = false;
        }
    }
}
