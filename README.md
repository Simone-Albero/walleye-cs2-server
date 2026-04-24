# WallEye CS2 Server

Autonomous CS2 server for cheater detection. Each match automatically assigns
a "cheater" role to a random player. During warmup ESP is active for all
(observation phase). When the match ends the report menu opens automatically
and the scoring service processes demos and reports to maintain a persistent
leaderboard.

---

## Requirements

- Docker and Docker Compose v2
- `jq` installed on the host (`sudo apt install jq`)
- At least 4 GB RAM
- At least 50 GB free disk space for the first CS2 download

---

## Exposed services & ports

| Service | Container | Protocol | Default port | URL / notes |
|---|---|---|---|---|
| CS2 game server | `cs2-server` | UDP | **27015** | Connect via CS2: `connect <IP>:27015` |
| SourceTV (GOTV / replays) | `cs2-server` | UDP | **27020** | CS2 GOTV spectate / demo recording |
| Web leaderboard | `cs2-web` | TCP | **8080** | `http://<IP>:8080` — auto-refreshes every 10s |
| Admin panel | `cs2-admin` | TCP | **8081** | `http://<IP>:8081` — login required, protect with a firewall or reverse proxy |

> The admin panel is now reachable externally on port `8081` and protected by a
> username/password login. Keep the credential file local, rotate the default
> password immediately, and preferably put HTTPS in front of it.
> CS2 and SourceTV ports must be opened in any firewall / security group.

---

## Quick start

```bash
# 1. Verify host dependencies
sudo apt install -y jq
# Docker Engine + docker compose v2 must already be installed

# 2. Create admins.conf from the template and validate config.json
make setup

# 3. Generate the admin dashboard auth file
make admin-auth
# Template reference: admin-auth.json.example

# 4. Edit cs2_main_image/admins.conf
# Format: <username> <steamid64>

# 5. (Optional) Review or edit config.json

# 6. Build images, run cs2-base if needed, and start the stack
make start
# First run: CS2 download can take 15-20+ min depending on disk/network

# 7. Verify status and open services
make status
# Web leaderboard: http://<server-ip>:8080
# Admin panel:     http://<server-ip>:8081
```

To generate `admin-auth.json` from the host:

```bash
python3 generate_admin_auth.py --username admin --write admin-auth.json
```

Or use the Make target:

```bash
make admin-auth
```

If the admin panel is served through HTTPS, use:

```bash
python3 generate_admin_auth.py --username admin --cookie-secure --write admin-auth.json
```

Or with `make`:

```bash
make admin-auth ADMIN_AUTH_ARGS=--cookie-secure
```

`admin-auth.json.example` is only a schema/template reference. Generate the real file with the command above so `password_hash` and `session_secret` are valid.

`make start` is the canonical entrypoint. It performs the same validated flow every time:

1. validate host prerequisites and `config.json`
2. ensure the external Docker volumes exist
3. build images incrementally
4. run `cs2-base` only if the shared volume does not already contain the completion marker, or if you forced a re-download
5. start `cs2-server`, `scoring-service`, `web-leaderboard`, and `admin-panel`

---

## Makefile targets

| Target | Description |
|---|---|
| `make start` | Canonical startup path: validate, build incrementally, run `cs2-base` if needed, start all services |
| `make admin-auth` | Generate `admin-auth.json` interactively from the project root |
| `make up` | Resume the stack using the same safe flow as `make start` |
| `make stop` | Stop and remove all containers (data volumes preserved) |
| `make down` | Same as `make stop` |
| `make restart` | Restart running services without rebuild |
| `make build` | Build all images only, without starting containers |
| `make update` | Incremental rebuild plus safe restart of the full stack |
| `make rebuild` | Force full rebuild (no-cache) and restart everything |
| `make apply-config` | Validate `config.json` and recreate `cs2-server`, `cs2-scorer`, `cs2-web`, and `cs2-admin` without rebuilding |
| `make logs` | Tail logs for the game server |
| `make logs-all` | Tail logs for all services |
| `make admin-logs` | Tail logs for the admin panel |
| `make status` | Show container status and service URLs |
| `make shell` | Open a bash shell inside the running cs2-server container |
| `make reset-download` | Force CS2 re-download on next `make start` |
| `make clean` | Force-remove all stale containers |

---

