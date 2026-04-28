"""WallEye Scoring Service — watches /data/ for pending_match.json."""

import json
import logging
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler

# ── Config da file (montato su /config/config.json) ───────────────────────────
def load_config() -> dict:
    path = Path("/config/config.json")
    if path.exists():
        with open(path) as f:
            return json.load(f)
    return {}

_cfg = load_config()
_scoring = _cfg.get("scoring", {})

DATA_DIR    = Path(_cfg.get("server", {}).get("data_path", "/data"))
REPLAYS_DIR = DATA_DIR / "replays"
REPORTS_DIR = DATA_DIR / "reports"
PLAYER_INDEX_PATH = DATA_DIR / "player_index.json"
DEMO_STABLE_SECONDS = 10
DEMO_REPAIR_INTERVAL_SECONDS = 30

PTS_PARTICIPATION  = _scoring.get("points_participation",      10)
PTS_CORRECT_REPORT = _scoring.get("points_correct_report",     30)
PTS_WRONG_REPORT   = _scoring.get("points_wrong_report",      -20)
PTS_NO_CHEATER_OK  = _scoring.get("points_no_cheater_correct", 30)
PTS_KILL           = _scoring.get("points_kill",                2)
PTS_ASSIST         = _scoring.get("points_assist",              1)
PTS_DEATH          = _scoring.get("points_death",              -1)

logging.basicConfig(level=logging.INFO,
                    format="%(asctime)s [%(levelname)s] %(message)s",
                    datefmt="%Y-%m-%d %H:%M:%S")
log = logging.getLogger("WallEyeScorer")

# ── I/O helpers ───────────────────────────────────────────────────────────────

def load_json(path: Path) -> dict:
    if not path.exists(): return {}
    with open(path, encoding="utf-8") as f: return json.load(f)

def save_json(path: Path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)

def _base_player(nickname: str, steam_id: str = "") -> dict:
    return {
        "nickname": nickname,
        "steam_id": steam_id,
        "steam_ids": [steam_id] if steam_id else [],
        "total_points": 0,
        "matches_played": 0,
        "correct_reports": 0,
        "wrong_reports": 0,
        "cheater_count": 0,
        "kills": 0,
        "deaths": 0,
        "assists": 0,
    }

def _clean_nickname(nickname: str | None, steam_id: str = "") -> str:
    nickname = (nickname or "").strip()
    if nickname:
        return nickname
    suffix = steam_id[-6:] if steam_id else "unknown"
    return f"Unknown_{suffix}"

def _canonical_nickname(index: dict, nickname: str, steam_id: str = "") -> str:
    nickname = _clean_nickname(nickname, steam_id)
    if not steam_id:
        return nickname

    existing = index.setdefault("nickname_to_steam", {}).get(nickname)
    if not existing or existing == steam_id:
        return nickname

    # Avoid merging two different Steam accounts that happen to share a name.
    return f"{nickname}#{steam_id[-4:]}"

def _empty_index() -> dict:
    return {"steam_to_nickname": {}, "nickname_to_steam": {}}

def normalize_players(raw: dict) -> tuple[dict, dict]:
    """Return players keyed by nickname plus a SteamID<->nickname index.

    Accepts both the old format ({steam_id: player}) and the new format
    ({nickname: player}) so existing deployments migrate in-place.
    """
    index = _empty_index()
    players: dict = {}
    if not isinstance(raw, dict):
        return players, index

    for key, value in raw.items():
        if not isinstance(value, dict):
            continue
        steam_ids = value.get("steam_ids") if isinstance(value.get("steam_ids"), list) else []
        steam_id = str(value.get("steam_id") or (key if key.isdigit() else "") or (steam_ids[0] if steam_ids else "") or "")
        nickname = _canonical_nickname(index, value.get("nickname") or ("" if key.isdigit() else key), steam_id)

        record = players.setdefault(nickname, _base_player(nickname, steam_id))
        for field in ("total_points", "matches_played", "correct_reports", "wrong_reports",
                      "cheater_count", "kills", "deaths", "assists"):
            record[field] = record.get(field, 0) + int(value.get(field, 0) or 0)

        if steam_id:
            record["steam_id"] = record.get("steam_id") or steam_id
            steam_ids = set(record.get("steam_ids") or [])
            steam_ids.add(steam_id)
            record["steam_ids"] = sorted(steam_ids)
            index["steam_to_nickname"][steam_id] = nickname
            index["nickname_to_steam"][nickname] = steam_id

    return players, index

