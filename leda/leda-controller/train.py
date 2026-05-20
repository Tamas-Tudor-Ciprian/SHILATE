#!/usr/bin/env python3
"""
SHILATE RL Training Script — trains a PPO agent to navigate an obstacle course.

Usage:
    python3 train.py [options]

    --num-envs      Number of parallel Unity environments (default: 4)
    --timescale     Unity Time.timeScale for training speed (default: 5)
    --total-timesteps  Total training timesteps (default: 100000)
    --learning-rate Learning rate (default: 3e-4)
    --n-steps       Steps per rollout per env (default: 2048)
    --batch-size    Minibatch size (default: 64)
    --ray-count     Number of raycast sensors (default: 9)
    --mqtt-host     MQTT broker host (default: localhost)
    --mqtt-port     MQTT broker port (default: 1883)
    --save-path     Model save directory (default: /tmp/shilate_model)
    --log-dir       TensorBoard log directory (default: /tmp/shilate_logs)
"""

import argparse
import logging
import os
import sys

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s — %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
log = logging.getLogger("shilate-train")


def parse_args():
    from config_loader import load_ray_count_from_config
    default_ray_count = load_ray_count_from_config()
    p = argparse.ArgumentParser(description="SHILATE RL Training")
    p.add_argument("--num-envs", type=int, default=4)
    p.add_argument("--timescale", type=float, default=5.0)
    p.add_argument("--total-timesteps", type=int, default=100_000)
    p.add_argument("--learning-rate", type=float, default=3e-4)
    p.add_argument("--n-steps", type=int, default=2048)
    p.add_argument("--batch-size", type=int, default=64)
    p.add_argument("--ray-count", type=int, default=default_ray_count)
    p.add_argument("--mqtt-host", default="localhost")
    p.add_argument("--mqtt-port", type=int, default=1883)
    p.add_argument("--save-path", default="/tmp/shilate_model")
    p.add_argument("--log-dir", default="/tmp/shilate_logs")
    p.add_argument("--resume-model", default=None,
                   help="Path to a .zip model file to resume training from")
    return p.parse_args()


def main():
    args = parse_args()

    # Import here so --help is fast even without torch installed
    from stable_baselines3 import PPO
    from stable_baselines3.common.vec_env import SubprocVecEnv
    from stable_baselines3.common.callbacks import CheckpointCallback

    from make_env import make_env
    from model import get_policy_kwargs

    log.info("═══════════════════════════════════════════════════")
    log.info("  SHILATE RL Training")
    log.info("═══════════════════════════════════════════════════")
    log.info("  Environments:     %d", args.num_envs)
    log.info("  TimeScale:        %.1fx", args.timescale)
    log.info("  Total timesteps:  %d", args.total_timesteps)
    log.info("  Learning rate:    %s", args.learning_rate)
    log.info("  n_steps:          %d", args.n_steps)
    log.info("  Batch size:       %d", args.batch_size)
    log.info("  Ray count:        %d", args.ray_count)
    log.info("  MQTT:             %s:%d", args.mqtt_host, args.mqtt_port)
    log.info("  Save path:        %s", args.save_path)
    log.info("  Log dir:          %s", args.log_dir)
    if args.resume_model:
        log.info("  Resume model:     %s", args.resume_model)
    else:
        log.info("  Mode:             new model")

    # Create parallel environments
    env_fns = [
        make_env(
            env_id=i,
            mqtt_host=args.mqtt_host,
            mqtt_port=args.mqtt_port,
            ray_count=args.ray_count,
            timescale=args.timescale,
        )
        for i in range(args.num_envs)
    ]

    log.info("Creating %d parallel environments...", args.num_envs)
    vec_env = SubprocVecEnv(env_fns)

    # Create PPO model
    policy_kwargs = get_policy_kwargs(ray_count=args.ray_count)

    if args.resume_model:
        log.info("Loading model from %s ...", args.resume_model)
        model = PPO.load(
            args.resume_model,
            env=vec_env,
            learning_rate=args.learning_rate,
            n_steps=args.n_steps,
            batch_size=args.batch_size,
        )
        model.tensorboard_log = args.log_dir
        log.info("Model loaded successfully, resuming training")
    else:
        model = PPO(
            "MlpPolicy",
            vec_env,
            learning_rate=args.learning_rate,
            n_steps=args.n_steps,
            batch_size=args.batch_size,
            policy_kwargs=policy_kwargs,
            verbose=1,
            tensorboard_log=args.log_dir,
        )
        log.info("PPO model created: %s", model.policy)

    # Checkpoint callback
    os.makedirs(args.save_path, exist_ok=True)
    checkpoint_cb = CheckpointCallback(
        save_freq=max(args.total_timesteps // 10, 1000),
        save_path=args.save_path,
        name_prefix="shilate_ppo",
    )

    # Train
    log.info("Starting training for %d timesteps...", args.total_timesteps)
    try:
        model.learn(
            total_timesteps=args.total_timesteps,
            callback=checkpoint_cb,
            tb_log_name="shilate_ppo",
            reset_num_timesteps=args.resume_model is None,
        )
    except KeyboardInterrupt:
        log.info("Training interrupted by user")

    # Save final model
    final_path = os.path.join(args.save_path, "best_model")
    model.save(final_path)
    log.info("Final model saved to %s.zip", final_path)

    vec_env.close()
    log.info("Training complete.")


if __name__ == "__main__":
    main()
