#!/usr/bin/env bash
# deploy-pi.sh — Build ARM64 Docker images and deploy SHILATE to Raspberry Pi 4
#
# Usage:
#   ./deploy-pi.sh [feeder|app|controller|all]   (default: all)
#
# Prerequisites:
#   - docker buildx with linux/arm64 support on this host
#   - SSH access: ssh tetrix@10.248.13.115 (ssh-copy-id for passwordless auth)
#   - Docker on the Pi (the script will attempt to install it if absent)
#
# NOTE: Run as your normal user, NOT with sudo. Docker access is via the docker group.
#   Correct:  ./deploy-pi.sh
#   Incorrect: sudo ./deploy-pi.sh  (breaks SSH key lookup)

set -euo pipefail

PI_HOST="10.122.6.115"
PI_USER="tetrix"
PI_SSH="ssh -o StrictHostKeyChecking=no ${PI_USER}@${PI_HOST}"
PI_SCP="scp -o StrictHostKeyChecking=no"
REMOTE_DIR="/home/${PI_USER}/shilate"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

TARGET="${1:-all}"

# ─── Logging helpers ──────────────────────────────────────────────────────

info()  { echo -e "\033[1;34m[INFO]\033[0m  $*"; }
ok()    { echo -e "\033[1;32m[OK]\033[0m    $*"; }
warn()  { echo -e "\033[1;33m[WARN]\033[0m  $*"; }
err()   { echo -e "\033[1;31m[ERROR]\033[0m $*" >&2; }

# ─── Preflight checks ─────────────────────────────────────────────────────

info "Checking SSH connectivity to ${PI_USER}@${PI_HOST} …"
if ! ${PI_SSH} "echo ok" &>/dev/null; then
    err "Cannot reach Pi at ${PI_HOST} via SSH."
    err "Ensure the Pi is on the network and your key is authorised:"
    err "  ssh-copy-id ${PI_USER}@${PI_HOST}"
    exit 1
fi
ok "Pi is reachable"

info "Checking Docker on Pi …"
if ! ${PI_SSH} "command -v docker" &>/dev/null; then
    warn "Docker not found on Pi. Attempting to install …"
    # Debian trixie splits docker into docker.io (daemon), docker-cli (CLI),
    # and docker-compose (compose). All three must be installed explicitly.
    ssh -t ${PI_USER}@${PI_HOST} \
        "sudo apt-get update -qq && \
         sudo apt-get install -y docker.io docker-cli docker-compose && \
         sudo systemctl enable --now docker && \
         sudo usermod -aG docker ${PI_USER}" || {
        err "Automatic Docker installation failed."
        err "Install it manually on the Pi then re-run this script:"
        err "  sudo apt-get install -y docker.io docker-cli docker-compose"
        err "  sudo usermod -aG docker ${PI_USER}"
        exit 1
    }
    ok "Docker installed on Pi"
else
    ok "Docker found on Pi"
fi

# Detect docker compose command (v2 plugin preferred, fall back to v1 binary)
info "Detecting docker compose command on Pi …"
if ${PI_SSH} "docker compose version" &>/dev/null; then
    COMPOSE_CMD="docker compose"
elif ${PI_SSH} "docker-compose version" &>/dev/null; then
    COMPOSE_CMD="docker-compose"
    warn "Using legacy docker-compose (v1). Consider upgrading to docker-compose-plugin."
else
    warn "docker compose not found on Pi. Installing docker-compose-plugin …"
    ${PI_SSH} "sudo apt-get install -y docker-compose-plugin" || {
        err "Could not install docker-compose-plugin. Install it manually:"
        err "  sudo apt-get install -y docker-compose-plugin"
        exit 1
    }
    COMPOSE_CMD="docker compose"
fi
ok "Compose command: ${COMPOSE_CMD}"

# Verify buildx ARM64 support on the host
info "Verifying docker buildx ARM64 support on host …"
if ! docker buildx ls 2>/dev/null | grep -q "linux/arm64"; then
    warn "linux/arm64 not available. Installing QEMU binfmt handlers …"
    docker run --privileged --rm tonistiigi/binfmt --install arm64 &>/dev/null || {
        err "QEMU binfmt install failed. Enable ARM64 support manually:"
        err "  docker run --privileged --rm tonistiigi/binfmt --install arm64"
        exit 1
    }
fi
ok "buildx ready (linux/arm64 emulation active)"

# ─── Build helpers ────────────────────────────────────────────────────────

build_arm64() {
    local name="$1"
    local dir="$2"
    local tag="shilate/${name}:latest"
    shift 2
    local extra_args=("$@")   # any additional buildx flags (e.g. --target)

    info "Building ARM64 image: ${tag} …"
    docker buildx build \
        --platform linux/arm64 \
        --load \
        -t "${tag}" \
        "${extra_args[@]}" \
        "${dir}"
    ok "Built: ${tag}"

    info "Saving ${tag} to /tmp/${name}-arm64.tar …"
    docker save "${tag}" -o "/tmp/${name}-arm64.tar"
    ok "Saved: /tmp/${name}-arm64.tar ($(du -sh "/tmp/${name}-arm64.tar" | cut -f1))"
}

