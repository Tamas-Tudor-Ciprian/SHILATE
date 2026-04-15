#!/bin/bash
# run-training.sh — Launch a single Unity headless instance for training
#
# Usage:
#   ./run-training.sh [--env-id 0] [--timescale 5] [--mqtt-host localhost] [--mqtt-port 1883]
#
# Requires Unity installed and accessible via the UNITY_PATH environment variable,
# or defaults to the standard Unity Hub install location.

set -euo pipefail

ENV_ID="${ENV_ID:-0}"
TIMESCALE="${TIMESCALE:-5}"
MQTT_HOST="${MQTT_HOST:-localhost}"
MQTT_PORT="${MQTT_PORT:-1883}"

# Parse CLI overrides
while [[ $# -gt 0 ]]; do
    case "$1" in
        --env-id)    ENV_ID="$2"; shift 2 ;;
        --timescale) TIMESCALE="$2"; shift 2 ;;
        --mqtt-host) MQTT_HOST="$2"; shift 2 ;;
        --mqtt-port) MQTT_PORT="$2"; shift 2 ;;
        *) echo "Unknown arg: $1"; exit 1 ;;
    esac
done

# Find Unity executable
if [[ -n "${UNITY_PATH:-}" ]]; then
    UNITY="$UNITY_PATH"
elif [[ -x "/c/Program Files/Unity/Hub/Editor/6000.3.12f1/Editor/Unity.exe" ]]; then
    UNITY="/c/Program Files/Unity/Hub/Editor/6000.3.12f1/Editor/Unity.exe"
else
    echo "ERROR: Set UNITY_PATH to your Unity editor executable" >&2
    exit 1
fi

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "[run-training] Launching Unity headless (env-id=$ENV_ID, timescale=$TIMESCALE, mqtt=$MQTT_HOST:$MQTT_PORT)"

"$UNITY" \
    -batchmode \
    -nographics \
    -projectPath "$PROJECT_DIR" \
    -executeMethod TrainingBootstrap.Launch \
    --env-id "$ENV_ID" \
    --timescale "$TIMESCALE" \
    --mqtt-host "$MQTT_HOST" \
    --mqtt-port "$MQTT_PORT" \
    -logFile "$PROJECT_DIR/Logs/training-env${ENV_ID}.log"
