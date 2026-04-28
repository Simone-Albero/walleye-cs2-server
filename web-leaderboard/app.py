"""WallEye Web Leaderboard — Flask API + pagina statica."""

import json
import re
from datetime import datetime, timezone
from pathlib import Path
from flask import Flask, render_template, jsonify, request

app      = Flask(__name__)
# Use [[ ]] for Jinja2 variables so {{ }} passes through to Vue unchanged
app.jinja_env.variable_start_string = "[["
app.jinja_env.variable_end_string   = "]]"
DATA_DIR = Path("/data")

def load_leaderboard():
    path = DATA_DIR / "players.json"
    if not path.exists(): return []
    try:
        with open(path, encoding="utf-8") as f:
            raw = json.load(f)
    except (json.JSONDecodeError, ValueError):
        return []
    players = []
    for key, p in raw.items():
        if not isinstance(p, dict):
            continue
        nickname = p.get("nickname") or ("" if key.isdigit() else key)
        if not nickname:
            continue
        players.append({
            "id":             nickname,
            "steam_id":       p.get("steam_id") or (key if key.isdigit() else ""),
            "nickname":       nickname,
            "total_points":   p.get("total_points",   0),
            "matches_played": p.get("matches_played", 0),
            "correct_reports":p.get("correct_reports",0),
            "wrong_reports":  p.get("wrong_reports",  0),
            "kills":          p.get("kills",          0),
            "deaths":         p.get("deaths",         0),
            "assists":        p.get("assists",         0),
        })
    players.sort(key=lambda x: x["total_points"], reverse=True)
    return players

@app.route("/")
def index():
    return render_template("index.html")

@app.route("/api/leaderboard")
def api_leaderboard():
    return jsonify({"players": load_leaderboard()})

@app.route("/api/matches")
def api_matches():
    path = DATA_DIR / "matches.json"
    if not path.exists(): return jsonify({"matches": {}})
    try:
        with open(path, encoding="utf-8") as f:
            return jsonify(json.load(f))
    except (json.JSONDecodeError, ValueError):
        return jsonify({"matches": {}})

@app.route("/api/bug-report", methods=["POST"])
def api_bug_report():
    data     = request.get_json(force=True) or {}
    nickname = (data.get("nickname") or "").strip()[:64]
    note     = (data.get("note")     or "").strip()[:1000]

    if not nickname:
        return jsonify({"error": "nickname is required"}), 400
    if not note:
        return jsonify({"error": "note is required"}), 400

    path    = DATA_DIR / "bug_reports.json"
    reports = []
    if path.exists():
        try:
            with open(path, encoding="utf-8") as f:
                reports = json.load(f)
            if not isinstance(reports, list):
                reports = []
        except Exception:
            reports = []

    reports.append({
        "id":         len(reports) + 1,
        "nickname":   nickname,
        "note":       note,
        "status":     "open",
        "created_at": datetime.now(timezone.utc).isoformat(),
    })

    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(reports, f, indent=2, ensure_ascii=False)

    return jsonify({"ok": True})

if __name__ == "__main__":
    app.run(host="0.0.0.0", port=8080, debug=False)
