#!/usr/bin/env python3
"""
SHILATE AI Driver — runs a trained PPO model for autonomous obstacle navigation.

Usage:
    python3 ai_driver.py --model /tmp/shilate_model/best_model.zip [options]

    --model         Path to trained SB3 model (required)
    --mqtt-host     MQTT broker host (default: localhost)
    --mqtt-port     MQTT broker port (default: 1883)
    --episodes      Number of episodes to run, 0=infinite (default: 0)

Ray count is read from ../../config.json (model.ray_count). The driver always
runs against a single Unity environment at real time.
"""

import argparse
import logging
import signal
import sys
import time

import numpy as np

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("ai-driver")

_running = True


def handle_signal(sig, frame):
    global _running
    _running = False


def parse_args():
    p = argparse.ArgumentParser(description="SHILATE AI Driver")
    p.add_argument("--model", required=True, help="Path to trained model .zip")
    p.add_argument("--mqtt-host", default="localhost")
    p.add_argument("--mqtt-port", type=int, default=1883)
    p.add_argument("--episodes", type=int, default=0, help="0 = run forever")
    p.add_argument("--env-prefix", default="env0",
                   help="MQTT topic prefix matching the Unity instance (e.g. env0, env1)")
    return p.parse_args()


def main():
    args = parse_args()

    from stable_baselines3 import PPO
    from config_loader import load_ray_count_from_config
    from controller import VehicleController

    ray_count = load_ray_count_from_config()

    signal.signal(signal.SIGINT, handle_signal)
    signal.signal(signal.SIGTERM, handle_signal)

    log.info("Loading model from %s (rays=%d)", args.model, ray_count)
    model = PPO.load(args.model)

    ctrl = VehicleController(
        mqtt_host=args.mqtt_host,
        mqtt_port=args.mqtt_port,
        subscribe_sensors=True,
        subscribe_training=True,
        env_prefix=args.env_prefix,
    )

    if not ctrl.connect():
        log.error("Could not connect to MQTT broker")
        sys.exit(1)

    ctrl.set_gear("D")

    log.info("AI Driver running (model: %s)", args.model)
    log.info("Press Ctrl+C to stop")

    episode = 0
    step = 0

    # Initial reset
    ctrl.send_reset()
    ctrl.obs_event.clear()
    ctrl.obs_event.wait(timeout=5.0)

    while _running:
        if args.episodes > 0 and episode >= args.episodes:
            break

        # Build observation
        obs = build_observation(ctrl, ray_count)

        # Run inference
        action, _ = model.predict(obs, deterministic=True)
        steer, throttle, brake = float(action[0]), float(action[1]), float(action[2])

        ctrl.send_action(steer, throttle, brake)
        step += 1

        # Wait for next observation
        ctrl.obs_event.clear()
        if not ctrl.obs_event.wait(timeout=2.0):
            log.warning("Observation timeout at step %d", step)
            continue

        # Print telemetry periodically
        if step % 50 == 0:
            log.info(
                "Step %d | SPD: %.1f km/h | STR: %+.2f | THR: %.2f | BRK: %.2f",
                step, ctrl.get_speed(), steer, throttle, brake,
            )

        # Check episode done
        if ctrl.training_done:
            episode += 1
            log.info("Episode %d complete (step %d)", episode, step)
            step = 0

            # Auto-reset for next episode
            time.sleep(0.5)
            ctrl.send_reset()
            time.sleep(0.1)
            ctrl.set_brake(1.0)
            ctrl.set_gear("D")
            time.sleep(0.1)
            ctrl.set_brake(0.0)
            ctrl.obs_event.clear()
            ctrl.obs_event.wait(timeout=5.0)

    # Stop car
    ctrl.send_action(0.0, 0.0, 0.0)
    ctrl.set_gear("P")
    time.sleep(0.1)
    ctrl.disconnect()
    log.info("AI Driver stopped after %d episodes", episode)


def build_observation(ctrl: "VehicleController", ray_count: int) -> np.ndarray:
    training_obs = ctrl.training_obs

    if isinstance(training_obs, dict) and "rays" in training_obs:
        rays = training_obs["rays"]
        speed = training_obs.get("speed", 0.0)
        steer = training_obs.get("steer", 0.5)
    else:
        rays = ctrl.sensor_rays
        speed = min(ctrl.get_speed() / 150.0, 1.0)
        steer = 0.5

    ray_arr = np.zeros(ray_count, dtype=np.float32)
    for i in range(min(len(rays), ray_count)):
        ray_arr[i] = float(rays[i])

    obs = np.concatenate([ray_arr, [float(speed), float(steer)]])
    return np.clip(obs, 0.0, 1.0).astype(np.float32)


if __name__ == "__main__":
    main()
