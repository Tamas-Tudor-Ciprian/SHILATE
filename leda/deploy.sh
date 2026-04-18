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
    local image_tag="shilate/${name}:latest"

    info "Building image: ${image_tag}"
    docker build -t "${image_tag}" "${dir}"
    ok "Image built: ${image_tag}"

    info "Exporting image to tarball …"
    docker save "${image_tag}" -o "/tmp/${name}.tar"

    info "Copying image to Leda VM …"
    ${LEDA_SCP} "/tmp/${name}.tar" "root@localhost:/tmp/${name}.tar"

    info "Importing image into containerd on Leda …"
    ${LEDA_SSH} "ctr --namespace kanto-cm images import /tmp/${name}.tar && rm /tmp/${name}.tar"
    ok "Image imported on Leda"

    info "Stopping existing container (if any) …"
    ${LEDA_SSH} "kanto-cm stop   --name ${name} 2>/dev/null || true"
    ${LEDA_SSH} "kanto-cm remove --name ${name} 2>/dev/null || true"

    # Build --e flags from the manifest's config.env array
    local env_flags=""
    if command -v jq &>/dev/null && [[ -f "${dir}/kanto-manifest.json" ]]; then
        while IFS= read -r envvar; do
            env_flags+=" --e=${envvar}"
        done < <(jq -r '.config.env[]? // empty' "${dir}/kanto-manifest.json")
    fi

    info "Creating and starting container via Kanto …"
    ${LEDA_SSH} "kanto-cm create \
        --name ${name} \
        --network=host \
        --rp=unless-stopped \
        --log-driver=json-file \
        --log-max-files=3 \
        --log-max-size=5M \
        ${env_flags} \
        docker.io/${image_tag}"
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

# ─── Main ─────────────────────────────────────────────────────────────────

info "Checking SSH connectivity to Leda …"
if ! ${LEDA_SSH} "echo ok" &>/dev/null; then
    err "Cannot reach Leda via SSH on localhost:2222."
    err "Make sure the Leda QEMU instance is running (run-leda.sh/cmd)."
    exit 1
fi
ok "Leda is reachable"

case "${TARGET}" in
    feeder) deploy_feeder ;;
    app)    deploy_app    ;;
    all)    deploy_feeder
            deploy_app    ;;
    *)
        err "Unknown target: ${TARGET}"
        echo "Usage: $0 [feeder|app|all]"
        exit 1
        ;;
esac

echo ""
info "═══════════════════════════════════════════════════════════"
ok   "Deployment complete!  Verify with:"
echo "  ${LEDA_SSH} \"kanto-cm list\""
echo "  ${LEDA_SSH} \"kanto-cm logs --name mqtt-kuksa-feeder\""
echo "  ${LEDA_SSH} \"kanto-cm logs --name shilate-velocitas-app\""
info "═══════════════════════════════════════════════════════════"
