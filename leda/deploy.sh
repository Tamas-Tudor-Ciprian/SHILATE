#!/usr/bin/env bash
# deploy.sh — Build and deploy SHILATE containers to Eclipse Leda via Kanto
#
# Usage:
#   ./deploy.sh [feeder|app|all]    (default: all)
#
# Prerequisites:
#   - Docker (or podman) on the host for building images
#   - Leda QEMU instance running and reachable via SSH on port 2222
#   - kanto-cm available inside the Leda VM

set -euo pipefail

LEDA_SSH="ssh -o StrictHostKeyChecking=no -p 2222 root@localhost"
LEDA_SCP="scp -o StrictHostKeyChecking=no -P 2222"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

TARGET="${1:-all}"

# ─── Helpers ──────────────────────────────────────────────────────────────

info()  { echo -e "\033[1;34m[INFO]\033[0m  $*"; }
ok()    { echo -e "\033[1;32m[OK]\033[0m    $*"; }
err()   { echo -e "\033[1;31m[ERROR]\033[0m $*" >&2; }

build_and_deploy() {
    local name="$1"
    local dir="$2"
    local docker_target="${3:-}"          # optional --target for multi-stage
    local image_tag="localhost/shilate/${name}:latest"

    info "Building image: ${image_tag}"
    if [[ -n "${docker_target}" ]]; then
        docker build --target "${docker_target}" -t "${image_tag}" "${dir}"
    else
        docker build -t "${image_tag}" "${dir}"
    fi
    ok "Image built: ${image_tag}"

    info "Streaming image directly into containerd on Leda …"
    docker save "${image_tag}" | ${LEDA_SSH} "ctr --namespace kanto-cm images import -"
    ok "Image imported on Leda"

    info "Copying Kanto manifest …"
    ${LEDA_SCP} "${dir}/kanto-manifest.json" "root@localhost:/tmp/${name}-manifest.json"

    info "Stopping existing container (if any) …"
    ${LEDA_SSH} "kanto-cm stop   --force --name ${name} 2>/dev/null || true"
    ${LEDA_SSH} "kanto-cm remove --force --name ${name} 2>/dev/null || true"

    info "Reading env vars from manifest …"
    local env_flags=""
    env_flags=$(python3 -c "
import json, sys
with open('${dir}/kanto-manifest.json') as f:
    m = json.load(f)
for e in m.get('config', {}).get('env', []):
    print(f'--e={e}')
" | tr '\n' ' ')

    info "Creating and starting container via Kanto …"
    ${LEDA_SSH} "kanto-cm create --name ${name} --network=host --rp=unless-stopped ${env_flags} ${image_tag}"
    ${LEDA_SSH} "kanto-cm start --name ${name}"
    ok "Container '${name}' deployed and running on Leda"
}

# ─── Deploy targets ──────────────────────────────────────────────────────

deploy_feeder() {
    build_and_deploy "mqtt-kuksa-feeder" "${SCRIPT_DIR}/mqtt-kuksa-feeder"
}

deploy_app() {
    build_and_deploy "shilate-velocitas-app" "${SCRIPT_DIR}/velocitas-app"
}

deploy_controller() {
    build_and_deploy "shilate-leda-controller" "${SCRIPT_DIR}/leda-controller" "full"
}

deploy_controller_debug() {
    build_and_deploy "shilate-leda-controller-debug" "${SCRIPT_DIR}/leda-controller" "debug"
}

# ─── Main ─────────────────────────────────────────────────────────────────

info "Checking SSH connectivity to Leda …"
if ! ${LEDA_SSH} "echo ok" &>/dev/null; then
    err "Cannot reach Leda via SSH on localhost:2222."
    err "Make sure the Leda QEMU instance is running (run-leda.sh/cmd)."
    exit 1
fi
ok "Leda is reachable"

case "${TARGET}" in
    feeder)           deploy_feeder           ;;
    app)              deploy_app              ;;
    controller)       deploy_controller       ;;
    controller-debug) deploy_controller_debug ;;
    all)              deploy_feeder
                      deploy_app
                      deploy_controller       ;;
    *)
        err "Unknown target: ${TARGET}"
        echo "Usage: $0 [feeder|app|controller|controller-debug|all]"
        exit 1
        ;;
esac

echo ""
info "═══════════════════════════════════════════════════════════"
ok   "Deployment complete!  Verify with:"
echo "  ${LEDA_SSH} \"kanto-cm list\""
echo "  ${LEDA_SSH} \"kanto-cm logs --name mqtt-kuksa-feeder\""
echo "  ${LEDA_SSH} \"kanto-cm logs --name shilate-velocitas-app\""
echo "  ${LEDA_SSH} \"kanto-cm logs --name shilate-leda-controller\""
info "═══════════════════════════════════════════════════════════"
