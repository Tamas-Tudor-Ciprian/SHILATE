# Stage 2: MQTT→KUKSA Bridge & Velocitas Integration

**Date:** April 2026
**Status:** Implementation complete, pending deployment verification

---

## Overview

Stage 2 bridges the gap between the Unity simulation's MQTT telemetry output
(Stage 1.5) and the Eclipse KUKSA.val Databroker, then introduces the first
Velocitas vehicle app that consumes those signals.

## Components Added

### 1. MQTT→KUKSA Feeder (`leda/mqtt-kuksa-feeder/`)

A Python service that subscribes to the `vehicle/#` MQTT topics published by
Unity's `LedaBroker` and writes the values into the KUKSA Databroker via gRPC.

**Signal mapping:**

| MQTT Topic | VSS Path | Unit |
|------------|----------|------|
| `vehicle/speed` | `Vehicle.Speed` | km/h |
| `vehicle/rpm` | `Vehicle.Powertrain.CombustionEngine.Speed` | rpm |
| `vehicle/steering` | `Vehicle.Chassis.SteeringWheel.Angle` | degrees |
| `vehicle/brake` | `Vehicle.Chassis.Brake.PedalPosition` | ratio 0-1 |
| `vehicle/throttle` | `Vehicle.Powertrain.CombustionEngine.Throttle` | ratio 0-1 |

**Key features:**
- Externalized config (`config.json`) — add new signal mappings without code changes
- Environment variable overrides for MQTT/KUKSA host and port
- Auto-reconnect for both MQTT and KUKSA connections
- Graceful shutdown on SIGTERM (container lifecycle)
- Deployed as Kanto container with host networking

### 2. Velocitas Vehicle App (`leda/velocitas-app/`)

A Python vehicle app using the Eclipse Velocitas SDK that subscribes to VSS
signals from the KUKSA Databroker and implements safety-monitoring logic.

**Business logic:**
- **Overspeed detection** — alerts when `Vehicle.Speed` exceeds 120 km/h
  (configurable via `SPEED_LIMIT` env var)
- **RPM redline detection** — alerts when engine RPM exceeds 6500
  (configurable via `RPM_REDLINE` env var)
- **Telemetry summary** — logs all 5 signals every 5 seconds
- **Bidirectional alerts** — publishes JSON alerts to `leda/command/alert`
  which Unity receives via the existing `leda/command/#` subscription

**Alert payload format:**
```json
{
  "type": "overspeed",
  "speed": 125.50,
  "limit": 120.0,
  "message": "Speed 125.5 km/h exceeds limit 120 km/h"
}
```

### 3. Deploy Script (`leda/deploy.sh`)

One-command build-and-deploy for both containers:
1. Builds Docker images locally
2. Exports as tarballs
3. SCPs to Leda VM over SSH (port 2222)
4. Imports into containerd (kanto-cm namespace)
5. Creates and starts Kanto containers with host networking

## Data Flow (End-to-End)

```
Unity VehicleController
  ↓ VehicleTelemetryBridge (per frame)
LedaBroker → MQTT Publish (QoS 0, 10 Hz)
  ↓ TCP localhost:1883
Mosquitto MQTT Broker (inside Leda)
  ↓ vehicle/# subscription
mqtt-kuksa-feeder → KUKSA Databroker (gRPC :55555)
  ↓ VSS signal subscriptions
shilate-velocitas-app → Monitors thresholds
  ↓ leda/command/alert
Mosquitto → Unity (via LedaBroker leda/command/# subscription)
```

## Deployment

```bash
# From leda/ directory
./deploy.sh all       # Deploy both
./deploy.sh feeder    # Deploy feeder only
./deploy.sh app       # Deploy Velocitas app only
```

## Verification Steps

1. **MQTT messages arriving:**
   ```bash
   ssh -p 2222 root@localhost "mosquitto_sub -t 'vehicle/#' -v -C 5"
   ```

2. **Feeder connected:**
   ```bash
   ssh -p 2222 root@localhost "kanto-cm logs --name mqtt-kuksa-feeder"
   # Should show: "Connected to MQTT broker" and "Connected to KUKSA Databroker"
   ```

3. **KUKSA has live values:**
   ```bash
   ssh -p 2222 root@localhost "databroker-cli"
   # Then: get Vehicle.Speed
   ```

4. **Velocitas app processing:**
   ```bash
   ssh -p 2222 root@localhost "kanto-cm logs --name shilate-velocitas-app"
   # Should show telemetry summaries every 5 seconds
   ```

5. **Bidirectional alert received in Unity:**
   Drive above 120 km/h in the sim → check Unity console for `leda/command/alert` message

## Files Created

```
leda/
├── deploy.sh                          # Build & deploy automation
├── mqtt-kuksa-feeder/
│   ├── feeder.py                      # MQTT subscriber → KUKSA writer
│   ├── config.json                    # Topic-to-VSS mapping
│   ├── requirements.txt               # paho-mqtt, kuksa-client
│   ├── Dockerfile                     # Python 3.11-slim container
│   └── kanto-manifest.json            # Kanto deployment descriptor
└── velocitas-app/
    ├── app.py                         # Velocitas vehicle app
    ├── requirements.txt               # sdv, kuksa-client, paho-mqtt
    ├── Dockerfile                     # Python 3.11-slim container
    └── kanto-manifest.json            # Kanto deployment descriptor
```

## Next Steps (Stage 3)

- [ ] Parse `leda/command/alert` in Unity to trigger visual/audio feedback
- [ ] Add more Velocitas app logic (e.g., eco-driving score, trip analytics)
- [ ] Integrate Raspberry Pi ECU for CAN/LIN signal bridging
- [ ] Add authentication to MQTT and KUKSA for production hardening