## Usage scenarios

### First time

```bash
make setup    # Copy admins.conf template and validate config.json
# Edit cs2_main_image/admins.conf — add your username + SteamID64
# (Optional) Edit config.json
make start    # Build images → download CS2 (~15 min) → start all services
```

### Resuming after a host reboot

```bash
make up       # Uses the same safe flow as make start; skips cs2-base if the marker exists
```

### Stopping the server

```bash
make down     # Stop and remove all containers (data volumes are preserved)
```

### After changing `config.json`

No rebuild is needed. `config.json` is bind-mounted into the containers.

```bash
make apply-config   # Validates JSON and recreates the affected services
# Or use the Admin panel → Config page after logging in
```

### After modifying CS2 plugin or Python service code

Docker layer caching ensures only changed layers are rebuilt.

```bash
make update   # Incremental rebuild + restart through the validated startup flow
```

If you need a completely clean rebuild with no layer cache (e.g. after updating
a base image or a dependency in `requirements.txt`):

```bash
make rebuild  # --no-cache rebuild of all images → restart
```

### After adding or removing admins

Preferred: use the Admin panel → Admins page after logging in.

Manual:

```bash
# Edit cs2_main_image/admins.conf (format: <username> <steamid64>, one per line)
make apply-config   # Recreates cs2-server to pick up the new admins.conf
```

### After CS2 releases an update (update the game server)

```bash
make reset-download   # Clears the .cs2_downloaded sentinel
make rebuild          # Re-downloads CS2 and rebuilds all images
```

### Debugging a broken service

```bash
make status       # Quick overview: which containers are running/healthy
make logs         # Tail cs2-server logs
make logs-all     # Tail all four services
make admin-logs   # Tail admin-panel logs
make shell        # Drop into a bash shell inside cs2-server
```

---

## Match flow

```
╔══════════════════════════════════════════════════════════════════════╗
║ Phase 1 — WaitingForPlayers                                          ║
║   Server is paused in warmup (mp_warmup_pausetimer 1).               ║
║   The plugin checks connected player count every time a player       ║
║   connects. As soon as count >= required_players the cycle starts.   ║
║   With dev.skip_player_check = true this phase is skipped entirely.  ║
╠══════════════════════════════════════════════════════════════════════╣
║ Phase 2 — WallhackPhase  (duration: wallhack_duration_seconds)       ║
║   • The cheater(s) are silently selected at this moment.             ║
║     Selection mode:                                                  ║
║       per_team — N cheaters per team (CT and T independently).       ║
║       global   — N cheaters from all connected players.              ║
║     Anti-repetition weighting: players who were cheaters recently    ║
║     have a lower probability of being selected again                 ║
║     (weight = 1 / (1 + times_as_cheater)).                           ║
║   • Only the cheater(s) receive a private chat message.              ║
║   • ESP is activated for ALL players (css_esp_on) so everyone can    ║
║     observe suspicious movement equally.                             ║
║   • warmup timer counts down wallhack_duration_seconds.              ║
║   • Warning message sent wallhack_warning_before_end_seconds before  ║
║     the phase ends (skipped if duration < warning threshold).        ║
╠══════════════════════════════════════════════════════════════════════╣
║ Phase 3 — MatchRunning                                               ║
║   • ESP disabled for everyone (css_esp_off).                         ║
║   • Cheater(s) receive private ESP only for them via                 ║
║     css_esp "<name>" true.                                           ║
║   • mp_warmup_end fires and the real match begins.                   ║
║   • Match plays until mp_maxrounds are exhausted OR a team wins,     ║
║     which triggers EventCsWinPanelMatch.                             ║
╠══════════════════════════════════════════════════════════════════════╣
║ Phase 4 — ReportPhase  (duration: report_phase_duration_seconds)     ║
║   • ESP disabled for everyone including the cheater.                 ║
║   • pending_match.json written to /data/:                            ║
║       { match_id, cheaters: [...], players: [...], timestamp }       ║
║     This file is the trigger for the scoring service.                ║
║   • Report menu opens for all players after report_menu_open_delay.  ║
║   • Players multi-select suspects via ChatMenu; they can change      ║
║     their selection before confirming.                               ║
║   • "No cheater" option available (rewarded if there are none).      ║
║   • Halfway reminder re-opens menu for players who haven't voted.    ║
║   • report_scope controls who is visible:                            ║
║       all        — all other connected players.                      ║
║       enemy_team — only the opposing team.                           ║
╠══════════════════════════════════════════════════════════════════════╣
║ Phase 5 — Scoring  (async, via scoring-service container)            ║
║   • watchdog detects pending_match.json and starts processing.       ║
║   • Reports are read from /data/reports/<match_id>_reports.json      ║
║     (written by the plugin via ReportModule.FlushReports at the      ║
║     start of the new cycle).                                         ║
║   • Scoring rules applied per reporter:                              ║
║       + points_participation (always)                                ║
║       + points_correct_report × N  (for each correctly IDed cheater) ║
║       + points_wrong_report × N    (for each wrongly reported player) ║
║       + points_no_cheater_correct  (if no cheater and no reports)    ║
║   • The latest .dem in /data/replays/ is parsed for K/D/A via        ║
║     demoparser2; demo score = kills×pts_kill + assists×pts_assist    ║
║     + deaths×pts_death. Applied only if demo not already processed.  ║
║   • players.json and matches.json updated atomically.                ║
║   • pending_match.json deleted.                                      ║
╠══════════════════════════════════════════════════════════════════════╣
║ Phase 6 — LeaderboardDisplay + Restart                               ║
║   • TOP 10 leaderboard shown in-game center HTML                     ║
║     for leaderboard_display_seconds.                                 ║
║   • After leaderboard_display + restart_delay seconds:               ║
║     mp_restartgame 1 → back to Phase 1 (WaitingForPlayers).         ║
╚══════════════════════════════════════════════════════════════════════╝
```

