# SHILATE Project Summary

> **Auto-maintained reference** for the `project-wiki` agent. Updated when PRs are merged to `main`.
> Last updated: 2026-06-04

## What is SHILATE?

SHILATE is a **Software-defined-Vehicle reinforcement-learning project**. A Unity simulation (`SIM/`) hosts a virtual vehicle on an obstacle course; a Python PPO agent (`leda/leda-controller/`) trains over MQTT to navigate it. The Eclipse Leda SDV stack (`leda/`) provides the runtime image, Kuksa data broker, and Velocitas vehicle-app scaffolding for deploying the trained model to a real/emulated device.

## Top-level Layout

| Path | Purpose |
|------|---------|
| `SIM/` | Unity 2022+ project — simulation, vehicle, sensors, RL environment side |
| `SIM/Assets/scripts/` | All gameplay/training C# scripts |
| `leda/leda-controller/` | Python RL training + inference (PPO via stable-baselines3) |
| `leda/mqtt-kuksa-feeder/` | Bridges MQTT ↔ Kuksa data broker |
| `leda/velocitas-app/` | Eclipse Velocitas vehicle-app template (deployment target) |
| `leda/examples/` | Sample manifests and configs |
| `config.json` (repo root) | Shared config — currently holds `model.ray_count` |
| `.github/agents/` | Custom Copilot agents for this repo |

## RL Training Pipeline (Python side)

| File | Role |
|------|------|
| `leda/leda-controller/train.py` | Entry point. Parses CLI args, builds `SubprocVecEnv`, runs `PPO.learn`, saves checkpoints. Defaults: 4 envs, timescale 5, 100k timesteps, lr 3e-4, n_steps 2048, batch 64. CLI flags: `--num-envs --timescale --total-timesteps --learning-rate --n-steps --batch-size --ray-count --mqtt-host --mqtt-port --save-path --log-dir --resume-model`. |
| `leda/leda-controller/environment.py` | `ShilateEnv(gym.Env)` — wraps MQTT comms as a Gymnasium env. Obs = `[N rays, norm_speed, norm_steer]`. Action = `[steer ∈ [-1,1], throttle ∈ [0,1], brake ∈ [0,1]]`. `OBS_TIMEOUT = 5.0s`, `DEFAULT_RAY_COUNT = 9`. |
| `leda/leda-controller/controller.py` | `VehicleController` — low-level MQTT publisher/subscriber for sensor + training topics. |
| `leda/leda-controller/make_env.py` | Factory that produces env-id-keyed `ShilateEnv` instances for vector training. |
| `leda/leda-controller/model.py` | `get_policy_kwargs(ray_count)` — defines PPO net architecture (`pi=[256,128], vf=[256,128]`). Edit here to change neural net topology. |
| `leda/leda-controller/config_loader.py` | Reads `../../config.json` → `model.ray_count`, fallback 9. |
| `leda/leda-controller/ai_driver.py` | Inference-only: loads a trained `.zip` and drives the sim. |
| `leda/leda-controller/debug_drive.py` | Manual/debug driver for diagnosing the MQTT bridge. |
| `leda/leda-controller/Dockerfile` | Container image for training/inference. |
| `leda/leda-controller/requirements.txt` | Python deps (sb3, gymnasium, paho-mqtt, torch). |
| `leda/leda-controller/best_model.zip`, `shilate_ppo_*_steps.zip` | Saved PPO checkpoints. |

## Unity Simulation (C# side)

All scripts live in `SIM/Assets/scripts/`.

| Script | Role |
|--------|------|
| `TrainingBridge.cs` | **Reward + episode logic.** Computes reward = progress·`progressReward` + finish/halfturn bonuses + collision penalty. Inspector fields: `progressReward=2`, `collisionPenalty=-5`, `finishBonus=50`, `halfTurnBonus=25`, `episodeTimeout=60s`, `maxSpeed=150 km/h`. Publishes obs/reward/done over MQTT. |
| `VehicleController.cs` | Vehicle physics — applies steer/throttle/brake from input or RL action. |
| `RaycastSensor.cs` | Emits the N raycasts that form the observation. |
| `ObstacleCourse.cs` | Track + progress tracking around the course. |
| `LedaBroker.cs` / `MqttClient.cs` | MQTT client wiring (publish/subscribe, topic naming per `env_id`). |
| `RemoteDriveInput.cs` | Receives `[steer,throttle,brake]` from Python via MQTT. |
| `ManualDriveInput.cs` | Keyboard/gamepad input for manual driving. |
| `TrainingBootstrap.cs` | Headless training entry — auto-spawns the scene for `num-envs` instances. |
| `StandaloneBootstrap.cs` | Standalone (non-training) launcher. |
| `RuntimeSceneBuilder.cs` / `CarFactory.cs` | Build scene + spawn vehicles at runtime. |
| `TimeScaleController.cs` | Receives `timescale` over MQTT and applies `Time.timeScale`. |
| `SimulationRunner.cs` | Top-level loop coordinator. |
| `DrivingScenario.cs` | Per-scenario configuration. |
| `CameraFollow.cs` | Editor-only follow camera. |
| `VehicleTelemetryBridge.cs` | Publishes telemetry (separate from training signals). |

