# SHILATE — Stage 1.5 Progress Report

**Date:** March 16, 2026
**Milestone:** Vehicle Simulation & Telemetry Pipeline (Stage 1.5)

---

## Overview

Introduced a drivable car with semi-realistic WheelCollider physics into the Unity project and wired its live telemetry into the existing `LedaBroker` MQTT bridge. The car supports both **scripted simulation runs** (ScriptableObject-based driving scenarios) and **manual keyboard/gamepad driving**.

```
VehicleController ──telemetry──▶ VehicleTelemetryBridge ──▶ LedaBroker ──MQTT──▶ Mosquitto
```

---

## Files Created

| File | Type | Description |
|------|------|-------------|
| `Assets/scripts/VehicleController.cs` | **New** | Core vehicle physics — WheelCollider-based RWD drivetrain, suspension, friction, visual wheel sync, telemetry output |
| `Assets/scripts/VehicleTelemetryBridge.cs` | **New** | Copies live telemetry (speed, RPM, steering, brake, throttle) from VehicleController → LedaBroker each frame |
| `Assets/scripts/DrivingScenario.cs` | **New** | ScriptableObject defining a sequence of timed driving commands (throttle, steer, brake × duration) |
| `Assets/scripts/SimulationRunner.cs` | **New** | Plays back a DrivingScenario by sequentially applying commands to VehicleController |
| `Assets/scripts/ManualDriveInput.cs` | **New** | Maps Input System "Player/Move" action (WASD/gamepad) to VehicleController inputs |
| `Assets/scripts/CameraFollow.cs` | **New** | Smooth third-person follow camera for the vehicle |
| `Assets/scripts/Editor/CarBuilder.cs` | **New** | Editor utility — menu item `SHILATE → Build Car Scene` creates the full car hierarchy + ground plane with one click |

## Files Modified

| File | Change |
|------|--------|
| `ProjectSettings/DynamicsManager.asset` | Solver iterations 6→12, velocity iterations 1→4 for WheelCollider stability |
| `docs/STAGE1_PROGRESS.md` | Marked "connect simulation inputs" item as done |

---

## Architecture

### VehicleController

MonoBehaviour on the car root GameObject. Uses Unity's built-in `WheelCollider` system (from `com.unity.modules.vehicles`).

**Drivetrain:** RWD — motor torque applied to rear wheels (`wheelRL`, `wheelRR`), steering on front wheels (`wheelFL`, `wheelFR`).

**Inputs** (set externally by `SimulationRunner` or `ManualDriveInput`):
- `ThrottleInput` (0–1)
- `SteerInput` (-1 to 1)
- `BrakeInput` (0–1)

**Derived telemetry** (read-only):
- `CurrentSpeed` — km/h from `Rigidbody.linearVelocity.magnitude × 3.6`
- `CurrentRPM` — engine RPM estimated from average rear wheel RPM × `finalDriveRatio`, clamped to idle/max
- `CurrentSteerAngle` — actual front wheel angle in degrees

**WheelCollider settings:**
- Radius: 0.35m
- Suspension: spring 35000, damper 4500, distance 0.2m
- Forward friction: extremum slip 0.4, value 1.0; asymptote slip 0.8, value 0.5
- Sideways friction: extremum slip 0.25, value 1.0; asymptote slip 0.5, value 0.75

### VehicleTelemetryBridge

Simple bridge that runs in `Update()` and copies:
- `VehicleController.CurrentSpeed` → `LedaBroker.Speed`
- `VehicleController.CurrentRPM` → `LedaBroker.RPM`
- `VehicleController.CurrentSteerAngle` → `LedaBroker.SteeringAngle`
- `VehicleController.BrakeInput` → `LedaBroker.BrakePedal`
- `VehicleController.ThrottleInput` → `LedaBroker.ThrottlePosition`

LedaBroker then publishes these via MQTT at its configured interval (default 10 Hz).

### DrivingScenario + SimulationRunner

`DrivingScenario` is a ScriptableObject (create via `Assets → Create → SHILATE → Driving Scenario`). It holds an array of `DriveCommand` structs:

```csharp
[System.Serializable]
public struct DriveCommand
{
    public float duration;    // seconds
    public float throttle;    // 0–1
    public float steer;       // -1 to 1
    public float brake;       // 0–1
}
```

`SimulationRunner` plays back the scenario in `FixedUpdate`, advancing through commands sequentially. Supports looping.

### ManualDriveInput

Uses the existing `InputSystem_Actions` → `Player/Move` action (Vector2 from WASD/gamepad). Move.y → throttle (positive) or brake (negative), Move.x → steering. Automatically suppressed while a `SimulationRunner` scenario is active.

### CarBuilder (Editor)

Menu item: **SHILATE → Build Car Scene**. Creates:
1. A 500×500 ground plane with tarmac physics material
2. A primitive-placeholder car (two boxes + four cylinder wheels)
3. Four WheelColliders with tuned suspension and friction
4. All scripts wired up: VehicleController, ManualDriveInput, SimulationRunner, VehicleTelemetryBridge
5. CameraFollow on the Main Camera pointing at the car
6. Connects to existing LedaBroker (or creates one)

---

## How to Use

### Quick Start (Primitive Car)

1. Open `SampleScene` in Unity Editor
2. Menu: **SHILATE → Build Car Scene**
3. Press Play → drive with **WASD** keys or gamepad left stick

### Scripted Simulation

1. Right-click in Project: **Create → SHILATE → Driving Scenario**
2. Add commands in the Inspector (e.g., throttle 0.8 for 3s, steer 0.3 for 2s, brake 1.0 for 1s)
3. Assign the scenario to `SimulationRunner.scenario` on the Car
4. Enable `autoStart` on SimulationRunner
5. Press Play → car follows the scripted sequence

### Swapping in a Real Car Model

1. Import `.fbx`/`.glTF` into `Assets/Models/`
2. Replace the primitive Body/Cabin with the imported mesh
3. Replace the cylinder WheelMeshes with the model's wheel meshes
4. Adjust `WheelCollider` radius to match the new wheels
5. Re-assign the wheel mesh Transforms in VehicleController Inspector

---

## Next Steps

- [ ] Configure **Kuksa Feeder** on Leda to consume MQTT topics and map to VSS paths
- [ ] Verify end-to-end: `kuksa-client → getValue Vehicle.Speed` returns live sim values
- [ ] Import a proper car 3D model to replace primitive placeholder
- [ ] Create sample DrivingScenario assets for common test patterns (acceleration test, lane change, emergency brake)
- [ ] Add on-screen HUD showing live speed/RPM/steering values

## Future Stages

- **Stage 2:** Bidirectional communication — Leda sends actuator commands back to Unity via `leda/command/#`
- **Stage 3:** Raspberry Pi ECU integration with CAN/LIN signal bridging
- **Stage 4:** Full closed-loop SIL/HIL testing with multiple vehicle signals
