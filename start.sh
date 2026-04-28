#!/usr/bin/env bash
# ── WallEye CS2 Server — server startup script ───────────────────────────────
# Called by the Makefile. Do not invoke directly unless you know what you're doing.
#
# Usage:
#   ./start.sh              — first run: build images, download CS2, start all services
#   ./start.sh --rebuild    — force full no-cache rebuild and restart
#   ./start.sh --reset-download — force cs2-base to run again on the next start
#
# For day-to-day operations use: make help
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"
STEAMCMD_VOLUME="walleye-cs2-server_steamcmd_volume"
STEAM_LOGIN_VOLUME="walleye-cs2-server_steamcmd_login_volume"
LEGACY_SENTINEL="$SCRIPT_DIR/.cs2_downloaded"
FORCE_CS2_BASE_FILE="$SCRIPT_DIR/.force_cs2_base"

# ── Colours ───────────────────────────────────────────────────────────────────
RED='\033[0;31m'; YELLOW='\033[1;33m'; GREEN='\033[0;32m'
CYAN='\033[0;36m'; BOLD='\033[1m'; RESET='\033[0m'

info()    { echo -e "${CYAN}[WallEye]${RESET} $*"; }
success() { echo -e "${GREEN}[WallEye]${RESET} $*"; }
warn()    { echo -e "${YELLOW}[WallEye]${RESET} $*"; }
error()   { echo -e "${RED}[WallEye] ERROR:${RESET} $*" >&2; }

# ── Helpers ───────────────────────────────────────────────────────────────────

