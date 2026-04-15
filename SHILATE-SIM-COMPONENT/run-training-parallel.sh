#!/bin/bash
# run-training-parallel.sh — Launch N Unity headless instances for parallel RL training
#
# Usage:
#   ./run-training-parallel.sh [--num-envs 4] [--timescale 5] [--mqtt-host localhost] [--mqtt-port 1883]

set -euo pipefail

NUM_ENVS="${NUM_ENVS:-4}"
TIMESCALE="${TIMESCALE:-5}"
MQTT_HOST="${MQTT_HOST:-localhost}"
MQTT_PORT="${MQTT_PORT:-1883}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --num-envs)  NUM_ENVS="$2"; shift 2 ;;
        --timescale) TIMESCALE="$2"; shift 2 ;;
        --mqtt-host) MQTT_HOST="$2"; shift 2 ;;
        --mqtt-port) MQTT_PORT="$2"; shift 2 ;;
        *) echo "Unknown arg: $1"; exit 1 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PIDS=()

cleanup() {
    echo "[parallel] Shutting down all Unity instances..."
    for pid in "${PIDS[@]}"; do
        kill "$pid" 2>/dev/null || true
    done
    wait
    echo "[parallel] All instances stopped."
}

trap cleanup EXIT INT TERM

echo "[parallel] Launching $NUM_ENVS Unity headless instances (timescale=$TIMESCALE)"

for ((i=0; i<NUM_ENVS; i++)); do
    echo "[parallel] Starting env$i ..."
    bash "$SCRIPT_DIR/run-training.sh" \
        --env-id "$i" \
        --timescale "$TIMESCALE" \
        --mqtt-host "$MQTT_HOST" \
        --mqtt-port "$MQTT_PORT" &
    PIDS+=($!)
    sleep 2  # Stagger launches to avoid file lock contention
done

echo "[parallel] All $NUM_ENVS instances launched. PIDs: ${PIDS[*]}"
echo "[parallel] Press Ctrl+C to stop all instances."

wait
