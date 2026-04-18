"""
SHILATE Velocitas Vehicle App

A Vehicle App built with the Eclipse Velocitas SDK that consumes live
vehicle signals from the KUKSA Databroker (fed by the MQTT-KUKSA feeder)
and implements safety-monitoring business logic.

Features:
  - Subscribes to Vehicle.Speed, Engine RPM, Steering, Brake, Throttle
  - Detects overspeed (>120 km/h) and RPM redline (>6500)
  - Publishes alerts back to MQTT on leda/command/alert, which the
    Unity simulation already subscribes to via leda/command/#
  - Logs a periodic telemetry summary every 5 seconds
"""

import asyncio
import json
import logging
import os
import signal

from velocitas_sdk.vehicle_app import VehicleApp
from velocitas_sdk.model import DataPointFloat, Node

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
log = logging.getLogger("shilate-vehicle-app")

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
KUKSA_HOST = os.environ.get("KUKSA_HOST", "127.0.0.1")
KUKSA_PORT = int(os.environ.get("KUKSA_PORT", 55555))
MQTT_HOST = os.environ.get("MQTT_HOST", "localhost")
MQTT_PORT = int(os.environ.get("MQTT_PORT", 1883))

SPEED_LIMIT = float(os.environ.get("SPEED_LIMIT", 120.0))
RPM_REDLINE = float(os.environ.get("RPM_REDLINE", 6500.0))

ALERT_TOPIC = "leda/command/alert"


# ---------------------------------------------------------------------------
# VSS Vehicle Model (minimal tree for the signals we monitor)
# ---------------------------------------------------------------------------
class Brake(Node):
    def __init__(self, parent: Node):
        super().__init__(parent)
        self.PedalPosition = DataPointFloat("PedalPosition", self)


class SteeringWheel(Node):
    def __init__(self, parent: Node):
        super().__init__(parent)
        self.Angle = DataPointFloat("Angle", self)


class Chassis(Node):
    def __init__(self, parent: Node):
        super().__init__(parent)
        self.Brake = Brake(self)
        self.SteeringWheel = SteeringWheel(self)


class CombustionEngine(Node):
    def __init__(self, parent: Node):
        super().__init__(parent)
        self.Speed = DataPointFloat("Speed", self)


class Powertrain(Node):
    def __init__(self, parent: Node):
        super().__init__(parent)
        self.CombustionEngine = CombustionEngine(self)


class OBD(Node):
    def __init__(self, parent: Node):
        super().__init__(parent)
        self.ThrottlePosition = DataPointFloat("ThrottlePosition", self)


class Vehicle(Node):
    def __init__(self):
        super().__init__()
        self.name = "Vehicle"
        self.Speed = DataPointFloat("Speed", self)
        self.Chassis = Chassis(self)
        self.Powertrain = Powertrain(self)
        self.OBD = OBD(self)


vehicle = Vehicle()


