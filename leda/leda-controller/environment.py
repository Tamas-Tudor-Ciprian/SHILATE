"""
SHILATE Gymnasium Environment — wraps MQTT communication with a single Unity
SHILATE instance into a standard Gym interface for Stable-Baselines3.

This wrapper is intentionally single-environment. Parallel training and time
acceleration were removed in favour of one Editor instance at real time so
problems are visible from the Unity Editor.
"""

import logging
import time

import gymnasium as gym
import numpy as np
from gymnasium import spaces

from controller import VehicleController

log = logging.getLogger(__name__)

OBS_TIMEOUT = 5.0  # seconds to wait for an observation before declaring it stale


def _emit(marker: str, payload: str) -> None:
    """Mirror train.py's structured stdout markers so the Editor sees env health."""
    print(f"{marker} {payload}", flush=True)


class ShilateEnv(gym.Env):
    """
    Gymnasium environment that communicates with a Unity SHILATE instance via MQTT.

    Observation: [ray_0..ray_N, speed, steer, progress, vForward, vLateral,
                  prev_steer, prev_throttle, prev_brake]
    Action:      [steer (-1 to 1), throttle (0 to 1), brake (0 to 1)]
    """

    metadata = {"render_modes": []}

    def __init__(
        self,
        ray_count: int,
        mqtt_host: str = "localhost",
        mqtt_port: int = 1883,
        env_prefix: str = "env0",
        smoothness_coeff: float = 0.05,
    ):
        super().__init__()

        self.ray_count = ray_count
        self.smoothness_coeff = smoothness_coeff

        # Observation: rays + speed + steer + progress + vForward + vLateral
        #              + prev_steer + prev_throttle + prev_brake
        obs_size = ray_count + 8
        self.observation_space = spaces.Box(
            low=0.0, high=1.0, shape=(obs_size,), dtype=np.float32
        )

        # Action: [steer, throttle, brake]
        self.action_space = spaces.Box(
            low=np.array([-1.0, 0.0, 0.0], dtype=np.float32),
            high=np.array([1.0, 1.0, 1.0], dtype=np.float32),
            dtype=np.float32,
        )

        # MQTT controller (always env0)
        self._ctrl = VehicleController(
            mqtt_host=mqtt_host,
            mqtt_port=mqtt_port,
            subscribe_sensors=True,
            subscribe_training=True,
            env_prefix=env_prefix,
        )

        self._connected = False
        self._step_count = 0
        self._prev_action = np.zeros(3, dtype=np.float32)  # [steer, throttle, brake]

    def _ensure_connected(self):
        if not self._connected:
            if not self._ctrl.connect(timeout=15.0):
                _emit("SHILATE-HEALTH", "mqtt_connect_failed")
                raise ConnectionError(
                    f"Could not connect to MQTT at "
                    f"{self._ctrl.mqtt_host}:{self._ctrl.mqtt_port}"
                )
            self._connected = True
            _emit("SHILATE-HEALTH", "mqtt_connected")
            self._ctrl.set_gear("D")

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self._ensure_connected()

        self._ctrl.send_reset()
        self._step_count = 0
        self._prev_action = np.zeros(3, dtype=np.float32)

        # Car resets to Park gear — need brake then gear D
        self._ctrl.set_brake(1.0)
        time.sleep(0.1)
        self._ctrl.set_gear("D")
        time.sleep(0.1)
        self._ctrl.set_brake(0.0)

        # Wait for first observation after reset
        self._ctrl.obs_event.clear()
        if not self._ctrl.obs_event.wait(timeout=OBS_TIMEOUT):
            log.warning("Timeout waiting for observation after reset")
            _emit("SHILATE-HEALTH", "obs_timeout phase=reset")

        obs = self._build_observation()
        return obs, {}

    def step(self, action):
        self._ensure_connected()

        steer, throttle, brake = float(action[0]), float(action[1]), float(action[2])

        # Smoothness penalty: penalise abrupt steering changes only.
        # Throttle and brake are excluded — penalising acceleration deltas
        # creates a local optimum at throttle=0 that prevents the agent from
        # learning to move.
        delta_steer = abs(steer - float(self._prev_action[0]))
        smoothness_penalty = -self.smoothness_coeff * delta_steer

        self._prev_action = np.array([steer, throttle, brake], dtype=np.float32)
        self._ctrl.send_action(steer, throttle, brake)

        # Wait for next observation from Unity
        self._ctrl.obs_event.clear()
        if not self._ctrl.obs_event.wait(timeout=OBS_TIMEOUT):
            log.warning("Observation timeout at step %d", self._step_count)
            _emit("SHILATE-HEALTH", f"obs_timeout phase=step step={self._step_count}")
            obs = self._build_observation()
            return obs, -1.0, False, True, {"reason": "obs_timeout"}

        obs = self._build_observation()
        reward = self._ctrl.training_reward + smoothness_penalty
        done = self._ctrl.training_done
        self._step_count += 1

        return obs, reward, done, False, {}

    def _build_observation(self) -> np.ndarray:
        """Build observation vector from latest sensor/training data."""
        training_obs = self._ctrl.training_obs

        if isinstance(training_obs, dict) and "rays" in training_obs:
            rays = training_obs["rays"]
            speed = training_obs.get("speed", 0.0)
            steer = training_obs.get("steer", 0.5)
            progress = training_obs.get("progress", 0.0)
            v_forward = training_obs.get("vForward", 0.0)
            v_lateral = training_obs.get("vLateral", 0.5)
        else:
            # Fallback to raw sensor data
            rays = self._ctrl.sensor_rays
            speed = min(self._ctrl.get_speed() / 150.0, 1.0)
            steer = 0.5
            progress = 0.0
            v_forward = speed  # best available approximation
            v_lateral = 0.5

        # Pad or truncate rays to expected count
        ray_arr = np.zeros(self.ray_count, dtype=np.float32)
        for i in range(min(len(rays), self.ray_count)):
            ray_arr[i] = float(rays[i])

        # Normalise prev_action to [0, 1]: steer maps [-1,1]→[0,1], throttle/brake stay as-is
        prev_steer_norm = (float(self._prev_action[0]) + 1.0) * 0.5
        prev_throttle = float(self._prev_action[1])
        prev_brake = float(self._prev_action[2])

        obs = np.concatenate([
            ray_arr,
            [float(speed), float(steer), float(progress),
             float(v_forward), float(v_lateral),
             prev_steer_norm, prev_throttle, prev_brake],
        ])
        return np.clip(obs, 0.0, 1.0).astype(np.float32)

    def close(self):
        if self._connected:
            self._ctrl.send_action(0.0, 0.0, 0.0)
            time.sleep(0.1)
            self._ctrl.disconnect()
            self._connected = False