---

## Configuration — `config.json`

All parameters are centralised in `config.json`. No rebuild is needed to apply
changes: edit the file and run `make apply-config` (or use the admin panel at
`http://127.0.0.1:8081/config` on the host).

### `match`

| Field | Default | Description |
|---|---|---|
| `required_players` | 10 | Players required to start the cycle |
| `wallhack_duration_seconds` | 300 | Wallhack phase duration (seconds) |
| `report_phase_duration_seconds` | 60 | Report phase duration (seconds) |
| `restart_delay_seconds` | 5 | Pause after leaderboard before cycle restart |
| `leaderboard_display_seconds` | 10 | In-game TOP 10 display duration |
| `map` | "de_dust2" | Map loaded on server restart |
| `max_rounds` | 30 | Rounds per match |
| `cheaters_count` | 1 | Cheaters assigned per match (per team if `per_team`) |
| `cheater_selection` | "per_team" | `"per_team"` = N per team, `"global"` = N from everyone |
| `report_scope` | "all" | `"all"` = everyone, `"enemy_team"` = opposing team only |

### `scoring`

| Field | Default | Description |
|---|---|---|
| `points_participation` | 10 | Base points for each reporter who voted |
| `points_correct_report` | 30 | Points per correctly identified cheater |
| `points_wrong_report` | -20 | Penalty per wrongly reported player |
| `points_no_cheater_correct` | 30 | Bonus for correctly voting "no cheater" |
| `points_kill` | 2 | Points per kill (from demo) |
| `points_assist` | 1 | Points per assist (from demo) |
| `points_death` | -1 | Points per death (from demo) |

### `server`

| Field | Default | Description |
|---|---|---|
| `port` | 27015 | CS2 server UDP port |
| `tv_port` | 27020 | SourceTV UDP port |
| `web_leaderboard_port` | 8080 | Web leaderboard HTTP port |
| `server_name` | "WallEye CS2 Server" | Name visible in server browser |
| `insecure` | true | Disable VAC (required for private servers) |
| `autoupdate` | true | Auto-update CS2 on server start |

### `ui`

| Field | Default | Description |
|---|---|---|
| `rules_display_seconds` | 15 | Duration of the rules overlay on connect |
| `rules_delay_on_connect_seconds` | 3 | Delay before showing rules |
| `report_menu_open_delay_seconds` | 3 | Delay before opening the report menu |
| `wallhack_warning_before_end_seconds` | 60 | Advance warning before wallhack ends |
| `chat_prefix` | "[WallEye]" | Chat message prefix |

### `dev`

