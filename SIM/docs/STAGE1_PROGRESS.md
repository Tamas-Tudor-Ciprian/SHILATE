# SHILATE — Stage 1 Progress Report

**Date:** March 9, 2026
**Milestone:** Unity → MQTT → Leda round-trip (Stage 1)

---

## Overview

Implemented the MQTT bridge between the Unity automotive simulation and an Eclipse Leda instance running on WSL. Unity now publishes vehicle telemetry signals to a Mosquitto MQTT broker, which is the first leg of the full data pipeline:

```
Unity (Windows) ──MQTT──▶ Mosquitto (WSL/Leda) ──▶ Kuksa Feeder ──▶ Kuksa Databroker
```

---

## Files Created / Modified

| File | Type | Description |
|------|------|-------------|
| `Assets/scripts/MqttClient.cs` | **New** | Zero-dependency MQTT 3.1.1 client (TCP sockets, background read thread, main-thread callback dispatch) |
| `Assets/scripts/LedaBroker.cs` | **Modified** | MonoBehaviour bridge — connects to Mosquitto and periodically publishes vehicle signals |

---

## What Was Implemented

### 1. MqttClient — Minimal MQTT 3.1.1 Client

A self-contained MQTT client written from scratch with no external dependencies, targeting .NET Standard 2.1 (Unity 6 compatible).

**Capabilities:**
- CONNECT with clean session and configurable keep-alive
- PUBLISH at QoS 0 (fire-and-forget)
- SUBSCRIBE to topic filters
- PINGREQ/PINGRESP keep-alive via background timer
- DISCONNECT with graceful packet send
- Thread-safe write operations (lock-guarded)
- Main-thread callback dispatching via `ConcurrentQueue<Action>` (Unity-safe)
- Background read thread for incoming packets

**Design decisions:**
- Not a MonoBehaviour — instantiated and managed by `LedaBroker` internally
- No external NuGet/UPM dependencies required
- Handles connection failures gracefully without crashing Unity

### 2. LedaBroker — Unity-to-Leda Bridge Component

A MonoBehaviour that acts as the bridge between the Unity simulation and the Leda MQTT ecosystem.

**Inspector-configurable fields:**
- `brokerHost` — MQTT broker address (default: `localhost`, works with WSL port forwarding)
- `brokerPort` — MQTT broker port (default: `1883`)
- `clientId` — MQTT client identifier (default: `unity-shilate`)
- `publishInterval` — Seconds between publish cycles (default: `0.1s` = 10 Hz)

**Published vehicle signals:**

| Public Field | MQTT Topic | Payload Format | VSS Path |
|---|---|---|---|
| `Speed` | `vehicle/speed` | `{"value":60.00}` | `Vehicle.Speed` |
| `RPM` | `vehicle/rpm` | `{"value":3000.00}` | `Vehicle.Powertrain.CombustionEngine.Speed` |
| `SteeringAngle` | `vehicle/steering` | `{"value":15.50}` | `Vehicle.Chassis.SteeringWheel.Angle` |
| `BrakePedal` | `vehicle/brake` | `{"value":0.80}` | `Vehicle.Chassis.Brake.PedalPosition` |
| `ThrottlePosition` | `vehicle/throttle` | `{"value":0.45}` | `Vehicle.Powertrain.CombustionEngine.Throttle` |

**Public API for other scripts:**
- `SetSpeed(float)` — Set speed and immediately publish
- `SetRPM(float)` — Set RPM and immediately publish
- `PublishSignal(string topic, float value)` — Publish any arbitrary signal

**Lifecycle:**
- Connects on `OnEnable()`, disconnects on `OnDisable()`
- Subscribes to `leda/command/#` for future bidirectional communication
- Logs connection status to Unity Console

---

## Bugs Fixed

### String Format Bug (March 9, 2026)

**Problem:** MQTT payloads were being published as `{"value":F2}` instead of actual numeric values.

**Cause:** The C# interpolated string `$"{{\"value\":{value:F2}}}"` had an ambiguous triple-brace `}}}` that was parsed incorrectly — the `F2` format specifier was treated as a literal string.

**Fix:** Replaced the interpolated string with explicit concatenation:
```csharp
// Before (broken):
string json = $"{{\"value\":{value:F2}}}";

// After (fixed):
string json = "{\"value\":" + value.ToString("F2", CultureInfo.InvariantCulture) + "}";
```

Using `InvariantCulture` also ensures the decimal separator is always `.` regardless of the system locale.

---

## Verification (Completed)

1. **Attached** `LedaBroker` component to a GameObject in the Unity scene.
2. **Started** Mosquitto broker on the WSL Leda instance.
3. **Pressed Play** in Unity — Console showed `[LedaBroker] Connected to MQTT broker at localhost:1883`.
4. **Verified** messages arriving on Leda:
   ```bash
   mosquitto_sub -t "vehicle/#" -v
   ```
   Output confirmed properly formatted payloads:
   ```
   vehicle/speed {"value":0.00}
   vehicle/rpm {"value":0.00}
   vehicle/steering {"value":0.00}
   vehicle/brake {"value":0.00}
   vehicle/throttle {"value":0.00}
   ```

---

## Next Steps (Remaining for Stage 1 Completion)

- [ ] Configure **Kuksa Feeder** on Leda to consume MQTT topics and map them to VSS paths
- [ ] Verify end-to-end round trip: `kuksa-client → getValue Vehicle.Speed` returns value published by Unity
- [x] Connect simulation inputs (e.g., vehicle controller) to `LedaBroker` fields so real telemetry is published → **Done in Stage 1.5** (see `STAGE1_5_PROGRESS.md`)

## Future Stages

- **Stage 2:** Bidirectional communication — Leda sends actuator commands back to Unity via `leda/command/#`
- **Stage 3:** Raspberry Pi ECU integration with CAN/LIN signal bridging
- **Stage 4:** Full closed-loop SIL/HIL testing with multiple vehicle signals