def get_or_create_player(players: dict, index: dict, steam_id: str = "", nickname: str = "") -> tuple[str, dict]:
    steam_id = str(steam_id or "")
    nickname = _canonical_nickname(index, nickname or index.get("steam_to_nickname", {}).get(steam_id, ""), steam_id)

    if nickname not in players:
        players[nickname] = _base_player(nickname, steam_id)
    record = players[nickname]
    record["nickname"] = nickname

    if steam_id:
        record["steam_id"] = record.get("steam_id") or steam_id
        steam_ids = set(record.get("steam_ids") or [])
        steam_ids.add(steam_id)
        record["steam_ids"] = sorted(steam_ids)
        index.setdefault("steam_to_nickname", {})[steam_id] = nickname
        index.setdefault("nickname_to_steam", {})[nickname] = steam_id

    for field in ("total_points", "matches_played", "correct_reports", "wrong_reports",
                  "cheater_count", "kills", "deaths", "assists"):
        record.setdefault(field, 0)
    record.setdefault("steam_ids", [steam_id] if steam_id else [])
    record.setdefault("steam_id", steam_id)
    return nickname, record

def resolve_player_token(token: str, players: dict, index: dict) -> str:
    token = str(token or "").strip()
    if not token:
        return ""
    if token in players:
        return token
    return index.get("steam_to_nickname", {}).get(token, token)

def normalize_match_logs(matches: dict, players: dict, index: dict) -> None:
    """Convert old SteamID references in match logs to nickname references."""
    for match in matches.get("matches", {}).values():
        if not isinstance(match, dict):
            continue

        for field in ("cheaters", "players"):
            tokens = match.get(field, [])
            if isinstance(tokens, list):
                match[field] = sorted(
                    name for name in (resolve_player_token(t, players, index) for t in tokens)
                    if name
                )

        summaries = match.get("report_summary", [])
        if not isinstance(summaries, list):
            continue
        for rep in summaries:
            if not isinstance(rep, dict):
                continue
            rep["reporter"] = resolve_player_token(rep.get("reporter", ""), players, index)
            for field in ("suspected", "correct", "wrong"):
                tokens = rep.get(field, [])
                if isinstance(tokens, list):
                    rep[field] = sorted(
                        name for name in (resolve_player_token(t, players, index) for t in tokens)
                        if name
                    )

# ── Demo parser ───────────────────────────────────────────────────────────────

def _first_present(row, names: tuple[str, ...], default=""):
    for name in names:
        if name in row and row.get(name) not in (None, ""):
            return row.get(name)
    return default

def _steam_id(value) -> str:
    value = str(value or "").strip()
    if value.endswith(".0"):
        value = value[:-2]
    return value if value and value != "0" else ""

def _int_value(value, default: int = 0) -> int:
    try:
        if value in (None, ""):
            return default
        return int(float(value))
    except (TypeError, ValueError):
        return default

def _is_demo_stable(demo_path: Path, stable_seconds: int = DEMO_STABLE_SECONDS) -> bool:
    try:
        first_size = demo_path.stat().st_size
        if first_size <= 0:
            return False
        time.sleep(1)
        stat = demo_path.stat()
        age = time.time() - stat.st_mtime
        return stat.st_size == first_size and age >= stable_seconds
    except FileNotFoundError:
        return False

