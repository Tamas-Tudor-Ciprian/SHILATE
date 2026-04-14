using UnityEngine;

/// <summary>
/// ScriptableObject defining a sequence of timed driving commands.
/// Create via Assets → Create → SHILATE → Driving Scenario.
/// </summary>
[CreateAssetMenu(fileName = "NewScenario", menuName = "SHILATE/Driving Scenario")]
public class DrivingScenario : ScriptableObject
{
    [Tooltip("Ordered list of driving commands executed sequentially")]
    public DriveCommand[] commands;

    [Tooltip("Loop the scenario after the last command")]
    public bool loop;
}

/// <summary>
/// A single timed driving command: hold the given inputs for a duration.
/// </summary>
[System.Serializable]
public struct DriveCommand
{
    [Tooltip("How long to apply these inputs (seconds)")]
    public float duration;

    [Tooltip("Gear selector (defaults to Drive for backward compatibility)")]
    public VehicleController.GearState gear;

    [Range(0f, 1f)]
    [Tooltip("Throttle: 0 = none, 1 = full")]
    public float throttle;

    [Range(-1f, 1f)]
    [Tooltip("Steering: -1 = full left, 1 = full right")]
    public float steer;

    [Range(0f, 1f)]
    [Tooltip("Brake: 0 = none, 1 = full")]
    public float brake;
}
