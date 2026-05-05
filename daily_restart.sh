#!/usr/bin/env bash
# daily_restart.sh — Archive replay files + JSON data, then perform a full server rebuild.
# Intended to be run by the walleye-daily-restart.timer systemd unit.
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA_DIR="$PROJECT_DIR/data"
REPLAYS_DIR="$DATA_DIR/replays"

# Use Europe/Rome timezone for the archive folder name so the date always
# reflects the user's local date regardless of the server's timezone.
DATE="$(TZ=Europe/Rome date +%Y-%m-%d)"
ARCHIVE_DIR="$REPLAYS_DIR/$DATE"

echo "[WallEye] daily_restart.sh — $(TZ=Europe/Rome date)"

# ── 1. Archive replay files ───────────────────────────────────────────────────
shopt -s nullglob
dem_files=("$REPLAYS_DIR"/*.dem)
shopt -u nullglob

if [[ ${#dem_files[@]} -gt 0 ]]; then
    echo "[WallEye] Moving ${#dem_files[@]} replay(s) to $ARCHIVE_DIR ..."
    mkdir -p "$ARCHIVE_DIR"
    mv "${dem_files[@]}" "$ARCHIVE_DIR/"
    echo "[WallEye] Replays archived."
else
    echo "[WallEye] No replay files found, skipping archive step."
fi

# ── 2. Copy JSON data files (excluding bug_reports.json and status.json) ─────
json_copied=0
for json_file in "$DATA_DIR"/*.json; do
    basename="${json_file##*/}"
    if [[ "$basename" == "bug_reports.json" || "$basename" == "status.json" ]]; then
        continue
    fi
    mkdir -p "$ARCHIVE_DIR"
    cp "$json_file" "$ARCHIVE_DIR/$basename"
    (( json_copied++ )) || true
done

if [[ $json_copied -gt 0 ]]; then
    echo "[WallEye] Copied $json_copied JSON file(s) to $ARCHIVE_DIR."
else
    echo "[WallEye] No JSON files to copy."
fi

# ── 3. Copy reports folder ────────────────────────────────────────────────────
REPORTS_DIR="$DATA_DIR/reports"
if [[ -d "$REPORTS_DIR" ]]; then
    mkdir -p "$ARCHIVE_DIR"
    cp -r "$REPORTS_DIR" "$ARCHIVE_DIR/reports"
    echo "[WallEye] Reports folder copied to $ARCHIVE_DIR/reports."
else
    echo "[WallEye] No reports folder found, skipping."
fi

# ── 4. Full no-cache rebuild + restart ───────────────────────────────────────
echo "[WallEye] Starting full rebuild..."
cd "$PROJECT_DIR"
make rebuild

echo "[WallEye] Daily restart complete."