def parse_demo(demo_path: Path) -> Optional[dict]:
    """Returns { steam_id: { kills, deaths, assists, score } } or None."""
    try:
        from demoparser2 import DemoParser
        parser   = DemoParser(str(demo_path))
        info_df  = parser.parse_player_info()
        kills_df = parser.parse_event("player_death", player=["kills", "deaths", "assists"])

        stats = {}
        info_columns = set(info_df.columns)
        log.info("Demo player_info columns: %s", sorted(info_columns))
        for _, row in info_df.iterrows():
            sid = _steam_id(_first_present(row, (
                "xuid", "steamid", "steam_id", "steamid64", "steam_id64"
            )))
            if sid:
                stats[sid] = {
                    "nickname": _first_present(row, ("name", "player_name", "user_name"), ""),
                    "kills": 0,
                    "deaths": 0,
                    "assists": 0,
                }

        event_columns = set(kills_df.columns) if kills_df is not None else set()
        log.info("Demo player_death columns: %s", sorted(event_columns))
        attacker_cols = ("attacker_xuid", "attacker_steamid", "attacker_steam_id", "attacker_steamid64")
        victim_cols   = ("user_xuid", "userid_xuid", "victim_xuid", "user_steamid", "victim_steamid", "user_steam_id")
        assister_cols = ("assister_xuid", "assister_steamid", "assister_steam_id", "assister_steamid64")

        if kills_df is not None and not kills_df.empty:
            for _, row in kills_df.iterrows():
                att = _steam_id(_first_present(row, attacker_cols))
                vic = _steam_id(_first_present(row, victim_cols))
                ass = _steam_id(_first_present(row, assister_cols))

                if vic and vic not in stats:
                    stats[vic] = {"nickname": "", "kills": 0, "deaths": 0, "assists": 0}
                if att and att not in stats:
                    stats[att] = {"nickname": "", "kills": 0, "deaths": 0, "assists": 0}
                if ass and ass not in stats:
                    stats[ass] = {"nickname": "", "kills": 0, "deaths": 0, "assists": 0}

                if vic:
                    stats[vic]["deaths"] += 1
                if att and att != vic:
                    stats[att]["kills"] += 1
                if ass and ass not in (att, vic):
                    stats[ass]["assists"] += 1

        # Some demoparser2 builds expose final scoreboard counters on player_info.
        # If event parsing produced no K/D/A, use those aggregate fields instead.
        if stats and not any(s["kills"] or s["deaths"] or s["assists"] for s in stats.values()):
            for _, row in info_df.iterrows():
                sid = _steam_id(_first_present(row, (
                    "xuid", "steamid", "steam_id", "steamid64", "steam_id64"
                )))
                if not sid or sid not in stats:
                    continue
                stats[sid]["kills"] = _int_value(_first_present(row, ("kills", "scoreboard_kills"), 0))
                stats[sid]["deaths"] = _int_value(_first_present(row, ("deaths", "scoreboard_deaths"), 0))
                stats[sid]["assists"] = _int_value(_first_present(row, ("assists", "scoreboard_assists"), 0))

        for s in stats.values():
            s["score"] = s["kills"] * PTS_KILL + s["assists"] * PTS_ASSIST + s["deaths"] * PTS_DEATH

        totals = {
            "kills": sum(s["kills"] for s in stats.values()),
            "deaths": sum(s["deaths"] for s in stats.values()),
            "assists": sum(s["assists"] for s in stats.values()),
        }
        log.info("Demo parsed: %s — %d players | K=%d D=%d A=%d",
                 demo_path.name, len(stats), totals["kills"], totals["deaths"], totals["assists"])
        return stats
    except Exception as e:
        log.error("Error parsing %s: %s", demo_path, e, exc_info=True)
        return None

def find_latest_unprocessed_demo(matches: dict, require_stable: bool = True) -> Optional[Path]:
    processed = set(matches.get("processed_demos", []))
    candidates = [
        demo for demo in REPLAYS_DIR.glob("*.dem")
        if demo.name not in processed
    ]
    if not candidates:
        log.warning("No unprocessed demo found in %s", REPLAYS_DIR)
        return None

    candidates = sorted(candidates, key=lambda p: p.stat().st_mtime, reverse=True)
    if not require_stable:
        return candidates[0]

    for demo in candidates:
        if _is_demo_stable(demo):
            return demo

    log.warning("Unprocessed demos exist, but none are stable yet: %s",
                [demo.name for demo in candidates])
    return None

def find_oldest_unprocessed_demo(matches: dict, require_stable: bool = True) -> Optional[Path]:
    processed = set(matches.get("processed_demos", []))
    candidates = [
        demo for demo in REPLAYS_DIR.glob("*.dem")
        if demo.name not in processed
    ]
    candidates = sorted(candidates, key=lambda p: p.stat().st_mtime)

    if not require_stable:
        return candidates[0] if candidates else None

    for demo in candidates:
        if _is_demo_stable(demo):
            return demo

    return None

def apply_demo_stats_to_players(demo_stats: dict, players: dict, index: dict,
                                match_scores: Optional[dict[str, int]] = None) -> dict:
    totals = {"kills": 0, "deaths": 0, "assists": 0, "score": 0}
    for sid, stat in demo_stats.items():
        nickname, player_record = get_or_create_player(players, index, sid, stat.get("nickname", ""))
        player_record["total_points"] += stat["score"]
        player_record["kills"]   = player_record.get("kills",   0) + stat["kills"]
        player_record["deaths"]  = player_record.get("deaths",  0) + stat["deaths"]
        player_record["assists"] = player_record.get("assists", 0) + stat["assists"]
        if match_scores is not None:
            match_scores[nickname] = match_scores.get(nickname, 0) + stat["score"]

        totals["kills"] += stat["kills"]
        totals["deaths"] += stat["deaths"]
        totals["assists"] += stat["assists"]
        totals["score"] += stat["score"]
    return totals

