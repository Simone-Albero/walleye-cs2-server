"""WallEye Admin Panel — Flask backend."""

import hmac
import json
import re
import secrets
from pathlib import Path
from urllib.parse import quote, urlparse

import docker
from flask import Flask, jsonify, redirect, render_template, request, session, url_for
from werkzeug.security import check_password_hash

app = Flask(__name__)
# Use [[ ]] for Jinja2 variables so {{ }} passes through to Vue unchanged
app.jinja_env.variable_start_string = "[["
app.jinja_env.variable_end_string   = "]]"
app.config.update(
    SESSION_COOKIE_HTTPONLY=True,
    SESSION_COOKIE_SAMESITE="Lax",
)

CONFIG_PATH = Path("/config/config.json")
AUTH_CONFIG_PATH = Path("/config/admin-auth.json")
ADMINS_PATH = Path("/admins/admins.conf")
DATA_DIR    = Path("/data")

MANAGED_SERVICES = ["cs2-server", "cs2-scorer", "cs2-web", "cs2-admin"]
PUBLIC_PATH_PREFIXES = ("/static/",)
PUBLIC_PATHS = {"/login", "/api/health"}


def _load_auth_config() -> tuple[dict | None, str | None]:
    if not AUTH_CONFIG_PATH.exists():
        return None, f"Missing auth config: {AUTH_CONFIG_PATH}"

    try:
        with open(AUTH_CONFIG_PATH, encoding="utf-8") as f:
            raw = json.load(f)
    except Exception as exc:
        return None, f"Invalid auth config: {exc}"

    session_secret = (raw.get("session_secret") or "").strip()
    if not session_secret:
        return None, "Auth config must include session_secret"

    # Support both old single-admin format and new multi-admin format
    if "admins" in raw:
        admins = [
            {"username": (e.get("username") or "").strip(),
             "password_hash": (e.get("password_hash") or "").strip()}
            for e in raw["admins"]
            if (e.get("username") or "").strip() and (e.get("password_hash") or "").strip()
        ]
    else:
        # Legacy single-admin format
        username = (raw.get("username") or "").strip()
        password_hash = (raw.get("password_hash") or "").strip()
        admins = [{"username": username, "password_hash": password_hash}] if username and password_hash else []

    if not admins:
        return None, "Auth config contains no valid admin accounts"

    return {
        "admins": admins,
        "session_secret": session_secret,
        "cookie_secure": bool(raw.get("cookie_secure", False)),
    }, None


def _ensure_csrf_token() -> str:
    token = session.get("csrf_token")
    if not token:
        token = secrets.token_urlsafe(32)
        session["csrf_token"] = token
    return token


def _safe_next_url(target: str | None) -> str:
    if not target:
        return url_for("page_dashboard")
    parsed = urlparse(target)
    if parsed.scheme or parsed.netloc:
        return url_for("page_dashboard")
    if not target.startswith("/") or target.startswith("//"):
        return url_for("page_dashboard")
    return target


def _is_authenticated(auth_config: dict | None) -> bool:
    if not auth_config:
        return False
    if not session.get("authenticated"):
        return False
    username = session.get("username", "")
    return any(hmac.compare_digest(username, a["username"]) for a in auth_config["admins"])


def _get_request_csrf_token() -> str:
    header_token = request.headers.get("X-CSRF-Token", "")
    if header_token:
        return header_token
    return (request.form.get("csrf_token") or "").strip()


def _validate_csrf() -> bool:
    expected = session.get("csrf_token", "")
    provided = _get_request_csrf_token()
    return bool(expected and provided and hmac.compare_digest(provided, expected))


def _is_public_request() -> bool:
    if request.path in PUBLIC_PATHS:
        return True
    return any(request.path.startswith(prefix) for prefix in PUBLIC_PATH_PREFIXES)


def _unauthorized_response(auth_error: str | None):
    if request.path.startswith("/api/"):
        payload = {"error": auth_error or "authentication required"}
        return jsonify(payload), (503 if auth_error else 401)
    next_url = quote(request.full_path if request.query_string else request.path, safe="/?=&")
    return redirect(f"{url_for('login')}?next={next_url}")


