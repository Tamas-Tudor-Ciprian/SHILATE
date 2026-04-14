using UnityEngine;

/// <summary>
/// Copies live telemetry from VehicleController into LedaBroker each frame.
/// Keeps vehicle physics and MQTT data export decoupled.
/// </summary>
public class VehicleTelemetryBridge : MonoBehaviour
{
    [Tooltip("The vehicle whose telemetry to read")]
    public VehicleController vehicle;

    [Tooltip("The LedaBroker that publishes signals to MQTT")]
    public LedaBroker broker;

    void Update()
    {
        if (vehicle == null || broker == null) return;

        broker.Speed = vehicle.CurrentSpeed;
        broker.SignedSpeed = vehicle.SignedSpeed;
        broker.RPM = vehicle.CurrentRPM;
        broker.SteeringAngle = vehicle.CurrentSteerAngle;
        broker.BrakePedal = vehicle.BrakeInput;
        broker.ThrottlePosition = vehicle.ThrottleInput;
        broker.Gear = vehicle.CurrentGear;
        broker.FrontSlip = vehicle.FrontSlip;
        broker.RearSlip = vehicle.RearSlip;
        broker.RearForwardSlip = vehicle.RearForwardSlip;
    }
}