def _add_demo_points_to_scoreboard(match: dict, demo_stats: dict, players: dict, index: dict) -> None:
    scores = {
        str(row.get("nickname", "")): int(row.get("points", 0) or 0)
        for row in match.get("scoreboard", [])
        if isinstance(row, dict) and row.get("nickname")
    }

    for sid, stat in demo_stats.items():
        nickname, _ = get_or_create_player(players, index, sid, stat.get("nickname", ""))
        scores[nickname] = scores.get(nickname, 0) + stat["score"]

    match["scoreboard"] = [
        {"rank": i + 1, "nickname": name, "points": points}
        for i, (name, points) in enumerate(sorted(
            scores.items(),
            key=lambda item: (-item[1], item[0].lower())
        ))
    ]

def repair_unprocessed_match_demos() -> None:
    raw_players = load_json(DATA_DIR / "players.json")
    players, index = normalize_players(raw_players)
    matches = load_json(DATA_DIR / "matches.json")
    if not isinstance(matches, dict):
        return

    processed = set(matches.get("processed_demos", []))
    repaired = 0

    for match_id, match in matches.get("matches", {}).items():
        if not isinstance(match, dict):
            continue
        demo_name = match.get("demo")
        if demo_name and demo_name in processed:
            continue

        demo_path = REPLAYS_DIR / demo_name if demo_name else find_oldest_unprocessed_demo(matches, require_stable=True)
        if demo_path and not demo_name:
            demo_name = demo_path.name
            match["demo"] = demo_name

        if not demo_path or not demo_path.exists() or not _is_demo_stable(demo_path):
            log.warning("Skipping demo repair for %s; file missing or not stable: %s", match_id, demo_name)
            continue

        demo_stats = parse_demo(demo_path)
        if not demo_stats:
            log.warning("Skipping demo repair for %s; parser returned no stats: %s", match_id, demo_name)
            match["demo_status"] = "parse_failed"
            continue

        totals = apply_demo_stats_to_players(demo_stats, players, index)
        _add_demo_points_to_scoreboard(match, demo_stats, players, index)
        matches.setdefault("processed_demos", []).append(demo_name)
        processed.add(demo_name)
        match["demo_status"] = "processed"
        match["demo_error"] = None
        repaired += 1
        log.info("Repaired demo stats for %s from %s | K=%d D=%d A=%d score=%d",
                 match_id, demo_name, totals["kills"], totals["deaths"],
                 totals["assists"], totals["score"])

    if repaired:
        save_json(DATA_DIR / "players.json", players)
        save_json(PLAYER_INDEX_PATH, index)
        save_json(DATA_DIR / "matches.json", matches)
        log.info("Demo repair completed. repaired_matches=%d", repaired)

# ── Main pipeline ───────────────────────────────────────────────────────