@app.context_processor
def inject_auth_context():
    auth_config, auth_error = _load_auth_config()
    return {
        "auth_enabled": auth_config is not None,
        "auth_error": auth_error,
        "current_user": session.get("username") if _is_authenticated(auth_config) else None,
        "csrf_token": _ensure_csrf_token(),
    }


@app.before_request
def require_authentication():
    auth_config, auth_error = _load_auth_config()
    if auth_config:
        app.config["SECRET_KEY"] = auth_config["session_secret"]
        app.config["SESSION_COOKIE_SECURE"] = auth_config["cookie_secure"]

    if _is_public_request():
        return None

    if auth_error:
        return _unauthorized_response(auth_error)

    if not _is_authenticated(auth_config):
        return _unauthorized_response(None)

    if request.method in {"POST", "PUT", "PATCH", "DELETE"} and not _validate_csrf():
        if request.path.startswith("/api/"):
            return jsonify({"error": "invalid csrf token"}), 403
        return redirect(url_for("login"))

    session.permanent = True

# ── Docker client (optional — may fail if socket not available) ───────────────

def get_docker_client():
    try:
        return docker.from_env()
    except Exception:
        return None


@app.route("/login", methods=["GET", "POST"])
def login():
    auth_config, auth_error = _load_auth_config()
    next_url = _safe_next_url(request.args.get("next") or request.form.get("next"))

    if auth_config and _is_authenticated(auth_config):
        return redirect(next_url)

    error = auth_error
    if request.method == "POST":
        if not _validate_csrf():
            error = "Session expired. Reload the page and try again."
        elif auth_error:
            error = auth_error
        else:
            username = (request.form.get("username") or "").strip()
            password = request.form.get("password") or ""
            matched = next(
                (a for a in auth_config["admins"]
                 if hmac.compare_digest(username, a["username"])
                 and check_password_hash(a["password_hash"], password)),
                None,
            )
            if matched:
                session.clear()
                session["authenticated"] = True
                session["username"] = matched["username"]
                session["csrf_token"] = secrets.token_urlsafe(32)
                session.permanent = True
                return redirect(next_url)
            error = "Invalid username or password."

    return render_template("login.html", error=error, next_url=next_url)


@app.route("/logout", methods=["POST"])
def logout():
    session.clear()
    return redirect(url_for("login"))

# ── Pages ──────────────────────────────────────────────────────────────────────

@app.route("/")
@app.route("/dashboard")
def page_dashboard():
    return render_template("dashboard.html", active="dashboard")

@app.route("/config")
def page_config():
    return render_template("config.html", active="config")

@app.route("/admins")
def page_admins():
    return render_template("admins.html", active="admins")

@app.route("/stats")
def page_stats():
    return render_template("stats.html", active="stats")

@app.route("/bug-reports")
def page_bug_reports():
    return render_template("bug-reports.html", active="bug-reports")

# ── API: Dashboard ─────────────────────────────────────────────────────────────

@app.route("/api/status")
def api_status():
    client = get_docker_client()
    result = {}
    for name in MANAGED_SERVICES:
        info = {"name": name, "status": "unknown", "cpu_pct": None, "ram_mb": None, "ram_limit_mb": None}
        if client:
            try:
                container = client.containers.get(name)
                info["status"] = container.status
                if container.status == "running":
                    stats = container.stats(stream=False)
                    cpu_delta    = stats["cpu_stats"]["cpu_usage"]["total_usage"] - \
                                   stats["precpu_stats"]["cpu_usage"]["total_usage"]
                    system_delta = stats["cpu_stats"].get("system_cpu_usage", 0) - \
                                   stats["precpu_stats"].get("system_cpu_usage", 0)
                    num_cpus     = stats["cpu_stats"].get("online_cpus") or \
                                   len(stats["cpu_stats"]["cpu_usage"].get("percpu_usage", [1]))
                    if system_delta > 0:
                        info["cpu_pct"] = round(cpu_delta / system_delta * num_cpus * 100, 1)
                    mem = stats.get("memory_stats", {})
                    usage = mem.get("usage", 0)
                    limit = mem.get("limit", 0)
                    # Subtract cache from usage (Linux)
                    cache = mem.get("stats", {}).get("cache", 0)
                    info["ram_mb"]       = round((usage - cache) / 1024 / 1024, 1)
                    info["ram_limit_mb"] = round(limit / 1024 / 1024, 1) if limit else None
            except docker.errors.NotFound:
                info["status"] = "not found"
            except Exception as e:
                info["status"] = f"error: {e}"
        result[name] = info
    return jsonify(result)

