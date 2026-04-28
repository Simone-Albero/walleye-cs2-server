#!/bin/bash
set -euo pipefail

echo "[WallEye] Waiting for cs2-base to finish..."
while [ ! -f /home/steam/steamcmd/.cs2_base_done ]; do sleep 2; done
# Note: do NOT remove the marker — it must persist on the volume so that
# subsequent cs2-server restarts (make apply-config, auto-restart, etc.)
# can skip this wait without hanging.
echo "[WallEye] cs2-base ready."

CSGO_STRING="Counter-Strike Global Offensive"
LAUNCH_DIR="/home/steam/Steam/steamapps/common/$CSGO_STRING/game"
SAFE_LAUNCH_DIR="/home/steam/cs2-game"
CSGO_DIR="$LAUNCH_DIR/csgo"
CFG_DIR="$CSGO_DIR/cfg"
CSS_PLUGINS="$CSGO_DIR/addons/counterstrikesharp/plugins"
CSS_CONFIGS="$CSGO_DIR/addons/counterstrikesharp/configs"
ADMINS_JSON="$CSGO_DIR/addons/counterstrikesharp/configs/admins.json"

# Shared volume data directories
mkdir -p /data/replays /data/reports

# Addons (MetaMod + CounterStrikeSharp baseline only)
if [[ ! -d /home/steam/download/addons ]]; then
    echo "[WallEye] ERROR: /home/steam/download/addons not found. Docker image build likely failed to extract MetaMod or CounterStrikeSharp."
    exit 1
fi
rm -rf "$CSGO_DIR/addons"
cp -r /home/steam/download/addons "$CSGO_DIR"

if [[ ! -d "$CSGO_DIR/addons/metamod" ]] || ! find "$CSGO_DIR/addons/metamod" -name server.so -print -quit | grep -q .; then
    echo "[WallEye] ERROR: MetaMod was not installed under $CSGO_DIR/addons/metamod."
    exit 1
fi

mkdir -p "$CSS_PLUGINS"
mkdir -p "$CSS_CONFIGS"

EXPECTED_PLUGIN_FILES=(
    "$CSS_PLUGINS/WallEyeServer/WallEyeServer.dll"
)

for plugin_file in "${EXPECTED_PLUGIN_FILES[@]}"; do
    if [[ ! -f "$plugin_file" ]]; then
        echo "[WallEye] ERROR: Expected CounterStrikeSharp plugin artifact missing: $plugin_file"
        exit 1
    fi
done

echo "[WallEye] Found expected plugin artifacts: WallEyeServer."

# CounterStrikeSharp chat triggers:
#   PublicChatTrigger is echoed in global chat.
#   SilentChatTrigger executes commands without showing the typed command.
CSS_CORE_JSON="$CSS_CONFIGS/core.json"
if [[ -f "$CSS_CORE_JSON" ]]; then
    tmp_core="$(mktemp)"
    jq '.PublicChatTrigger = [] | .SilentChatTrigger = ((.SilentChatTrigger // []) + ["!", "/"] | unique)' \
        "$CSS_CORE_JSON" > "$tmp_core"
    mv "$tmp_core" "$CSS_CORE_JSON"
else
    cat > "$CSS_CORE_JSON" <<'JSON'
{
  "PublicChatTrigger": [],
  "SilentChatTrigger": ["!", "/"],
  "FollowCS2ServerGuidelines": true,
  "PluginHotReloadEnabled": true,
  "PluginAutoLoadEnabled": true,
  "ServerLanguage": "en"
}
JSON
fi
echo "[WallEye] CounterStrikeSharp chat commands are silent for ! and /."

# Config
cp /home/steam/autoexec.cfg "$CFG_DIR/"
cp /home/steam/server.cfg   "$CFG_DIR/"