def process_pending_match(pending_path: Path):
    try:
        pending     = load_json(pending_path)
        match_id    = pending.get("match_id", "match_unknown")
        raw_cheaters = pending.get("cheaters", [])
        player_list = pending.get("players", [])

        raw_players = load_json(DATA_DIR / "players.json")
        players, index = normalize_players(raw_players)
        matches = load_json(DATA_DIR / "matches.json")
        if not isinstance(matches, dict):
            matches = {}
        matches.setdefault("matches", {})
        matches.setdefault("next_id", 1)
        matches.setdefault("processed_demos", [])

        participant_names = set()
        for p in player_list:
            nickname, player_record = get_or_create_player(players, index, p.get("steam_id", ""), p.get("nickname", ""))
            if nickname:
                participant_names.add(nickname)
                player_record["matches_played"] += 1

        cheater_names = {
            resolve_player_token(c, players, index)
            for c in raw_cheaters
            if resolve_player_token(c, players, index)
        }

        log.info("Processing %s | cheaters=%s", match_id, cheater_names)

        # Report
        reports_raw = load_json(REPORTS_DIR / f"{match_id}_reports.json")
        if not isinstance(reports_raw, list): reports_raw = []

        report_summary = []
        match_scores: dict[str, int] = {name: 0 for name in participant_names}
        for name in participant_names:
            players[name]["total_points"] += PTS_PARTICIPATION
            match_scores[name] += PTS_PARTICIPATION

        for rep in reports_raw:
            reporter_name, p = get_or_create_player(
                players,
                index,
                rep.get("reporter_steam_id", ""),
                rep.get("reporter_nickname", ""),
            )
            if not reporter_name:
                continue

            suspected_tokens = rep.get("suspected_nicknames")
            if suspected_tokens is None:
                suspected_tokens = rep.get("suspected_steam_ids", [])
            suspected = {
                resolve_player_token(s, players, index)
                for s in suspected_tokens
                if resolve_player_token(s, players, index)
            }

            gained = 0

            if not cheater_names:
                if not suspected:
                    gained += PTS_NO_CHEATER_OK; p["correct_reports"] += 1; result = "OK (no cheater)"
                else:
                    gained += PTS_WRONG_REPORT;  p["wrong_reports"] += 1;   result = "Wrong"
                correct = set()
                wrong = suspected
            else:
                correct = suspected & cheater_names
                wrong   = suspected - cheater_names
                if correct: gained += PTS_CORRECT_REPORT * len(correct); p["correct_reports"] += len(correct)
                if wrong:   gained += PTS_WRONG_REPORT   * len(wrong);   p["wrong_reports"]   += len(wrong)
                if not suspected:
                    wrong = cheater_names
                    gained += PTS_WRONG_REPORT * len(wrong)
                    p["wrong_reports"] += len(wrong)
                result = f"C:{len(correct)} W:{len(wrong)}"

            p["total_points"] += gained
            match_scores[reporter_name] = match_scores.get(reporter_name, 0) + gained
            report_summary.append({
                "reporter": reporter_name,
                "suspected": sorted(suspected),
                "correct": sorted(correct),
                "wrong": sorted(wrong),
                "result": result,
                "pts": gained,
            })
            log.info("  %s -> %s %+d", reporter_name, result, gained)

        # Demo K/D/A is processed asynchronously by repair_unprocessed_match_demos().
        # This keeps match/report persistence independent from SourceTV flush timing.
        latest_demo = find_latest_unprocessed_demo(matches, require_stable=False)

        for cheater_name in cheater_names:
            if cheater_name in players:
                players[cheater_name]["cheater_count"] = players[cheater_name].get("cheater_count", 0) + 1

        normalize_match_logs(matches, players, index)

        matches["matches"][match_id] = {
            "cheaters":       sorted(cheater_names),
            "players":        sorted(participant_names),
            "scoreboard":     [
                {"rank": i + 1, "nickname": name, "points": points}
                for i, (name, points) in enumerate(sorted(
                    match_scores.items(),
                    key=lambda item: (-item[1], item[0].lower())
                ))
            ],
            "reports_count":  len(reports_raw),
            "report_summary": report_summary,
            "demo":           latest_demo.name if latest_demo else None,
            "demo_status":    "pending" if latest_demo else "not_found",
            "demo_error":     None,
            "closed":         True,
            "created_at":     pending.get("timestamp", datetime.now(timezone.utc).isoformat()),
        }
        last_part = match_id.split("_")[-1]
        if last_part.isdigit():
            matches["next_id"] = max(matches["next_id"], int(last_part) + 1)

        save_json(DATA_DIR / "players.json", players)
        save_json(PLAYER_INDEX_PATH, index)
        save_json(DATA_DIR / "matches.json", matches)
        pending_path.unlink(missing_ok=True)
        log.info("Match %s processed.", match_id)

    except Exception as e:
        log.error("Error: %s", e, exc_info=True)

# ── Watchdog ──────────────────────────────────────────────────────────────────

class Handler(FileSystemEventHandler):
    def _handle(self, path: str):
        if path.endswith("pending_match.json"):
            log.info("Detected pending_match.json")
            time.sleep(2)
            process_pending_match(Path(path))

    def on_created(self, event):
        if not event.is_directory: self._handle(event.src_path)

    def on_moved(self, event):
        if not event.is_directory: self._handle(event.dest_path)

def main():
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    REPLAYS_DIR.mkdir(parents=True, exist_ok=True)
    REPORTS_DIR.mkdir(parents=True, exist_ok=True)
    log.info("Scoring service started. Watching %s", DATA_DIR)
    repair_unprocessed_match_demos()

    pending = DATA_DIR / "pending_match.json"
    if pending.exists():
        log.info("Found existing pending_match.json at startup")
        process_pending_match(pending)

    observer = Observer()
    observer.schedule(Handler(), str(DATA_DIR), recursive=False)
    observer.start()
    next_demo_repair = time.time() + DEMO_REPAIR_INTERVAL_SECONDS
    try:
        while True:
            time.sleep(5)
            if time.time() >= next_demo_repair:
                repair_unprocessed_match_demos()
                next_demo_repair = time.time() + DEMO_REPAIR_INTERVAL_SECONDS
    except KeyboardInterrupt:
        observer.stop()
    observer.join()

if __name__ == "__main__":
    main()