@app.route("/api/game-status")
def api_game_status():
    status = _load_json(DATA_DIR / "status.json") or {}
    return jsonify({
        "phase": status.get("phase") or "unknown",
        "players": status.get("players"),
        "required_players": status.get("required_players"),
        "match": status.get("match"),
        "cheaters": status.get("cheaters") or [],
    })

@app.route("/api/health")
def api_health():
    return jsonify({"ok": True})

@app.route("/api/restart/<name>", methods=["POST"])
def api_restart(name):
    if name not in MANAGED_SERVICES:
        return jsonify({"error": "unknown service"}), 400
    client = get_docker_client()
    if not client:
        return jsonify({"error": "docker unavailable"}), 503
    try:
        client.containers.get(name).restart(timeout=10)
        return jsonify({"ok": True})
    except Exception as e:
        return jsonify({"error": str(e)}), 500

# ── API: Config ────────────────────────────────────────────────────────────────

@app.route("/api/config", methods=["GET"])
def api_config_get():
    if not CONFIG_PATH.exists():
        return jsonify({"error": "config.json not found"}), 404
    with open(CONFIG_PATH, encoding="utf-8") as f:
        return jsonify(json.load(f))

@app.route("/api/config", methods=["POST"])
def api_config_post():
    data = request.get_json(force=True)
    if not data:
        return jsonify({"error": "empty body"}), 400

    errors = _validate_config(data)
    if errors:
        return jsonify({"error": errors}), 422

    # Preserve fields not managed by the GUI (e.g. plugins, esp, _comment)
    if CONFIG_PATH.exists():
        with open(CONFIG_PATH, encoding="utf-8") as f:
            existing = json.load(f)
    else:
        existing = {}

    for section in ("match", "scoring", "server", "ui", "dev"):
        if section in data:
            existing[section] = data[section]

    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(existing, f, indent=2, ensure_ascii=False)

    # Restart affected services
    client = get_docker_client()
    if client:
        for svc in ("cs2-server", "cs2-scorer", "cs2-web"):
            try:
                client.containers.get(svc).restart(timeout=10)
            except Exception:
                pass

    return jsonify({"ok": True})

def _validate_config(data: dict) -> str | None:
    m = data.get("match", {})
    if not isinstance(m.get("required_players", 1), int) or not (1 <= m.get("required_players", 1) <= 20):
        return "required_players must be an integer between 1 and 20"
    if not isinstance(m.get("cheaters_count", 1), int) or not (0 <= m.get("cheaters_count", 1) <= 10):
        return "cheaters_count must be an integer between 0 and 10"
    if m.get("cheater_selection") not in (None, "per_team", "global"):
        return "cheater_selection must be 'per_team' or 'global'"
    if m.get("report_scope") not in (None, "all", "enemy_team"):
        return "report_scope must be 'all' or 'enemy_team'"
    return None

# ── API: Admins ────────────────────────────────────────────────────────────────

def _read_admins() -> list[dict]:
    if not ADMINS_PATH.exists():
        return []
    admins = []
    for line in ADMINS_PATH.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split(None, 1)
        if len(parts) == 2:
            admins.append({"username": parts[0], "steamid": parts[1]})
        elif len(parts) == 1:
            admins.append({"username": "", "steamid": parts[0]})
    return admins