check_deps() {
    local missing=()
    command -v docker  &>/dev/null || missing+=("docker")
    command -v jq      &>/dev/null || missing+=("jq")
    if [[ ${#missing[@]} -gt 0 ]]; then
        error "Missing dependencies: ${missing[*]}"
        error "Install with: sudo apt install ${missing[*]}"
        exit 1
    fi
    # Docker Compose v2 (plugin) or v1 (standalone)
    if ! docker compose version &>/dev/null 2>&1; then
        error "Docker Compose v2 not found. Install the docker-compose-plugin package."
        exit 1
    fi
}

check_config() {
    if [[ ! -f config.json ]]; then
        error "config.json not found. Cannot start."
        exit 1
    fi
    # Validate JSON
    if ! jq empty config.json &>/dev/null; then
        error "config.json is not valid JSON. Fix it before starting."
        exit 1
    fi
    local port web_port server_ip
    port="$(jq -r '.server.port // 27015' config.json)"
    web_port="$(jq -r '.server.web_leaderboard_port // 8080' config.json)"
    server_ip="$(hostname -I | awk '{print $1}')"
    info "CS2 server     : ${BOLD}${server_ip}:${port}/UDP${RESET}"
    info "Web leaderboard: ${BOLD}http://${server_ip}:${web_port}${RESET}"
    info "Admin panel    : ${BOLD}http://${server_ip}:8081${RESET}"
}

wait_for_cs2_base() {
    info "Waiting for cs2-base to finish downloading CS2 (this takes ~15-20 min on first run)..."
    info "Showing cs2-base logs — will continue automatically when done:"
    echo
    # Stream logs in background, kill the tail when the container stops
    docker compose logs -f cs2-base &
    local logs_pid=$!
    # Poll until container is no longer running
    while [[ "$(docker inspect --format='{{.State.Status}}' cs2-base 2>/dev/null)" == "running" ]]; do
        sleep 3
    done
    # Give logs a moment to flush then kill the background tail
    sleep 2
    kill "$logs_pid" 2>/dev/null || true
    wait "$logs_pid" 2>/dev/null || true
    echo
    # Verify the container exited cleanly
    local exit_code
    exit_code="$(docker inspect --format='{{.State.ExitCode}}' cs2-base 2>/dev/null || echo 1)"
    if [[ "$exit_code" != "0" ]]; then
        error "cs2-base exited with code ${exit_code}. Check logs: docker compose logs cs2-base"
        exit 1
    fi
    # Clear any manual re-download request and remove the old host-side sentinel.
    rm -f "$FORCE_CS2_BASE_FILE" "$LEGACY_SENTINEL"
    success "cs2-base finished successfully."
}

print_status() {
    echo
    echo -e "${BOLD}── Service status ────────────────────────────────────────${RESET}"
    docker compose ps
    echo
}

ensure_volumes() {
    local volumes=(
        "$STEAMCMD_VOLUME"
        "$STEAM_LOGIN_VOLUME"
    )

    for volume in "${volumes[@]}"; do
        if ! docker volume inspect "$volume" >/dev/null 2>&1; then
            info "Creating Docker volume ${BOLD}${volume}${RESET}..."
            docker volume create "$volume" >/dev/null
        fi
    done
}

cs2_base_marker_exists() {
    docker image inspect base-cs2-image >/dev/null 2>&1 || return 1
    docker run --rm \
        -v "${STEAMCMD_VOLUME}:/steamcmd" \
        --entrypoint bash \
        base-cs2-image \
        -lc 'test -f /steamcmd/.cs2_base_done'
}

build_images() {
    local rebuild="${1:-false}"

    if [[ "$rebuild" == "true" ]]; then
        docker compose build --no-cache cs2-base
        docker compose build --no-cache cs2-server scoring-service web-leaderboard admin-panel
    else
        docker compose build cs2-base
        docker compose build cs2-server scoring-service web-leaderboard admin-panel
    fi
}

# ── Modes ─────────────────────────────────────────────────────────────────────

do_reset_download() {
    warn "Forcing cs2-base on the next start."
    touch "$FORCE_CS2_BASE_FILE"
    rm -f "$LEGACY_SENTINEL"
    success "cs2-base will run again on the next 'make start'."
}

do_start() {
    local rebuild="${1:-false}"

    check_deps
    check_config
    ensure_volumes

    echo
    echo -e "${BOLD}══════════════════════════════════════════════════════════${RESET}"
    echo -e "${BOLD}            WallEye CS2 Server — Starting                 ${RESET}"
    echo -e "${BOLD}══════════════════════════════════════════════════════════${RESET}"
    echo

    # ── Step 1: Build images ──────────────────────────────────────────────────
    if [[ "$rebuild" == "true" ]]; then
        info "Step 1/4 — Building all Docker images (--no-cache)..."
    else
        info "Step 1/4 — Building Docker images (incremental)..."
    fi
    build_images "$rebuild"
    success "Images built."
    echo

    # ── Step 2: Download CS2 (cs2-base, runs once per volume state) ──────────
    if [[ "$rebuild" != "true" ]] && [[ ! -f "$FORCE_CS2_BASE_FILE" ]] && cs2_base_marker_exists; then
        info "Step 2/4 — CS2 base marker found in Docker volume. Skipping cs2-base."
    else
        info "Step 2/4 — Running cs2-base to download CS2..."
        # Force-remove any stale container (works regardless of state)
        docker rm -f cs2-base 2>/dev/null || true
        # Run detached so this script keeps control; then stream logs + poll
        docker compose up -d cs2-base
        wait_for_cs2_base
    fi
    echo

    # ── Step 3: Create data directories ──────────────────────────────────────
    info "Step 3/4 — Ensuring data directories exist..."
    mkdir -p data/replays data/reports
    success "data/replays and data/reports ready."
    echo

    # ── Step 4: Start main services ───────────────────────────────────────────
    info "Step 4/4 — Starting cs2-server, scoring-service, web-leaderboard, admin-panel..."
    docker compose up -d cs2-server scoring-service web-leaderboard admin-panel
    echo

    # ── Done ──────────────────────────────────────────────────────────────────
    print_status

    local web_port
    web_port="$(jq -r '.server.web_leaderboard_port // 8080' config.json)"
    local server_ip
    server_ip="$(hostname -I | awk '{print $1}')"

    echo -e "${BOLD}══════════════════════════════════════════════════════════${RESET}"
    success "Stack is up!"
    echo
    echo -e "  Web leaderboard : ${CYAN}http://${server_ip}:${web_port}${RESET}"
    echo -e "  Admin panel     : ${CYAN}http://${server_ip}:8081${RESET}"
    echo -e "  CS2 server logs : ${CYAN}make logs${RESET}"
    echo -e "  All logs        : ${CYAN}make logs-all${RESET}"
    echo -e "  Stop all        : ${CYAN}make down${RESET}"
    echo -e "${BOLD}══════════════════════════════════════════════════════════${RESET}"
    echo
}

# ── Entry point ───────────────────────────────────────────────────────────────

MODE="${1:-}"

case "$MODE" in
    --rebuild)        do_start true ;;
    --reset-download) do_reset_download ;;
    "")               do_start false ;;
    *)
        echo -e "Usage: $0 [--rebuild|--reset-download]"
        echo -e "For all other operations, use: make help"
        exit 1
        ;;
esac