transfer_and_load() {
    local name="$1"
    local tarball="/tmp/${name}-arm64.tar"

    info "Copying ${name} image to Pi …"
    ${PI_SCP} "${tarball}" "${PI_USER}@${PI_HOST}:${REMOTE_DIR}/${name}-arm64.tar"

    info "Loading ${name} image on Pi …"
    ${PI_SSH} "docker load -i ${REMOTE_DIR}/${name}-arm64.tar && rm ${REMOTE_DIR}/${name}-arm64.tar"
    ok "${name} ready on Pi"
}

# ─── Remote directory & config setup ─────────────────────────────────────

setup_remote() {
    info "Creating remote directory structure …"
    ${PI_SSH} "mkdir -p ${REMOTE_DIR}/models"

    info "Writing mosquitto.conf on Pi …"
    ${PI_SSH} "cat > ${REMOTE_DIR}/mosquitto.conf" << 'EOF'
listener 1883
allow_anonymous true
EOF
    ok "mosquitto.conf written"

    info "Copying docker-compose.pi.yml to Pi …"
    ${PI_SCP} "${SCRIPT_DIR}/docker-compose.pi.yml" \
              "${PI_USER}@${PI_HOST}:${REMOTE_DIR}/docker-compose.pi.yml"
    ok "Compose file copied"

    # Copy model: prefer best_model.zip, then the newest checkpoint in models/
    local model_src=""
    if [[ -f "${SCRIPT_DIR}/leda-controller/best_model.zip" ]]; then
        model_src="${SCRIPT_DIR}/leda-controller/best_model.zip"
    else
        model_src="$(ls -t "${SCRIPT_DIR}/leda-controller/models/"*.zip 2>/dev/null | head -1 || true)"
    fi

    if [[ -n "${model_src}" && -f "${model_src}" ]]; then
        info "Copying model: $(basename "${model_src}") → Pi:${REMOTE_DIR}/models/best_model.zip …"
        ${PI_SCP} "${model_src}" "${PI_USER}@${PI_HOST}:${REMOTE_DIR}/models/best_model.zip"
        ok "Model copied"
    else
        warn "No trained model (.zip) found under leda-controller/."
        warn "The leda-controller container will fail to start until a model is placed at:"
        warn "  ${REMOTE_DIR}/models/best_model.zip"
    fi
}

# ─── Compose up ───────────────────────────────────────────────────────────

compose_up() {
    info "Pulling pre-built multi-arch images on Pi (mosquitto, kuksa-databroker) …"
    ${PI_SSH} "${COMPOSE_CMD} -f ${REMOTE_DIR}/docker-compose.pi.yml pull mosquitto kuksa-databroker" || true

    info "Starting all SHILATE services …"
    ${PI_SSH} "${COMPOSE_CMD} -f ${REMOTE_DIR}/docker-compose.pi.yml up -d"
    ok "All services started"

    echo ""
    info "Container status on Pi:"
    ${PI_SSH} "docker ps --format 'table {{.Names}}\t{{.Status}}\t{{.Image}}'" || true
}

# ─── Build / deploy targets ───────────────────────────────────────────────

build_feeder()     { build_arm64 "mqtt-kuksa-feeder"    "${SCRIPT_DIR}/mqtt-kuksa-feeder"; }
build_app()        { build_arm64 "shilate-velocitas-app" "${SCRIPT_DIR}/velocitas-app"; }
build_controller() { build_arm64 "leda-controller"       "${SCRIPT_DIR}/leda-controller" --target full; }

# ─── Main ─────────────────────────────────────────────────────────────────

setup_remote

case "${TARGET}" in
    feeder)
        build_feeder
        transfer_and_load "mqtt-kuksa-feeder"
        ;;
    app)
        build_app
        transfer_and_load "shilate-velocitas-app"
        ;;
    controller)
        build_controller
        transfer_and_load "leda-controller"
        ;;
    all)
        build_feeder
        build_app
        build_controller
        transfer_and_load "mqtt-kuksa-feeder"
        transfer_and_load "shilate-velocitas-app"
        transfer_and_load "leda-controller"
        ;;
    *)
        err "Unknown target: ${TARGET}"
        echo "Usage: $0 [feeder|app|controller|all]"
        exit 1
        ;;
esac

compose_up

echo ""
info "═══════════════════════════════════════════════════════════"
ok   "Deployment complete!"
echo ""
echo "  Full log stream:"
echo "    ssh ${PI_USER}@${PI_HOST} \"${COMPOSE_CMD} -f ${REMOTE_DIR}/docker-compose.pi.yml logs -f\""
echo ""
echo "  Individual service logs:"
echo "    ssh ${PI_USER}@${PI_HOST} \"docker logs shilate-mosquitto\""
echo "    ssh ${PI_USER}@${PI_HOST} \"docker logs shilate-kuksa-databroker\""
echo "    ssh ${PI_USER}@${PI_HOST} \"docker logs shilate-mqtt-kuksa-feeder\""
echo "    ssh ${PI_USER}@${PI_HOST} \"docker logs shilate-velocitas-app\""
echo "    ssh ${PI_USER}@${PI_HOST} \"docker logs shilate-leda-controller\""
echo ""
echo "  Stop everything:"
echo "    ssh ${PI_USER}@${PI_HOST} \"${COMPOSE_CMD} -f ${REMOTE_DIR}/docker-compose.pi.yml down\""
info "═══════════════════════════════════════════════════════════"
