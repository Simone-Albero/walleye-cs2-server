# ── WallEye CS2 Server — Makefile ────────────────────────────────────────────
# Run `make` (or `make help`) to see all available targets.
# ─────────────────────────────────────────────────────────────────────────────

# Use bash explicitly: the setup target uses [[ ]] which is bash-only
# and not supported by /bin/sh (dash) on Ubuntu.
SHELL := /bin/bash

.DEFAULT_GOAL := help

.PHONY: help setup admin-auth start up down stop build update rebuild restart \
	apply-config logs logs-all admin-logs status shell \
        reset-download clean

START_SCRIPT := ./start.sh
ADMIN_AUTH_SCRIPT := ./generate_admin_auth.py
ADMIN_AUTH_FILE ?= admin-auth.json
ADMIN_AUTH_ARGS ?=
COMPOSE    := docker compose
# Container names (used for docker rm -f in clean)
CONTAINERS := cs2-server cs2-scorer cs2-web cs2-admin cs2-base
# Compose service names used for selective build/up
APP_SERVICES := cs2-server scoring-service web-leaderboard admin-panel

SERVER_PORT := $(shell jq -r '.server.port // 27015' config.json 2>/dev/null || echo 27015)
WEB_PORT   := 8080
ADMIN_PORT := 8081
SERVER_IP  := $(shell hostname -I | awk '{print $$1}')

# ── Help ──────────────────────────────────────────────────────────────────────
help:
	@echo ""
	@echo "  WallEye CS2 Server"
	@echo ""
	@echo "  ── First time ────────────────────────────────────────────────────"
	@echo "  make setup          Copy admins.conf template + validate config"
	@echo "  make admin-auth     Generate admin-auth.json interactively"
	@echo "  make start          Build images, run cs2-base if needed, start everything"
	@echo ""
	@echo "  ── Day-to-day ────────────────────────────────────────────────────"
	@echo "  make up             Resume the full stack with the safe bootstrap flow"
	@echo "  make down           Stop and remove all containers (data preserved)"
	@echo "  make restart        Restart running services in place (no rebuild)"
	@echo "  make status         Show container status + service URLs"
	@echo ""
	@echo "  ── After code changes ────────────────────────────────────────────"
	@echo "  make build          (Re)build all images without starting them"
	@echo "  make update         Incremental rebuild + safe restart of the full stack"
	@echo ""
	@echo "  ── After config.json changes ─────────────────────────────────────"
	@echo "  make apply-config   Validate + recreate affected containers without rebuilding"
	@echo ""
	@echo "  ── Debugging ─────────────────────────────────────────────────────"
	@echo "  make logs           Tail game-server logs"
	@echo "  make logs-all       Tail logs for all services"
	@echo "  make admin-logs     Tail admin-panel logs"
	@echo "  make shell          Open bash shell inside cs2-server"
	@echo ""
	@echo "  ── Maintenance ───────────────────────────────────────────────────"
	@echo "  make rebuild        Force full no-cache rebuild + restart"
	@echo "  make reset-download Force CS2 re-download on next 'make start'"
	@echo "  make clean          Force-remove stale containers"
	@echo ""

# ── Setup (first-time helper) ─────────────────────────────────────────────────
setup:
	@if [[ ! -f cs2_main_image/admins.conf ]]; then \
	    echo "[WallEye] Creating admins.conf from template..."; \
	    cp cs2_main_image/admins.conf.template cs2_main_image/admins.conf; \
	    echo "[WallEye] Edit cs2_main_image/admins.conf (format: <name> <steamid64>)"; \
	fi
	@echo "[WallEye] Validating config.json..."
	@jq empty config.json && echo "[WallEye] config.json OK" \
	    || (echo "[WallEye] ERROR: config.json is invalid JSON" && exit 1)
	@echo "[WallEye] Setup complete. Run 'make admin-auth' and then 'make start'."