def _write_admins(admins: list[dict]):
    lines = [f"{a['username']} {a['steamid']}\n" for a in admins]
    ADMINS_PATH.write_text("".join(lines), encoding="utf-8")

@app.route("/api/admins", methods=["GET"])
def api_admins_get():
    return jsonify(_read_admins())

@app.route("/api/admins", methods=["POST"])
def api_admins_post():
    data = request.get_json(force=True)
    username = (data.get("username") or "").strip()
    steamid  = (data.get("steamid")  or "").strip()

    if not re.fullmatch(r"\d{17}", steamid):
        return jsonify({"error": "SteamID64 must be exactly 17 digits"}), 422

    admins = _read_admins()
    if any(a["steamid"] == steamid for a in admins):
        return jsonify({"error": "SteamID already present"}), 409

    admins.append({"username": username or steamid, "steamid": steamid})
    _write_admins(admins)
    _restart_cs2_server()
    return jsonify({"ok": True})

@app.route("/api/admins/<steamid>", methods=["DELETE"])
def api_admins_delete(steamid):
    if not re.fullmatch(r"\d{17}", steamid):
        return jsonify({"error": "invalid SteamID"}), 422
    admins = _read_admins()
    new_admins = [a for a in admins if a["steamid"] != steamid]
    if len(new_admins) == len(admins):
        return jsonify({"error": "not found"}), 404
    _write_admins(new_admins)
    _restart_cs2_server()
    return jsonify({"ok": True})

def _restart_cs2_server():
    client = get_docker_client()
    if client:
        try:
            client.containers.get("cs2-server").restart(timeout=10)
        except Exception:
            pass

# ── API: Stats ─────────────────────────────────────────────────────────────────

def _iter_player_records(players_raw: dict | None):
    for key, value in (players_raw or {}).items():
        if not isinstance(value, dict):
            continue
        nickname = value.get("nickname") or ("" if key.isdigit() else key)
        if not nickname:
            continue
        yield {
            **value,
            "nickname": nickname,
            "steam_id": value.get("steam_id") or (key if key.isdigit() else ""),
        }

@app.route("/api/stats/summary")
def api_stats_summary():
    players_path = DATA_DIR / "players.json"
    matches_path = DATA_DIR / "matches.json"

    players_raw = _load_json(players_path)
    matches_raw = _load_json(matches_path)
    matches_map = matches_raw.get("matches", {}) if matches_raw else {}

    total_matches = len(matches_map)
    player_records = list(_iter_player_records(players_raw))
    unique_players = len(player_records)

    # Detection rate: % reporters who had at least 1 correct report
    total_reports  = sum(len(m.get("report_summary", [])) for m in matches_map.values())
    correct_hits   = sum(
        1 for m in matches_map.values()
        for r in m.get("report_summary", [])
        if "C:" in r.get("result", "") and not r["result"].startswith("C:0")
    )
    detection_pct  = round(correct_hits / total_reports * 100) if total_reports else 0

    top5 = []
    if player_records:
        sorted_players = sorted(player_records, key=lambda p: p.get("total_points", 0), reverse=True)
        top5 = [
            {"nickname": p.get("nickname", "Unknown"), "steam_id": p.get("steam_id", ""), "total_points": p.get("total_points", 0)}
            for p in sorted_players[:5]
        ]

    avg_score = 0
    if player_records:
        all_scores = [p.get("total_points", 0) for p in player_records]
        avg_score  = round(sum(all_scores) / len(all_scores), 1)

    return jsonify({
        "total_matches":   total_matches,
        "unique_players":  unique_players,
        "detection_pct":   detection_pct,
        "avg_score":       avg_score,
        "top5":            top5,
    })

