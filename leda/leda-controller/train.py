#!/usr/bin/env python3
"""
SHILATE RL Training Script — trains a PPO agent against a single Unity
environment running inside the Unity Editor.

Usage:
    python3 train.py [options]

    --total-timesteps  Total training timesteps (default: unlimited)
    --learning-rate    Learning rate (default: 3e-4)
    --n-steps          Steps per rollout (default: 2048)
    --batch-size       Minibatch size (default: 64)
    --mqtt-host        MQTT broker host (default: localhost)
    --mqtt-port        MQTT broker port (default: 1883)
    --save-path        Model save directory (default: <project-root>/models)
    --log-dir          TensorBoard log directory (default: <project-root>/logs/tensorboard)
    --resume-model     Path to a .zip model file to resume from

The number of raycast sensors is read from ../../config.json (model.ray_count).
This trainer always runs against exactly ONE environment at real-time speed —
parallel environments and time acceleration have been intentionally removed
so failures are easy to diagnose in the Editor.

Structured stdout markers (consumed by the Unity Editor):
    SHILATE-METRIC <key>=<value>     single metric update
    SHILATE-HEALTH <signal>          health-relevant event
"""

import argparse
import logging
import math
import os
import sys

_UNLIMITED = sys.maxsize  # effective "no limit" for SB3 (requires an int)

# Project root is two levels above this script (leda/leda-controller/ → root)
_PROJECT_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s — %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
log = logging.getLogger("shilate-train")


def emit(marker: str, payload: str) -> None:
    """Write a structured marker line to stdout for the Editor to parse."""
    print(f"{marker} {payload}", flush=True)


def parse_args():
    p = argparse.ArgumentParser(description="SHILATE RL Training (single env)")
    p.add_argument("--total-timesteps", type=int, default=None,
                   help="Total training timesteps. Omit (or leave blank in the UI) to train indefinitely.")
    p.add_argument("--learning-rate", type=float, default=3e-4)
    p.add_argument("--n-steps", type=int, default=2048)
    p.add_argument("--batch-size", type=int, default=64)
    p.add_argument("--mqtt-host", default="localhost")
    p.add_argument("--mqtt-port", type=int, default=1883)
    p.add_argument("--save-path", default=os.path.join(_PROJECT_ROOT, "models"))
    p.add_argument("--log-dir", default=os.path.join(_PROJECT_ROOT, "logs", "tensorboard"))
    p.add_argument("--resume-model", default=None,
                   help="Path to a .zip model file to resume training from")
    p.add_argument("--ent-coef",type=float, default=0.01, help="This encourages the agent to explore more")
    return p.parse_args()