## How to Run

| Goal | Command |
|------|---------|
| Train (Linux) | `SIM/run-training.sh` then `python3 leda/leda-controller/train.py` |
| Train in parallel | `SIM/run-training-parallel.sh` |
| Train with graphics (debug) | `SIM/run-training-graphics.cmd` |
| Inference | `python3 leda/leda-controller/ai_driver.py` |
| Manual drive (debug) | `python3 leda/leda-controller/debug_drive.py` |
| Boot Leda image | `leda/run-leda.sh` (or `.cmd`) |

## How to Verify Training is Actually Happening

1. **TensorBoard**: `tensorboard --logdir /tmp/shilate_logs` — look for `rollout/ep_rew_mean` increasing.
2. **Console**: `train.py` logs `shilate-train` messages; SB3 prints rollout/training tables every `n_steps`.
3. **Checkpoint files**: `shilate_ppo_<N>_steps.zip` should appear in `--save-path` (`/tmp/shilate_model` by default).
4. **MQTT traffic**: subscribe to `shilate/env<id>/obs` and `shilate/env<id>/reward` — rewards should arrive each step.
5. **Unity console** (with `run-training-graphics`): `TrainingBridge` logs episode end with `_cumulativeReward`.
6. **Episode count**: rising episode index in SB3 logs == environment is resetting and training.

## Common "Where do I change X?" Map

| To change… | Edit… |
|------------|-------|
| Reward shaping (progress/collision/finish/halfturn) | `SIM/Assets/scripts/TrainingBridge.cs` Inspector fields, or serialized defaults |
| Episode timeout | `TrainingBridge.cs` → `episodeTimeout` |
| Action space bounds | `leda/leda-controller/environment.py` → `action_space` |
| Observation contents / size | `environment.py` → `_make_obs` and `RaycastSensor.cs` |
| Number of raycasts | root `config.json` → `model.ray_count` (used by both sides) or `--ray-count` CLI |
| Neural net architecture | `leda/leda-controller/model.py` → `get_policy_kwargs` |
| PPO hyperparameters (lr, n_steps, batch, gamma, etc.) | `train.py` CLI flags or `PPO(...)` call inside `main()` |
| Number of parallel envs | `--num-envs` flag |
| Sim speed | `--timescale` flag → `TimeScaleController.cs` |
| MQTT broker host/port | `--mqtt-host` / `--mqtt-port`, and Unity Inspector on `LedaBroker` |
| Track / obstacles | `SIM/Assets/scripts/ObstacleCourse.cs` + scene assets in `SIM/Assets/Scenes/` |
| Vehicle physics | `SIM/Assets/scripts/VehicleController.cs` and ScriptableObjects in `SIM/Assets/scripts/ScriptableObjects/` |
| Resume from checkpoint | `--resume-model path/to.zip` |

## MQTT Topic Conventions

Topics are namespaced per env, e.g. `shilate/env0/...`:
- `…/action` — Python → Unity: steer/throttle/brake
- `…/obs` — Unity → Python: ray distances + speed + steer
- `…/reward`, `…/done` — Unity → Python training signals
- `…/timescale`, `…/gear`, `…/reset` — control commands

## Update Protocol (automated)

This file is refreshed automatically. On every push to `main`, `.github/workflows/update-wiki.yml` opens a GitHub issue assigned to `@copilot` containing the merge SHA and a `git diff --name-status` list. The Copilot coding agent then runs the **SHILATE Wiki** custom agent (defined in `project-wiki.agent.md`) in update mode and opens a PR titled `chore(wiki): auto-refresh project-summary`.

The agent must:
1. Use the diff in the triggering issue to know what changed.
2. For each changed source file in the tables above, re-read it and update the relevant row (defaults, fields, CLI flags, descriptions).
3. Add rows for new files, remove rows for deleted files.
4. Bump the "Last updated" date.
5. Edit **only** this file.
6. Keep the document under ~250 lines — collapse stale detail rather than appending.

Humans should not need to invoke this manually. To force a refresh, run the `Update SHILATE Wiki` workflow via the Actions tab (`workflow_dispatch`).