@app.route("/api/stats/matches")
def api_stats_matches():
    matches_raw = _load_json(DATA_DIR / "matches.json")
    if not matches_raw:
        return jsonify([])
    rows = []
    for mid, m in matches_raw.get("matches", {}).items():
        rows.append({
            "id":            mid,
            "created_at":    m.get("created_at", ""),
            "cheaters":      m.get("cheaters", []),
            "reports_count": m.get("reports_count", 0),
            "demo":          m.get("demo"),
        })
    rows.sort(key=lambda x: x["created_at"], reverse=True)
    return jsonify(rows)

@app.route("/api/stats/match/<match_id>")
def api_stats_match(match_id):
    if not re.fullmatch(r"match_\w+", match_id):
        return jsonify({"error": "invalid match id"}), 422
    matches_raw = _load_json(DATA_DIR / "matches.json")
    if not matches_raw:
        return jsonify({"error": "not found"}), 404
    match = matches_raw.get("matches", {}).get(match_id)
    if not match:
        return jsonify({"error": "not found"}), 404
    return jsonify(match)

def _load_json(path: Path) -> dict | None:
    if not path.exists():
        return None
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None

# ── API: Bug Reports ───────────────────────────────────────────────────────────

BUG_REPORTS_PATH = DATA_DIR / "bug_reports.json"

def _load_bug_reports() -> list[dict]:
    if not BUG_REPORTS_PATH.exists():
        return []
    try:
        with open(BUG_REPORTS_PATH, encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, list):
            return []
        # Back-fill status for reports created before this field existed
        for r in data:
            if "status" not in r:
                r["status"] = "open"
        return data
    except Exception:
        return []

def _save_bug_reports(reports: list[dict]):
    BUG_REPORTS_PATH.parent.mkdir(parents=True, exist_ok=True)
    with open(BUG_REPORTS_PATH, "w", encoding="utf-8") as f:
        json.dump(reports, f, indent=2, ensure_ascii=False)

@app.route("/api/bug-reports")
def api_bug_reports_list():
    reports = _load_bug_reports()

    status_filter  = request.args.get("status", "").strip()       # open | resolved | ""
    nickname_filter = request.args.get("nickname", "").strip().lower()
    keyword_filter  = request.args.get("keyword", "").strip().lower()
    date_from       = request.args.get("date_from", "").strip()   # ISO date string
    date_to         = request.args.get("date_to",   "").strip()

    if status_filter in ("open", "resolved"):
        reports = [r for r in reports if r.get("status") == status_filter]

    if nickname_filter:
        reports = [r for r in reports if nickname_filter in r.get("nickname", "").lower()]

    if keyword_filter:
        reports = [r for r in reports if keyword_filter in r.get("note", "").lower()
                                       or keyword_filter in r.get("nickname", "").lower()]

    if date_from:
        reports = [r for r in reports if r.get("created_at", "") >= date_from]

    if date_to:
        # date_to is inclusive, add T23:59:59 to cover the whole day
        reports = [r for r in reports if r.get("created_at", "") <= date_to + "T23:59:59"]

    reports = sorted(reports, key=lambda r: r.get("created_at", ""), reverse=True)
    return jsonify(reports)

@app.route("/api/bug-reports/<int:report_id>", methods=["PATCH"])
def api_bug_reports_patch(report_id):
    data   = request.get_json(force=True) or {}
    status = (data.get("status") or "").strip()
    if status not in ("open", "resolved"):
        return jsonify({"error": "status must be 'open' or 'resolved'"}), 422

    reports = _load_bug_reports()
    for r in reports:
        if r.get("id") == report_id:
            r["status"] = status
            _save_bug_reports(reports)
            return jsonify({"ok": True})
    return jsonify({"error": "not found"}), 404

@app.route("/api/bug-reports/<int:report_id>", methods=["DELETE"])
def api_bug_reports_delete(report_id):
    reports = _load_bug_reports()
    new_reports = [r for r in reports if r.get("id") != report_id]
    if len(new_reports) == len(reports):
        return jsonify({"error": "not found"}), 404
    _save_bug_reports(new_reports)
    return jsonify({"ok": True})

# ── Entry point ────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    app.run(host="0.0.0.0", port=8081, debug=False)
