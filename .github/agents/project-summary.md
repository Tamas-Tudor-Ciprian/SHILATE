# SHILATE Project Summary

> **Auto-maintained reference** for the `project-wiki` agent. Updated when PRs are merged to `main`.
> Last updated: 2026-06-29

## What is SHILATE?

SHILATE is a **Software-defined-Vehicle reinforcement-learning project**. A Unity simulation (`SIM/`) hosts a virtual vehicle on an obstacle course; a Python PPO agent (`leda/leda-controller/`) trains over MQTT to navigate it. The Eclipse Leda SDV stack (`leda/`) provides the runtime image, Kuksa data broker, and Velocitas vehicle-app scaffolding for deploying the trained model to a real/emulated device.

**Training is Editor-only and single-environment.** One Unity Editor instance runs at real time (`Time.timeScale = 1`), driven from the **Training Controller** window (`SHILATE → Training Controller`). The fixed MQTT prefix is `env0`. There is no parallel-env mode, no headless trainer, and no time-acceleration knob.

## Top-level Layout

| Path | Purpose |
|------|---------|
| `SIM/` | Unity 2022+ project — simulation, vehicle, sensors, RL environment side |
| `SIM/Assets/scripts/` | All gameplay/training C# scripts |
| `SIM/Assets/scripts/Editor/Training/` | Editor-only training control + health UI |
| `leda/leda-controller/` | Python RL training + inference (PPO via stable-baselines3) |
| `leda/mqtt-kuksa-feeder/` | Bridges MQTT ↔ Kuksa data broker |
| `leda/velocitas-app/` | Eclipse Velocitas vehicle-app template (deployment target) |
| `leda/examples/` | Sample manifests and configs |
| `leda/docker-compose.pi.yml` | Raspberry Pi deployment stack — 5 containers (mosquitto, kuksa-databroker, mqtt-kuksa-feeder, velocitas-app, leda-controller), all `network_mode: host` |
| `leda/deploy-pi.sh` | Builds ARM64 Docker images via `docker buildx`, transfers them to the Pi (`tetrix@10.122.6.115`) over SSH/SCP, and starts the stack with `docker compose up -d` |
| `config.json` (repo root) | Shared config — currently holds `model.ray_count` |
| `.github/agents/` | Custom Copilot agents for this repo |

## RL Training Pipeline (Python side)

| File | Role |
|------|------|
| `leda/leda-controller/train.py` | Entry point. Builds a single `Monitor(ShilateEnv(...))` wrapped in a one-slot `DummyVecEnv`, then `PPO.learn`. Defaults: 100k timesteps, lr 3e-4, n_steps 2048, batch 64. CLI flags: `--total-timesteps --learning-rate --n-steps --batch-size --mqtt-host --mqtt-port --save-path --log-dir --resume-model`. Emits `SHILATE-METRIC` / `SHILATE-HEALTH` markers on stdout for the Editor parser. |
| `leda/leda-controller/environment.py` | `ShilateEnv(gym.Env)` — wraps MQTT comms as a Gymnasium env. Obs = `[N rays, norm_speed, norm_steer]`. Action = `[steer ∈ [-1,1], throttle ∈ [0,1], brake ∈ [0,1]]`. `OBS_TIMEOUT = 5.0s`. Emits `SHILATE-HEALTH obs_timeout` / `mqtt_lost` markers when the bridge breaks. `ray_count` is constructor-injected (loaded from `config.json` by `train.py`). |
| `leda/leda-controller/controller.py` | `VehicleController` — low-level MQTT publisher/subscriber. Topic prefix hardcoded to `env0`. Subscribes to `env0/vehicle/training/{obs,reward,done,episode_end,heartbeat}`. |
| `leda/leda-controller/model.py` | `get_policy_kwargs(ray_count)` — defines PPO net architecture (`pi=[256,128], vf=[256,128]`). |
| `leda/leda-controller/config_loader.py` | Reads `../../config.json` → `model.ray_count`, fallback 9. |
| `leda/leda-controller/ai_driver.py` | Inference-only: loads a trained `.zip` and drives the sim. `ray_count` from `config.json`; no `--ray-count` flag. |
| `leda/leda-controller/debug_drive.py` | Manual/debug driver for diagnosing the MQTT bridge. |
| `leda/leda-controller/Dockerfile` | Container image for training/inference. Env vars: `MODE`, `MQTT_HOST`, `MQTT_PORT`, `TOTAL_TIMESTEPS`, `MODEL_PATH`. |
| `leda/leda-controller/kanto-manifest.json` | Kanto container manifest matching the Dockerfile env vars. |
| `leda/leda-controller/requirements.txt` | Python deps (sb3, gymnasium, paho-mqtt, torch). |
| `leda/leda-controller/best_model.zip`, `shilate_ppo_*_steps.zip` | Saved PPO checkpoints. |

## Unity Simulation — Runtime (C# side)

All runtime scripts live in `SIM/Assets/scripts/`.