| Field | Default | Description |
|---|---|---|
| `enabled` | false | Enables dev/admin commands in-game |
| `admin_steam_ids` | [] | SteamID64 list of players with admin access |
| `skip_player_check` | false | Start cycle immediately without waiting for N players |

---

## In-game commands

| Command | Who | Description |
|---|---|---|
| `!top` / `css_top` | Everyone | Shows TOP 10 leaderboard |
| `!rank` / `css_rank` | Everyone | Shows your personal rank |
| `css_walleye_status` | Admin | Plugin status (phase, players, match counter) |

Dev commands (require `dev.enabled: true` and your SteamID64 in `admin_steam_ids`):
see [ADMIN_GUIDE.md](ADMIN_GUIDE.md).

---

## Admin panel

Available at `http://<server-ip>:8081`. Login is required and credentials are
read from `admin-auth.json` on the host. Four sections:

| Section | Description |
|---|---|
| **Dashboard** | Live status (running/stopped), CPU%, RAM for each container. Per-service Restart button. |
| **Config** | Edit all `config.json` parameters via form. Save triggers automatic restart of affected services. |
| **Admins** | Add/remove CS2 in-game admins. Changes are applied to `admins.conf` and trigger a `cs2-server` restart. |
| **Stats** | KPI summary (matches, unique players, detection rate, avg score), paginated match history, per-match detail modal. |

Authentication notes:

- `admin-auth.json.example` is the committed template.
- `admin-auth.json` is host-local and ignored by git.
- `cookie_secure` should be set to `true` when you terminate HTTPS in front of the admin panel.
- The initial sample config should be rotated before real use.

---

## Installed plugins

| Plugin | Version | Function |
|---|---|---|
| MetaMod:Source | 2.0.0-git1396 | Base framework for server plugins |
| CounterStrikeSharp | 1.0.367 | C# plugin runtime for CS2 |
| AdminESP (kgarri fork) | v1.0.2 | Command-controllable Wallhack/XRay |
| WallEyeServer | 1.0.0 | Match cycle, reports, leaderboard, rules |

---

## Data files (`data/`)

| File | Contents |
|---|---|
| `players.json` | Cumulative scores and statistics per SteamID64 |
| `matches.json` | History of closed matches with reports and cheater list |
| `match_counter.json` | Match counter written by the CS2 plugin on each cycle |
| `cheat_history.json` | Per-player cheater count (used for anti-repetition weighting) |
| `replays/*.dem` | SourceTV demo of each match |
| `reports/<match_id>_reports.json` | Raw report data per match |

---

## Admin management

Preferred: use the admin panel at `http://<server-ip>:8081/admins` after login.

Manual: edit `cs2_main_image/admins.conf` (format: `<username> <steamid64>`, one per line),
then run `make apply-config`.

---

## Troubleshooting

| Problem | What to check |
|---|---|
| Server won't start | `docker logs cs2-base` — CS2 download in progress (~20 min first time) |
| ESP not working | `docker logs cs2-server \| grep -i AdminESP` — check plugin loaded |
| Leaderboard empty | Normal until the first match completes |
| Report menu doesn't open | `docker logs cs2-server \| grep -i WallEye` — check plugin status |
| Port 8080 in use | Update the `web-leaderboard` port mapping in `compose.yaml`, then run `make rebuild` |
| Admin panel can't restart containers | Ensure `/var/run/docker.sock` exists on host and `admin-panel` service has `user: root` |

---

## End-to-end checklist

Use this when setting up a new host from zero.

```bash
# 1. Install host dependencies
sudo apt install -y jq
# Install Docker Engine and docker compose v2 separately if missing

# 2. Prepare repo config
make setup

# 3. Edit these files
$EDITOR cs2_main_image/admins.conf
$EDITOR admin-auth.json
$EDITOR config.json

# 4. Start the stack
make start

# 5. Verify runtime health
make status
make logs

# 6. Open services
# Leaderboard: http://<server-ip>:8080
# Admin panel: http://<server-ip>:8081

# 7. Day-2 operations
make update         # after code changes
make apply-config   # after config/admin changes
make rebuild        # after dependency/base-image/forced-CS2 refresh changes
```

---

## Development notes

- **AdminESP confirmed commands** (kgarri/bg-koka-cs2-xray-esp):
  `css_esp_on` · `css_esp_off` · `css_esp "<username>" true|false`
