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

def get_or_create_player(players: dict, steam_id: str, nickname: str) -> dict:
    if steam_id not in players:
        players[steam_id] = {"nickname": nickname, "total_points": 0,
                             "matches_played": 0, "correct_reports": 0,
                             "wrong_reports": 0, "cheater_count": 0,
                             "kills": 0, "deaths": 0, "assists": 0}
    elif nickname:
        players[steam_id]["nickname"] = nickname
    # Migrate older records that lack K/D/A fields
    for field in ("kills", "deaths", "assists"):
        players[steam_id].setdefault(field, 0)
    return players[steam_id]

# ── Demo parser ───────────────────────────────────────────────────────────────

def parse_demo(demo_path: Path) -> Optional[dict]:
    """Returns { steam_id: { kills, deaths, assists, score } } or None."""
    try:
        from demoparser2 import DemoParser
        parser   = DemoParser(str(demo_path))
        info_df  = parser.parse_player_info()
        kills_df = parser.parse_event("player_death", player=["kills", "deaths", "assists"])

        stats = {}
        if "xuid" in info_df.columns:
            for _, row in info_df.iterrows():
                xuid = str(row.get("xuid", ""))
                if xuid and xuid != "0":
                    stats[xuid] = {"nickname": row.get("name", ""),
                                   "kills": 0, "deaths": 0, "assists": 0}

        if "attacker_xuid" in kills_df.columns:
            for _, row in kills_df.iterrows():
                att = str(row.get("attacker_xuid", ""))
                vic = str(row.get("user_xuid", ""))
                ass = str(row.get("assister_xuid", ""))
                if att in stats: stats[att]["kills"]   += 1
                if vic in stats: stats[vic]["deaths"]  += 1
                if ass in stats: stats[ass]["assists"] += 1

        for s in stats.values():
            s["score"] = s["kills"] * PTS_KILL + s["assists"] * PTS_ASSIST + s["deaths"] * PTS_DEATH

        log.info("Demo parsed: %s — %d players", demo_path.name, len(stats))
        return stats
    except Exception as e:
        log.error("Error parsing %s: %s", demo_path, e)
        return None

# ── Main pipeline ───────────────────────────────────────────────────────

def process_pending_match(pending_path: Path):
    try:
        pending     = load_json(pending_path)
        match_id    = pending.get("match_id", "match_unknown")
        cheater_ids = set(pending.get("cheaters", []))
        player_list = pending.get("players", [])

        log.info("Processing %s | cheaters=%s", match_id, cheater_ids)

        players = load_json(DATA_DIR / "players.json")
        matches = load_json(DATA_DIR / "matches.json")
        matches.setdefault("matches", {})
        matches.setdefault("next_id", 1)
        matches.setdefault("processed_demos", [])

        for p in player_list:
            get_or_create_player(players, p["steam_id"], p.get("nickname", ""))

        # Report
        reports_raw = load_json(REPORTS_DIR / f"{match_id}_reports.json")
        if not isinstance(reports_raw, list): reports_raw = []

        report_summary = []
        for rep in reports_raw:
            rid       = rep.get("reporter_steam_id", "")
            suspected = set(rep.get("suspected_steam_ids", []))
            if not rid: continue

            p      = get_or_create_player(players, rid, rep.get("reporter_nickname", ""))
            p["matches_played"] += 1
            gained = PTS_PARTICIPATION

            if not cheater_ids:
                if not suspected:
                    gained += PTS_NO_CHEATER_OK; p["correct_reports"] += 1; result = "OK (no cheater)"
                else:
                    gained += PTS_WRONG_REPORT;  p["wrong_reports"] += 1;   result = "Wrong"
            else:
                correct = suspected & cheater_ids
                wrong   = suspected - cheater_ids
                if correct: gained += PTS_CORRECT_REPORT * len(correct); p["correct_reports"] += len(correct)
                if wrong:   gained += PTS_WRONG_REPORT   * len(wrong);   p["wrong_reports"]   += len(wrong)
                result = f"C:{len(correct)} W:{len(wrong)}"

            p["total_points"] += gained
            report_summary.append({"reporter": rid, "result": result, "pts": gained})
            log.info("  %s -> %s +%d", rid, result, gained)

        # Demo K/D/A
        latest_demo = next(iter(sorted(REPLAYS_DIR.glob("*.dem"),
                                       key=lambda p: p.stat().st_mtime, reverse=True)), None)
        if latest_demo and latest_demo.name not in matches["processed_demos"]:
            demo_stats = parse_demo(latest_demo)
            if demo_stats:
                for sid, stat in demo_stats.items():
                    if sid in players:
                        players[sid]["total_points"] += stat["score"]
                        players[sid]["kills"]   = players[sid].get("kills",   0) + stat["kills"]
                        players[sid]["deaths"]  = players[sid].get("deaths",  0) + stat["deaths"]
                        players[sid]["assists"] = players[sid].get("assists", 0) + stat["assists"]
                matches["processed_demos"].append(latest_demo.name)

        matches["matches"][match_id] = {
            "cheaters":       list(cheater_ids),
            "reports_count":  len(reports_raw),
            "report_summary": report_summary,
            "demo":           latest_demo.name if latest_demo else None,
            "closed":         True,
            "created_at":     pending.get("timestamp", datetime.now(timezone.utc).isoformat()),
        }
        last_part = match_id.split("_")[-1]
        if last_part.isdigit():
            matches["next_id"] = max(matches["next_id"], int(last_part) + 1)

        save_json(DATA_DIR / "players.json", players)
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

    observer = Observer()
    observer.schedule(Handler(), str(DATA_DIR), recursive=False)
    observer.start()
    try:
        while True: time.sleep(5)
    except KeyboardInterrupt:
        observer.stop()
    observer.join()

if __name__ == "__main__":
    main()