# ---------------------------------------------------------------------------
# Vehicle App
# ---------------------------------------------------------------------------
class ShilateMonitorApp(VehicleApp):
    """
    Monitors vehicle telemetry from KUKSA Databroker and raises alerts
    when safety thresholds are exceeded.
    """

    def __init__(self):
        super().__init__()
        self._latest: dict[str, float] = {
            "Vehicle.Speed": 0.0,
            "Vehicle.Powertrain.CombustionEngine.Speed": 0.0,
            "Vehicle.Chassis.SteeringWheel.Angle": 0.0,
            "Vehicle.Chassis.Brake.PedalPosition": 0.0,
            "Vehicle.OBD.ThrottlePosition": 0.0,
        }
        self._overspeed_active = False
        self._redline_active = False

    async def on_start(self):
        log.info("═══════════════════════════════════════════════")
        log.info("  SHILATE Vehicle Monitor App  (Velocitas)")
        log.info("═══════════════════════════════════════════════")
        log.info("  KUKSA Databroker: %s:%d", KUKSA_HOST, KUKSA_PORT)
        log.info("  MQTT Broker:      %s:%d", MQTT_HOST, MQTT_PORT)
        log.info("  Speed limit:      %.0f km/h", SPEED_LIMIT)
        log.info("  RPM redline:      %.0f", RPM_REDLINE)

        await vehicle.Speed.subscribe(self._on_speed)
        await vehicle.Powertrain.CombustionEngine.Speed.subscribe(self._on_rpm)
        await vehicle.Chassis.SteeringWheel.Angle.subscribe(self._on_steering)
        await vehicle.Chassis.Brake.PedalPosition.subscribe(self._on_brake)
        await vehicle.OBD.ThrottlePosition.subscribe(self._on_throttle)

        log.info("Subscribed to %d VSS signals", len(self._latest))
        asyncio.ensure_future(self._telemetry_summary_loop())

    # -- Signal callbacks ---------------------------------------------------

    async def _on_speed(self, data):
        value = data.get(vehicle.Speed).value
        self._latest["Vehicle.Speed"] = value
        await self._check_overspeed(value)

    async def _on_rpm(self, data):
        value = data.get(vehicle.Powertrain.CombustionEngine.Speed).value
        self._latest["Vehicle.Powertrain.CombustionEngine.Speed"] = value
        await self._check_redline(value)

    async def _on_steering(self, data):
        self._latest["Vehicle.Chassis.SteeringWheel.Angle"] = data.get(
            vehicle.Chassis.SteeringWheel.Angle
        ).value

    async def _on_brake(self, data):
        self._latest["Vehicle.Chassis.Brake.PedalPosition"] = data.get(
            vehicle.Chassis.Brake.PedalPosition
        ).value

    async def _on_throttle(self, data):
        self._latest["Vehicle.OBD.ThrottlePosition"] = data.get(
            vehicle.OBD.ThrottlePosition
        ).value

    # -- Safety checks ------------------------------------------------------

    async def _check_overspeed(self, speed: float):
        if speed > SPEED_LIMIT and not self._overspeed_active:
            self._overspeed_active = True
            alert = {
                "type": "overspeed",
                "speed": round(speed, 2),
                "limit": SPEED_LIMIT,
                "message": f"Speed {speed:.1f} km/h exceeds limit {SPEED_LIMIT:.0f} km/h",
            }
            log.warning("ALERT: %s", alert["message"])
            await self.publish_event(ALERT_TOPIC, json.dumps(alert))
        elif speed <= SPEED_LIMIT and self._overspeed_active:
            self._overspeed_active = False
            log.info("Speed returned to normal: %.1f km/h", speed)

    async def _check_redline(self, rpm: float):
        if rpm > RPM_REDLINE and not self._redline_active:
            self._redline_active = True
            alert = {
                "type": "redline",
                "rpm": round(rpm, 2),
                "limit": RPM_REDLINE,
                "message": f"RPM {rpm:.0f} exceeds redline {RPM_REDLINE:.0f}",
            }
            log.warning("ALERT: %s", alert["message"])
            await self.publish_event(ALERT_TOPIC, json.dumps(alert))
        elif rpm <= RPM_REDLINE and self._redline_active:
            self._redline_active = False
            log.info("RPM returned to normal: %.0f", rpm)

    # -- Telemetry summary --------------------------------------------------

    async def _telemetry_summary_loop(self):
        while True:
            await asyncio.sleep(5)
            log.info(
                "Telemetry | SPD: %6.1f km/h | RPM: %6.0f | STR: %5.1f° "
                "| BRK: %.2f | THR: %.2f",
                self._latest["Vehicle.Speed"],
                self._latest["Vehicle.Powertrain.CombustionEngine.Speed"],
                self._latest["Vehicle.Chassis.SteeringWheel.Angle"],
                self._latest["Vehicle.Chassis.Brake.PedalPosition"],
                self._latest["Vehicle.OBD.ThrottlePosition"],
            )


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
async def main():
    app = ShilateMonitorApp()

    loop = asyncio.get_event_loop()
    for sig in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(sig, lambda: asyncio.ensure_future(app.stop()))

    await app.run()


if __name__ == "__main__":
    asyncio.run(main())
