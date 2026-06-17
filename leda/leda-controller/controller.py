"""
SHILATE Controller — shared MQTT base class for Leda-side vehicle control.

Single-environment design: all topics use the hardcoded "env0/" prefix to match
the Unity LedaBroker's default. Shared by debug_drive.py, train.py, and
ai_driver.py.
"""

import json
import logging
import threading
import time

import paho.mqtt.client as mqtt

log = logging.getLogger(__name__)


class VehicleController:
    """MQTT-based vehicle controller that sends commands and receives telemetry."""

    # Telemetry topics we subscribe to (relative, without env prefix)
    TELEMETRY_TOPICS = [
        "vehicle/speed",
        "vehicle/signedSpeed",
        "vehicle/rpm",
        "vehicle/steering",
        "vehicle/brake",
        "vehicle/throttle",
        "vehicle/gear",
        "vehicle/slip/front",
        "vehicle/slip/rear",
        "vehicle/slip/rearForward",
    ]

    SENSOR_TOPICS = [
        "vehicle/sensor/rays",
        "vehicle/sensor/collision",
        "vehicle/sensor/position",
    ]

    TRAINING_TOPICS = [
        "vehicle/training/reward",
        "vehicle/training/done",
        "vehicle/training/obs",
        "vehicle/training/episode_end",
        "vehicle/training/heartbeat",
    ]

    def __init__(
        self,
        mqtt_host: str = "localhost",
        mqtt_port: int = 1883,
        subscribe_sensors: bool = False,
        subscribe_training: bool = False,
        env_prefix: str = "env0",
    ):
        self.prefix = env_prefix
        self.mqtt_host = mqtt_host
        self.mqtt_port = mqtt_port
        self._subscribe_sensors = subscribe_sensors
        self._subscribe_training = subscribe_training

        # Latest telemetry values
        self.telemetry: dict[str, float | str] = {}
        self._telemetry_lock = threading.Lock()

        # Sensor data
        self.sensor_rays: list[float] = []
        self.sensor_collision: bool = False
        self.sensor_position: dict = {}

        # Training signals
        self.training_reward: float = 0.0
        self.training_done: bool = False
        self.training_obs: dict = {}
        self.training_obstacle_count: int = 0

        # Events for synchronization
        self.obs_event = threading.Event()
        self.done_event = threading.Event()

        # Python → Unity heartbeat
        self._heartbeat_seq = 0
        self._heartbeat_stop = threading.Event()
        self._heartbeat_thread: threading.Thread | None = None

        # MQTT client
        self._client = mqtt.Client(
            client_id=f"shilate-controller-{env_prefix}",
            protocol=mqtt.MQTTv311,
        )
        self._client.on_connect = self._on_connect
        self._client.on_disconnect = self._on_disconnect
        self._client.on_message = self._on_message
        self._connected = threading.Event()

    def connect(self, timeout: float = 10.0) -> bool:
        """Connect to the MQTT broker. Returns True on success."""
        try:
            self._client.reconnect_delay_set(min_delay=1, max_delay=30)
            self._client.connect(self.mqtt_host, self.mqtt_port, keepalive=30)
            self._client.loop_start()
            if not self._connected.wait(timeout):
                log.error("MQTT connection timeout after %.1fs", timeout)
                return False
            self._heartbeat_stop.clear()
            self._heartbeat_thread = threading.Thread(
                target=self._heartbeat_loop, daemon=True, name="shilate-heartbeat"
            )
            self._heartbeat_thread.start()
            return True
        except Exception as e:
            log.error("MQTT connection failed: %s", e)
            return False

    def disconnect(self):
        """Disconnect from the MQTT broker."""
        self._heartbeat_stop.set()
        if self._heartbeat_thread is not None:
            self._heartbeat_thread.join(timeout=2.0)
            self._heartbeat_thread = None
        self._client.loop_stop()
        self._client.disconnect()

    def _heartbeat_loop(self) -> None:
        """Publish a 1 Hz heartbeat on env0/leda/heartbeat so Unity can detect command loss."""
        while not self._heartbeat_stop.wait(1.0):
            if self._connected.is_set():
                payload = json.dumps({"value": self._heartbeat_seq, "ts": time.time()})
                self._client.publish(f"{self.prefix}/leda/heartbeat", payload)
                self._heartbeat_seq += 1

    def _on_disconnect(self, client, userdata, rc) -> None:
        self._connected.clear()
        if rc != 0:
            log.warning("MQTT unexpectedly disconnected (rc=%d); paho will auto-reconnect", rc)
            print("SHILATE-HEALTH mqtt_lost", flush=True)

    def _on_connect(self, client, userdata, flags, rc):
        if rc != 0:
            log.error("MQTT connect failed with code %d", rc)
            return

        log.info("Connected to MQTT broker at %s:%d (prefix: %s)",
                 self.mqtt_host, self.mqtt_port, self.prefix)

        # Subscribe to telemetry
        for topic in self.TELEMETRY_TOPICS:
            client.subscribe(f"{self.prefix}/{topic}")

        if self._subscribe_sensors:
            for topic in self.SENSOR_TOPICS:
                client.subscribe(f"{self.prefix}/{topic}")

        if self._subscribe_training:
            for topic in self.TRAINING_TOPICS:
                client.subscribe(f"{self.prefix}/{topic}")

        self._connected.set()

    def _on_message(self, client, userdata, msg):
        topic = msg.topic
        try:
            payload = json.loads(msg.payload.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            return

        value = payload.get("value")

        # Strip env prefix
        prefix_str = f"{self.prefix}/"
        local_topic = topic[len(prefix_str):] if topic.startswith(prefix_str) else topic

        # Telemetry
        if local_topic.startswith("vehicle/") and not local_topic.startswith("vehicle/sensor/") \
                and not local_topic.startswith("vehicle/training/"):
            with self._telemetry_lock:
                self.telemetry[local_topic] = value

        # Sensor data
        elif local_topic == "vehicle/sensor/rays":
            self.sensor_rays = value if isinstance(value, list) else []
        elif local_topic == "vehicle/sensor/collision":
            self.sensor_collision = bool(value)
        elif local_topic == "vehicle/sensor/position":
            self.sensor_position = value if isinstance(value, dict) else {}

        # Training signals
        elif local_topic == "vehicle/training/reward":
            self.training_reward = float(value) if value is not None else 0.0
        elif local_topic == "vehicle/training/done":
            # Unity sends {"value":1,...} or {"value":0}
            if bool(value):
                self.training_done = True
                self.done_event.set()
        elif local_topic == "vehicle/training/obs":
            # Unity publishes {"rays":[...],"speed":...,"steer":...} — no "value" wrapper.
            # Store the full payload dict, not payload["value"].
            self.training_obs = payload if isinstance(payload, dict) else {}
            self.obs_event.set()
        elif local_topic == "vehicle/training/episode_end":
            obs_count = payload.get("obstacle_count")
            if obs_count is not None:
                self.training_obstacle_count = int(obs_count)

    # ─── Control commands ────────────────────────────────────────────

    def _publish(self, topic: str, value):
        payload = json.dumps({"value": value})
        self._client.publish(f"{self.prefix}/{topic}", payload)

    def set_throttle(self, value: float):
        self._publish("leda/control/throttle", round(max(0.0, min(1.0, value)), 4))

    def set_steer(self, value: float):
        self._publish("leda/control/steer", round(max(-1.0, min(1.0, value)), 4))

    def set_brake(self, value: float):
        self._publish("leda/control/brake", round(max(0.0, min(1.0, value)), 4))

    def set_gear(self, gear: str):
        """Set gear: 'P', 'R', 'N', or 'D'."""
        self._publish("leda/control/gear", gear.upper())

    def send_reset(self):
        """Request episode reset in Unity."""
        self.training_done = False
        self.done_event.clear()
        self.obs_event.clear()
        self._publish("leda/control/reset", 1)

    def send_action(self, steer: float, throttle: float, brake: float):
        """Send a complete action tuple in one call."""
        self.set_steer(steer)
        self.set_throttle(throttle)
        self.set_brake(brake)

    # ─── Telemetry access ────────────────────────────────────────────

    def get_speed(self) -> float:
        with self._telemetry_lock:
            return float(self.telemetry.get("vehicle/speed", 0.0))

    def get_rpm(self) -> float:
        with self._telemetry_lock:
            return float(self.telemetry.get("vehicle/rpm", 0.0))

    def get_steering(self) -> float:
        with self._telemetry_lock:
            return float(self.telemetry.get("vehicle/steering", 0.0))

    def get_gear(self) -> str:
        with self._telemetry_lock:
            return str(self.telemetry.get("vehicle/gear", "P"))

    def get_brake(self) -> float:
        with self._telemetry_lock:
            return float(self.telemetry.get("vehicle/brake", 0.0))

    def get_throttle(self) -> float:
        with self._telemetry_lock:
            return float(self.telemetry.get("vehicle/throttle", 0.0))