| Script | Role |
|--------|------|
| `TrainingBridge.cs` | **Reward + episode logic.** Computes reward = progress·`progressReward` + finish/halfturn bonuses + collision penalty. Inspector fields: `progressReward=2`, `collisionPenalty=-5`, `finishBonus=50`, `halfTurnBonus=25`, `episodeTimeout=60s`, `maxSpeed=150 km/h`. Publishes `obs/reward/done`, plus **`vehicle/training/episode_end`** (JSON `{episode, reward, steps, duration, reason}`) and **`vehicle/training/heartbeat`** (1 Hz unscaled) for the Editor health UI. Exposes `CurrentEpisode`, `EpisodeSteps`, `EpisodeTime`, `EpisodeTimeout`, `CumulativeReward`. |
| `TrainingHUDOverlay.cs` | **Play-mode IMGUI HUD.** `#if UNITY_EDITOR`-guarded `MonoBehaviour` wired by `CarFactory`. Compact pin in the top-left of the Game view (260×132) showing episode #, cumulative reward, elapsed/timeout, MQTT status, last end reason. Toggle with **F8**. Reads only locally-visible signals (broker, TrainingBridge); SB3-derived health stays in the Editor window. |
| `VehicleController.cs` | Vehicle physics — applies steer/throttle/brake from input or RL action. |
| `RaycastSensor.cs` | Emits the N raycasts that form the observation. |
| `ObstacleCourse.cs` | Track + progress tracking around the course. |
| `LedaBroker.cs` / `MqttClient.cs` | MQTT client wiring (publish/subscribe). `LedaBroker.Configure(host, port, "env0")` is the only call site. No timescale handling. |
| `RemoteDriveInput.cs` | Receives `[steer,throttle,brake]` from Python via MQTT. |
| `ManualDriveInput.cs` | Keyboard/gamepad input for manual driving (auto-disabled during training). |
| `RuntimeSceneBuilder.cs` / `CarFactory.cs` | Build scene + spawn the single vehicle at runtime. `CarFactory` wires `TrainingHUDOverlay` in Editor builds. |
| `SimulationRunner.cs` | Top-level loop coordinator. |
| `DrivingScenario.cs` | Per-scenario configuration. |
| `CameraFollow.cs` | Editor-only follow camera. |
| `VehicleTelemetryBridge.cs` | Publishes telemetry (separate from training signals). |

**Removed in the single-env refactor:** `TimeScaleController.cs`, `TrainingBootstrap.cs`, `StandaloneBootstrap.cs`, `make_env.py`. All `run-training-*.{sh,cmd,ps1}` scripts in `SIM/` are gone.

## Unity Simulation — Editor (Training Controller)

All Editor scripts live in `SIM/Assets/scripts/Editor/Training/`.

| Script | Role |
|--------|------|
| `TrainingEditorWindow.cs` | **The training UX.** Menu: `SHILATE → Training Controller`. Layout top-to-bottom: toolbar (status + MQTT dot + duration) → coloured health banner → snapshot row (Episodes / Last reward / Rolling reward / Heartbeats) → collapsed settings → metric graphs (reward / policy loss / value loss / KL) → issue log → collapsed raw stdout → controls (Start Training / Run Model / Stop). Three operating modes: **Training** (enters Play mode + spawns local `train.py`), **Inference** (pick a local `.zip` → spawns `ai_driver.py`), **Remote** (cancel the file picker → kills local Mosquitto/Python, enters Play mode, points `LedaBroker` at the Pi broker, no local Python started; toolbar shows amber **[REMOTE]**; on Stop, local Mosquitto is restarted). State persisted via `SessionState`. Beeps on non-zero Python exit. |
| `TrainingHealthEvaluator.cs` | Pure C# state machine emitting `HealthState ∈ {Idle, Healthy, Warning, Critical, Disconnected}`. Tickle every 0.25 s. Signals: MQTT disconnect, Python crash, no obs > 5 s, no heartbeat / no rewards > 60 s, reward collapse (> 50% drop over a 10-episode window), NaN/Inf in losses. `History` list of `Issue { Time, Level, Message }` powers the issue log. |
| `TrainingMetricsParser.cs` | Parses SB3 stdout tables (`rollout/ep_rew_mean`, `train/policy_gradient_loss`, `train/value_loss`, `train/approx_kl`) **and** the structured `SHILATE-METRIC key=value` / `SHILATE-HEALTH signal` markers. Exposes histories per metric and an `OnHealthMarker` event consumed by the evaluator. |
| `PythonProcessManager.cs` | Spawns/kills the Python trainer or inference script. Streams stdout/stderr line-by-line; raises `OnOutputLine`, `OnErrorLine`, `OnExited(int)`. No `debugMode`/`--num-envs`/`--timescale`/`--ray-count` plumbing. |
| `EditorMqttListener.cs` | Editor-side MQTT subscriber (separate client-id from the runtime broker). Subscribes to `env0/vehicle/training/episode_end` and `env0/vehicle/training/heartbeat`, parses JSON, fires `OnEpisodeEnd(episode, reward, steps, reason)` / `OnHeartbeat(episode, steps, reward)` / `OnConnectionChanged(bool)`. Pumped from `EditorApplication.update`. |
| `SleepPreventer.cs` | Cross-platform OS sleep inhibitor held while training/inference is active. |
| `TrainingSettings.cs` (ScriptableObject in `Assets/scripts/ScriptableObjects/`) | Persisted knobs: `venvPath`, `trainScriptPath`, `learningRate`, `nSteps`, `batchSize`, `mqttHost`, `mqttPort`, `savePath`, `logDir`, `resumeModelPath`. `BuildCommandLineArgs()` takes no parameters. |