# ── Generate admin-auth.json ─────────────────────────────────────────────────
admin-auth:
	@echo "[WallEye] Generating $(ADMIN_AUTH_FILE)..."
	@python3 $(ADMIN_AUTH_SCRIPT) $(ADMIN_AUTH_ARGS) --write "$(ADMIN_AUTH_FILE)"
	@echo "[WallEye] Wrote $(ADMIN_AUTH_FILE)."

# ── Start (first run or resume) ───────────────────────────────────────────────
# Delegates to start.sh which handles the CS2 one-time download wait.
start:
	@bash $(START_SCRIPT)

# ── Up (resume through the same safe flow used by start) ──────────────────────
up:
	@bash $(START_SCRIPT)

# ── Down (stop + remove containers, preserve volumes) ─────────────────────────
down:
	@echo "[WallEye] AgN3kwskdn wklskdnslfnas grrrrrrrrrrrrrrrrrrrr!"
	@echo "[WallEye] Sorry bad joke, but seriously..."
	@echo "[WallEye] Stopping all services..."
	@$(COMPOSE) down --remove-orphans
	@echo "[WallEye] Done."

# ── Stop (alias for down) ──────────────────────────────────────────────────────
stop: down

# ── Build (build/rebuild images without starting them) ────────────────────────
build:
	@echo "[WallEye] Building images..."
	@$(COMPOSE) build cs2-base
	@$(COMPOSE) build $(APP_SERVICES)
	@echo "[WallEye] Images built. Run 'make up' to start."

# ── Update (incremental: rebuild changed images, restart affected services) ───
# Use this after pulling new code. Docker layer caching means only changed
# layers are rebuilt; unchanged services restart quickly.
update:
	@bash $(START_SCRIPT)

# ── Restart (restart running containers in place, no rebuild) ─────────────────
restart:
	@echo "[WallEye] Restarting services..."
	@$(COMPOSE) restart cs2-server scoring-service web-leaderboard admin-panel
	@echo "[WallEye] Done."
	@$(MAKE) --no-print-directory status

# ── Rebuild (full no-cache rebuild + restart) ─────────────────────────────────
rebuild:
	@bash $(START_SCRIPT) --rebuild

# ── Reset CS2 download sentinel (forces re-download on next start) ────────────
reset-download:
	@bash $(START_SCRIPT) --reset-download

# ── Apply config.json changes ─────────────────────────────────────────────────
# config.json is bind-mounted into containers; a restart is all that is needed
# to pick up new values. No rebuild required.
apply-config:
	@echo "[WallEye] Validating config.json..."
	@jq empty config.json || (echo "[WallEye] ERROR: invalid JSON in config.json" && exit 1)
	@echo "[WallEye] Recreating services to apply config..."
	@$(COMPOSE) up -d --no-deps cs2-server scoring-service web-leaderboard admin-panel
	@echo "[WallEye] Config applied."
	@$(MAKE) --no-print-directory status

# ── Logs ──────────────────────────────────────────────────────────────────────
logs:
	@$(COMPOSE) logs -f cs2-server

logs-all:
	@$(COMPOSE) logs -f cs2-server scoring-service web-leaderboard admin-panel

admin-logs:
	@$(COMPOSE) logs -f admin-panel

# ── Shell ─────────────────────────────────────────────────────────────────────
shell:
	@$(COMPOSE) exec cs2-server bash

# ── Status ────────────────────────────────────────────────────────────────────
status:
	@echo ""
	@echo "── Container status ─────────────────────────────────────────"
	@$(COMPOSE) ps
	@echo ""
	@echo "  CS2 server      : connect $(SERVER_IP):$(SERVER_PORT)"
	@echo "  Web leaderboard : http://$(SERVER_IP):$(WEB_PORT)"
	@echo "  Admin panel     : http://$(SERVER_IP):$(ADMIN_PORT) (login required)"
	@echo ""

# ── Clean (force-remove stale containers without stopping gracefully) ─────────
clean:
	@echo "[WallEye] Removing stale containers..."
	@$(COMPOSE) down --remove-orphans >/dev/null 2>&1 || true
	@docker rm -f $(CONTAINERS) 2>/dev/null || true
	@echo "[WallEye] Done. Run 'make start' to restart."
