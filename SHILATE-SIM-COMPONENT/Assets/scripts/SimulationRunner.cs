using UnityEngine;

/// <summary>
/// Plays back a DrivingScenario by sequentially applying each DriveCommand
/// to a VehicleController. Runs automatically on Start or via StartScenario().
/// </summary>
public class SimulationRunner : MonoBehaviour
{
    [Tooltip("The scenario to execute")]
    public DrivingScenario scenario;

    [Tooltip("The vehicle to control")]
    public VehicleController vehicle;

    [Tooltip("Start the scenario automatically on Play")]
    public bool autoStart = true;

    /// <summary>True while a scenario is actively running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Index of the current command being executed.</summary>
    public int CurrentCommandIndex { get; private set; }

    float _commandTimer;
    VehicleController.GearState _lastGear;

    void Start()
    {
        if (autoStart && scenario != null && vehicle != null)
            StartScenario();
    }

    void FixedUpdate()
    {
        if (!IsRunning) return;
        if (scenario == null || scenario.commands == null || scenario.commands.Length == 0) return;

        _commandTimer += Time.fixedDeltaTime;

        DriveCommand cmd = scenario.commands[CurrentCommandIndex];

        // Apply gear change when command's gear differs from last applied
        VehicleController.GearState targetGear = cmd.gear;
        if (targetGear != _lastGear)
        {
            if (vehicle.RequestGearChange(targetGear))
                _lastGear = targetGear;
        }

        // Apply current command inputs
        vehicle.ThrottleInput = cmd.throttle;
        vehicle.SteerInput = cmd.steer;
        vehicle.BrakeInput = cmd.brake;

        // Advance to next command when duration expires
        if (_commandTimer >= cmd.duration)
        {
            _commandTimer = 0f;
            CurrentCommandIndex++;

            if (CurrentCommandIndex >= scenario.commands.Length)
            {
                if (scenario.loop)
                {
                    CurrentCommandIndex = 0;
                    Debug.Log("[SimulationRunner] Scenario looping.");
                }
                else
                {
                    StopScenario();
                }
            }
        }
    }

    /// <summary>Begin executing the assigned scenario from the start.</summary>
    public void StartScenario()
    {
        if (scenario == null || vehicle == null)
        {
            Debug.LogWarning("[SimulationRunner] Cannot start — scenario or vehicle not assigned.");
            return;
        }

        if (scenario.commands == null || scenario.commands.Length == 0)
        {
            Debug.LogWarning("[SimulationRunner] Scenario has no commands.");
            return;
        }

        CurrentCommandIndex = 0;
        _commandTimer = 0f;
        _lastGear = vehicle.CurrentGear;
        IsRunning = true;
        Debug.Log($"[SimulationRunner] Started scenario '{scenario.name}' ({scenario.commands.Length} commands).");
    }

    /// <summary>Stop the scenario and zero out vehicle inputs.</summary>
    public void StopScenario()
    {
        IsRunning = false;
        vehicle?.ResetInputs();
        Debug.Log("[SimulationRunner] Scenario finished.");
    }
}