def main():
    args = parse_args()

    # Import here so --help is fast even without torch installed
    from stable_baselines3 import PPO
    from stable_baselines3.common.vec_env import DummyVecEnv
    from stable_baselines3.common.callbacks import BaseCallback, CheckpointCallback
    from stable_baselines3.common.monitor import Monitor

    from config_loader import load_ray_count_from_config
    from environment import ShilateEnv
    from model import get_policy_kwargs

    ray_count = load_ray_count_from_config()

    # Resolve unlimited training: SB3 requires an int, so use sys.maxsize.
    unlimited = args.total_timesteps is None
    total_timesteps = _UNLIMITED if unlimited else args.total_timesteps

    log.info("═══════════════════════════════════════════════════")
    log.info("  SHILATE RL Training — single env, real time")
    log.info("═══════════════════════════════════════════════════")
    log.info("  Total timesteps:  %s", "unlimited" if unlimited else total_timesteps)
    log.info("  Learning rate:    %s", args.learning_rate)
    log.info("  n_steps:          %d", args.n_steps)
    log.info("  Batch size:       %d", args.batch_size)
    log.info("  Ray count:        %d (from config.json)", ray_count)
    log.info("  MQTT:             %s:%d", args.mqtt_host, args.mqtt_port)
    log.info("  Save path:        %s", args.save_path)
    log.info("  Log dir:          %s", args.log_dir)
    if args.resume_model:
        log.info("  Resume model:     %s", args.resume_model)
    else:
        log.info("  Mode:             new model")

    log.info("Creating environment...")

    def _make_env():
        env = ShilateEnv(
            mqtt_host=args.mqtt_host,
            mqtt_port=args.mqtt_port,
            ray_count=ray_count,
        )
        return Monitor(env)

    # DummyVecEnv runs in-process — no subprocess, no IPC overhead, easy to debug.
    vec_env = DummyVecEnv([_make_env])

    # Create PPO model
    policy_kwargs = get_policy_kwargs(ray_count=ray_count)

    if args.resume_model:
        log.info("Loading model from %s ...", args.resume_model)
        model = PPO.load(
            args.resume_model,
            env=vec_env,
            learning_rate=args.learning_rate,
            n_steps=args.n_steps,
            batch_size=args.batch_size,
            ent_coef=args.ent_coef,
        )
        model.tensorboard_log = args.log_dir
        log.info("Model loaded, resuming training")
    else:
        model = PPO(
            "MlpPolicy",
            vec_env,
            learning_rate=args.learning_rate,
            n_steps=args.n_steps,
            batch_size=args.batch_size,
            ent_coef=args.ent_coef,
            policy_kwargs=policy_kwargs,
            verbose=1,
            tensorboard_log=args.log_dir,
        )
        log.info("PPO model created")

    # Checkpoint callback — save every 50k steps when running unlimited,
    # otherwise every 10% of the total budget (min 1000 steps).
    _checkpoint_freq = 50_000 if unlimited else max(total_timesteps // 10, 1000)
    os.makedirs(args.save_path, exist_ok=True)
    checkpoint_cb = CheckpointCallback(
        save_freq=_checkpoint_freq,
        save_path=args.save_path,
        name_prefix="shilate_ppo",
    )

    # Health/metric callback — re-emits SB3's rollout stats as SHILATE-METRIC lines
    # and raises SHILATE-HEALTH alerts when loss/KL become non-finite.
    class HealthCallback(BaseCallback):
        _WATCH = (
            "rollout/ep_rew_mean",
            "rollout/ep_len_mean",
            "train/loss",
            "train/policy_gradient_loss",
            "train/value_loss",
            "train/approx_kl",
            "time/fps",
        )

        def _on_step(self) -> bool:
            return True

        def _on_rollout_end(self) -> None:
            emit("SHILATE-METRIC", f"rollout_end={self.num_timesteps}")
            self._dump_metrics()

        def _check_finite(self, key: str, value) -> None:
            try:
                fv = float(value)
            except (TypeError, ValueError):
                return
            if math.isnan(fv) or math.isinf(fv):
                emit("SHILATE-HEALTH", f"nan_loss key={key} value={value}")

        def _dump_metrics(self) -> None:
            logger = self.model.logger
            if logger is None:
                return
            stats = getattr(logger, "name_to_value", {}) or {}
            for key in self._WATCH:
                if key in stats:
                    val = stats[key]
                    short = key.split("/", 1)[-1]
                    emit("SHILATE-METRIC", f"{short}={val}")
                    if "loss" in key or "kl" in key:
                        self._check_finite(key, val)

    health_cb = HealthCallback()

    # Train
    if unlimited:
        log.info("Starting training — no timestep limit (Ctrl-C to stop)...")
    else:
        log.info("Starting training for %d timesteps...", total_timesteps)
    emit("SHILATE-HEALTH", "training_started")
    try:
        model.learn(
            total_timesteps=total_timesteps,
            callback=[checkpoint_cb, health_cb],
            tb_log_name="shilate_ppo",
            reset_num_timesteps=args.resume_model is None,
            progress_bar=False,
        )
    except KeyboardInterrupt:
        log.info("Training interrupted by user")
        emit("SHILATE-HEALTH", "training_interrupted")
    except Exception as exc:
        log.exception("Training crashed: %s", exc)
        emit("SHILATE-HEALTH", f"training_crashed reason={type(exc).__name__}")
        raise

    # Save final model
    final_path = os.path.join(args.save_path, "best_model")
    model.save(final_path)
    log.info("Final model saved to %s.zip", final_path)
    emit("SHILATE-METRIC", f"final_model={final_path}.zip")

    vec_env.close()
    emit("SHILATE-HEALTH", "training_complete")
    log.info("Training complete.")


if __name__ == "__main__":
    main()