# Generate admins.json from admins.conf
# Uses jq to build the JSON incrementally, correctly handling:
#   - blank lines (skipped)
#   - comment lines starting with # (skipped)
#   - files with or without a trailing newline
ADMINS_CONF=/home/steam/admins.conf
ADMINS_DATA="{}"
while IFS= read -r line || [[ -n "$line" ]]; do
    # Strip leading whitespace
    line="${line#"${line%%[![:space:]]*}"}"
    # Skip blank lines and comments
    [[ -z "$line" || "${line:0:1}" == "#" ]] && continue
    read -r -a parts <<< "$line"
    [[ ${#parts[@]} -lt 2 ]] && continue
    ADMINS_DATA=$(printf '%s' "$ADMINS_DATA" | jq \
        --arg u "${parts[0]}" --arg s "${parts[1]}" \
        '.[$u] = {"identity": $s, "flags": ["@css/ban"]}')
done < "$ADMINS_CONF"
mkdir -p "$(dirname "$ADMINS_JSON")"
printf '%s\n' "$ADMINS_DATA" > "$ADMINS_JSON"
echo "[WallEye] Generated $(printf '%s' "$ADMINS_DATA" | jq 'keys | length') admin entries."

# MetaMod in gameinfo.gi
if ! grep -q metamod "$CSGO_DIR/gameinfo.gi" 2>/dev/null; then
    sed -ie '/Game_LowViolence/a\\n\t\t\tGame    csgo/addons/metamod' "$CSGO_DIR/gameinfo.gi"
fi

# Symlink replay → shared volume
rm -rf "$CSGO_DIR/replays"
ln -sf /data/replays "$CSGO_DIR/replays"

# Read parameters from config.json (mounted at /config/config.json)
SERVER_PORT=$(jq -r '.server.port'        /config/config.json)
MAP=$(jq -r         '.match.map // "de_dust2"' /config/config.json 2>/dev/null || echo "de_dust2")
SERVER_NAME=$(jq -r '.server.server_name' /config/config.json)
STEAM_ACCOUNT_TOKEN=$(jq -r '.server.steam_account_token // empty' /config/config.json)
INSECURE=$(jq -r    '.server.insecure // true' /config/config.json)
AUTOUPDATE=$(jq -r  '.server.autoupdate // true' /config/config.json)

if [[ "${SKIP_SERVER_START:-false}" == "true" ]]; then
    echo "[WallEye] Bootstrap complete; skipping CS2 start because SKIP_SERVER_START=true."
    exit 0
fi

# Launch through a path without spaces because cs2.sh mishandles unquoted $0.
ln -sfn "$LAUNCH_DIR" "$SAFE_LAUNCH_DIR"

# Build launch arguments with arrays so empty/optional values do not corrupt argv.
LAUNCH_ARGS=(
    +exec autoexec.cfg
    +exec server.cfg
    -dedicated
)
[[ "$INSECURE" == "true" ]] && LAUNCH_ARGS+=(-insecure)
[[ "$AUTOUPDATE" == "true" ]] && LAUNCH_ARGS+=(-autoupdate)
LAUNCH_ARGS+=(
    -port "$SERVER_PORT"
    +map "$MAP"
    +game_alias competitive
)
[[ -n "$STEAM_ACCOUNT_TOKEN" ]] && LAUNCH_ARGS+=(+sv_setsteamaccount "$STEAM_ACCOUNT_TOKEN")
LAUNCH_ARGS+=(+hostname "$SERVER_NAME")

echo "[WallEye] Starting CS2 server on port $SERVER_PORT, map $MAP..."
cd "$SAFE_LAUNCH_DIR"

STARTUP_LOG=/tmp/cs2-startup.log
STARTUP_TIMEOUT_SECONDS=${STARTUP_TIMEOUT_SECONDS:-90}
rm -f "$STARTUP_LOG"
touch "$STARTUP_LOG"

stdbuf -oL -eL "$SAFE_LAUNCH_DIR/cs2.sh" "${LAUNCH_ARGS[@]}" 2>&1 | tee -a "$STARTUP_LOG" &
SERVER_PID=$!

startup_deadline=$((SECONDS + STARTUP_TIMEOUT_SECONDS))
while (( SECONDS < startup_deadline )); do
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "[WallEye] ERROR: CS2 exited before startup checks completed. Last startup log lines:"
        tail -n 80 "$STARTUP_LOG" || true
        wait "$SERVER_PID"
        exit 1
    fi

    if grep -q "\[META\] Failed to load plugin" "$STARTUP_LOG"; then
        echo "[WallEye] ERROR: MetaMod reported a plugin load failure during startup."
        tail -n 80 "$STARTUP_LOG" || true
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" || true
        exit 1
    fi

    if grep -q "CounterStrikeSharp.API Loaded Successfully\." "$STARTUP_LOG" && \
       grep -q "\[META\] Loaded 1 plugin\." "$STARTUP_LOG" && \
       grep -q "Finished loading plugin WallEyeServer" "$STARTUP_LOG"; then
        echo "[WallEye] Startup check passed: MetaMod, CounterStrikeSharp, and WallEyeServer are active."
        wait "$SERVER_PID"
        exit $?
    fi

    sleep 1
done

echo "[WallEye] ERROR: Timed out after ${STARTUP_TIMEOUT_SECONDS}s waiting for MetaMod/CounterStrikeSharp startup confirmation."
tail -n 80 "$STARTUP_LOG" || true
kill "$SERVER_PID" 2>/dev/null || true
wait "$SERVER_PID" || true
exit 1