## How to Run

| Goal | How |
|------|-----|
| Train | Open Unity → menu **SHILATE → Training Controller** → press **Start Training**. The window enters Play mode and launches Python automatically. |
| Inference (local model) | Same window → **Run Model** → pick a `.zip` checkpoint. |
| Inference (Pi remote) | Same window → **Run Model** → **Cancel** in the file dialog. Kills local Mosquitto, enters Play mode, `LedaBroker` connects to the Pi broker; Pi's `leda-controller` drives the sim. |
| Manual drive (debug) | `python3 leda/leda-controller/debug_drive.py` while Unity is in Play mode. |
| Deploy to Raspberry Pi | `cd leda && ./deploy-pi.sh` (optionally with `feeder`, `app`, `controller`, or `all`). |
| Boot Leda QEMU image | `leda/run-leda.sh` (or `.cmd`). |

For local training/inference, a Mosquitto broker must be running on the configured host (`127.0.0.1:1883` by default). For Pi remote mode, set `mqttHost` to the Pi's IP in Settings — the window manages broker switching automatically.

## How to Tell If Training Is Healthy (at a glance)

The Training Controller window is designed so failures are obvious in under one second:

1. **Health banner** — coloured strip near the top. Green = healthy; yellow = warning; red = critical; gray = disconnected/idle. The current banner text names the exact problem.
2. **Snapshot row** — Episodes / Last reward / Rolling reward / Heartbeats. If Heartbeats stops climbing, Unity has stalled.
3. **Issue log** — chronological list of every health-state transition for the session.
4. **Metric graphs** — reward, policy loss, value loss, KL divergence; flat lines = no learning.
5. **Game-view HUD** (F8 to toggle) — episode #, reward, timeout countdown, MQTT dot.
6. **Tooling fallbacks** — TensorBoard at `--log-dir` (default `/tmp/shilate_logs`); checkpoints land in `--save-path` (default `/tmp/shilate_model`).

## Common "Where do I change X?" Map

| To change… | Edit… |
|------------|-------|
| Reward shaping (progress/collision/finish/halfturn) | `SIM/Assets/scripts/TrainingBridge.cs` Inspector fields |
| Episode timeout | `TrainingBridge.cs` → `episodeTimeout` |
| Action space bounds | `leda/leda-controller/environment.py` → `action_space` |
| Observation contents / size | `environment.py` → `_make_obs` and `RaycastSensor.cs` |
| Number of raycasts | root `config.json` → `model.ray_count` (the only knob — no CLI flag) |
| Neural net architecture | `leda/leda-controller/model.py` → `get_policy_kwargs` |
| PPO hyperparameters (lr, n_steps, batch) | Training Controller window → Settings, or `train.py` CLI flags |
| MQTT broker host/port | Training Controller window → Settings (writes to the broker via `LedaBroker.Configure`) |
| Track / obstacles | `SIM/Assets/scripts/ObstacleCourse.cs` + scene assets in `SIM/Assets/Scenes/` |
| Vehicle physics | `SIM/Assets/scripts/VehicleController.cs` and ScriptableObjects in `SIM/Assets/scripts/ScriptableObjects/` |
| Resume from checkpoint | Training Controller window → Settings → "Model File (.zip)" (or `--resume-model` CLI) |
| Health thresholds (obs timeout, reward-collapse ratio) | `SIM/Assets/scripts/Editor/Training/TrainingHealthEvaluator.cs` constants |

## MQTT Topic Conventions

Topics are namespaced under the fixed prefix `env0`:

| Topic | Direction | Payload |
|-------|-----------|---------|
| `env0/leda/control/{throttle,steer,brake,gear,reset}` | Python → Unity | float / int |
| `env0/vehicle/{speed,rpm,...}` | Unity → Python | telemetry floats |
| `env0/vehicle/training/obs` | Unity → Python | observation vector |
| `env0/vehicle/training/reward` | Unity → Python | scalar reward |
| `env0/vehicle/training/done` | Unity → Python | episode-done flag |
| `env0/vehicle/training/episode_end` | Unity → Editor | JSON `{episode, reward, steps, duration, reason}` |
| `env0/vehicle/training/heartbeat` | Unity → Editor | JSON `{value, steps, reward}` (1 Hz unscaled) |

(The `timescale` topic was removed alongside `TimeScaleController.cs`.)

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
