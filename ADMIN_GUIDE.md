# WallEye CS2 — Admin & Developer Mode Guide

This document is for server admins. It covers the web admin panel, the WallEye
Developer Mode (in-game commands), runtime-configurable parameters, and testing
procedures.

---

## Admin Panel (web)

Available at `http://<server-ip>:8081` when the stack is running. Login is
required before any page or API other than the health check is accessible.

| Section | What you can do |
|---|---|
| **Dashboard** | See live status (running / stopped), CPU%, RAM for each container. Restart individual services. |
| **Config** | Edit every field of `config.json` via a form. Clicking "Save & Apply" writes the file and restarts cs2-server, cs2-scorer and cs2-web automatically. |
| **Admins** | Add or remove CS2 in-game admins (backed by `admins.conf`). Changes take effect after the automatic cs2-server restart. |
| **Stats** | KPI summary, paginated match history, per-match detail modal (cheater list, per-reporter results). |

Auth configuration:

- Use `admin-auth.json.example` as a field reference only.
- Generate the real `admin-auth.json` with the helper script or Make target.
- `admin-auth.json` is ignored by git and should stay host-local.
- If you publish the panel on the public internet, terminate HTTPS first and set `cookie_secure: true`.

`admin-auth.json` fields:

- `username`: account name used on the login page.
- `password_hash`: password hash generated with Werkzeug. Do not store a plain-text password here.
- `session_secret`: long random string used to sign the Flask session cookie.
- `cookie_secure`: set to `true` only when the panel is served through HTTPS.

Generate `admin-auth.json` from the project root:

```bash
python3 generate_admin_auth.py --write admin-auth.json
```

Equivalent Make target:

```bash
make admin-auth
```

The script prompts for the username and password, generates a compatible `password_hash`, creates a fresh `session_secret`, and writes the full JSON document. If the panel is behind HTTPS, use:

```bash
python3 generate_admin_auth.py --cookie-secure --write admin-auth.json
```

Or with `make`:

```bash
make admin-auth ADMIN_AUTH_ARGS=--cookie-secure
```

Example:

```json
{
   "username": "admin",
   "password_hash": "scrypt:...generated-hash...",
   "session_secret": "...long-random-secret...",
   "cookie_secure": false
}
```

How to authenticate:

1. Start the stack with `make start` or apply container changes with `make apply-config` if only the admin services changed.
2. Open `http://<server-ip>:8081/login`.
3. Enter the `username` configured in `admin-auth.json`.
4. Enter the plain password that matches the stored `password_hash`.
5. After login you are redirected to the dashboard and can access Dashboard, Config, Admins, and Stats.

If you rotate credentials in `admin-auth.json`, regenerate or edit the file, then recreate the admin panel container so it reloads the configuration.

How to save new CS2 admins from the web UI:

1. Log in to the admin panel.
2. Open the `Admins` page.
3. Add a new row with the in-game name and SteamID64, or remove an old row.
4. Confirm the save action from the page.
5. The panel writes the updated `admins.conf` and automatically restarts `cs2-server`, so the new admin list becomes active.

Before saving a new admin, verify the SteamID64 carefully. The easiest check is to run `css_players` from the server console and copy the 17-digit SteamID64 shown next to the player.

---

## Enabling Developer Mode (in-game commands)

Edit `config.json`:

```json
"dev": {
  "enabled": true,
  "admin_steam_ids": ["76561198XXXXXXXXX"],
  "skip_player_check": false
}
```

Then apply: `make apply-config` (or save via the admin panel Config page after login).

> With `dev.enabled: false` (default) **no** dev commands are registered in-game.

---

## How to find your SteamID64

1. Connect to the server
2. From the server console type `css_players`
3. The 17-digit number next to your name is your SteamID64
4. Alternatively: [steamid.io](https://steamid.io/) or [steamidfinder.com](https://www.steamidfinder.com/)

---

## Available in-game commands

Commands work from **in-game chat** (prefix `!` or `/`) and from the **server console** (`css_` prefix).

### Information

| Command | Description |
|---|---|
| `css_help` | List all dev commands |
| `css_status` | Full status: current phase, players, active cheaters, key parameters |
| `css_players` | Connected players with SteamID64 and team (T/CT) |

### Game cycle control

| Command | Description |
|---|---|
| `css_phase` | Opens a popup to jump to Waiting, Wallhack, Match, Report, or Leaderboard/next cycle |

The popup stays open until closed with `!9`.

### ESP

| Command | Description |
|---|---|
| `css_xray` | Opens a popup with all-player ON/OFF and per-player ESP assignment |

The popup stays open until closed with `!9`.

### Cheater assignment

| Command | Description |
|---|---|
| `css_cheater <name>` | Assign the cheater role (ESP) to a specific player. Works in any phase. |

### Reports

| Command | Description |
|---|---|
| `css_reports` | Open the report menu for all players |

### Runtime configuration

| Command | Description |
|---|---|
| `css_set <key> <value>` | Modify an in-memory parameter (see table below) |
| `css_reload` | Reload all parameters from `config.json` on disk |
| `css_map <map>` | Change map immediately (e.g. `css_map de_inferno`) |

---

## Parameters editable with `css_set`

| Key | Type | Description | Example |
|---|---|---|---|
| `required_players` | int | Players required to start | `css_set required_players 2` |
| `wallhack_duration` | float | Wallhack phase duration (seconds) | `css_set wallhack_duration 30` |
| `report_duration` | float | Report phase duration (seconds) | `css_set report_duration 20` |
| `cheaters_count` | int | Number of cheaters to select | `css_set cheaters_count 2` |
| `cheater_selection` | string | `per_team` = N per team, `global` = N from all players | `css_set cheater_selection global` |
| `report_scope` | string | `all` = everyone, `enemy_team` = opposing team only | `css_set report_scope enemy_team` |
| `restart_delay` | float | Pause before cycle restart (seconds) | `css_set restart_delay 0` |
| `leaderboard_display` | float | In-game leaderboard display duration (seconds) | `css_set leaderboard_display 5` |
| `skip_player_check` | bool | Ignore player count | `css_set skip_player_check true` |
| `points_participation` | int | Points for participation | `css_set points_participation 5` |
| `points_correct_report` | int | Points for a correct report | `css_set points_correct_report 50` |
| `points_wrong_report` | int | Penalty for a wrong report (use negative) | `css_set points_wrong_report -10` |

> **Important:** `css_set` changes are **in-memory only** — lost on restart.
> To make permanent: edit `config.json` and run `css_reload`, or
> use the admin panel Config page (which also triggers a service restart).

---

## Full `config.json` reference

### `match`

| Field | Type | Default | Description |
|---|---|---|---|
| `required_players` | int | 10 | Players needed to start the cycle |
| `wallhack_duration_seconds` | float | 300 | Wallhack phase duration in seconds |
| `report_phase_duration_seconds` | float | 60 | Time to vote in the report menu |
| `restart_delay_seconds` | float | 5 | Pause before automatic restart |
| `leaderboard_display_seconds` | float | 10 | In-game leaderboard display duration |
| `map` | string | "de_dust2" | Game map (applied on server restart) |
| `max_rounds` | int | 30 | Number of rounds per match |
| `cheaters_count` | int | 1 | Number of cheaters to select per match |
| `cheater_selection` | string | "per_team" | `"per_team"` = N per team, `"global"` = N from all |
| `report_scope` | string | "all" | `"all"` = everyone, `"enemy_team"` = opposing team only |

### `scoring`

| Field | Type | Default | Description |
|---|---|---|---|
| `points_participation` | int | 10 | Base points for each reporter who voted |
| `points_correct_report` | int | 30 | Points per correctly identified cheater |
| `points_wrong_report` | int | -20 | Penalty per wrongly reported player |
| `points_no_cheater_correct` | int | 30 | Bonus for correctly voting "no cheater" |
| `points_kill` | int | 2 | Points per kill (from .dem replay) |
| `points_assist` | int | 1 | Points per assist (from .dem replay) |
| `points_death` | int | -1 | Points per death (from .dem replay) |

### `server`

| Field | Type | Default | Description |
|---|---|---|---|
| `port` | int | 27015 | CS2 server UDP port |
| `tv_port` | int | 27020 | SourceTV UDP port (replays) |
| `web_leaderboard_port` | int | 8080 | Internal app setting. Host port publishing is still controlled by `compose.yaml` |
| `server_name` | string | "WallEye CS2 Server" | Name visible in server browser |
| `insecure` | bool | true | Disable VAC (`-insecure` launch flag) |
| `autoupdate` | bool | true | Auto-update CS2 on server start (`-autoupdate` flag) |

### `esp`

| Field | Description |
|---|---|
| `cmd_enable_all` | Command to enable ESP for all (default: `css_esp_on`) |
| `cmd_disable_all` | Command to disable ESP for all (default: `css_esp_off`) |
| `cmd_enable_player` | Command to enable ESP for one player (`{name}` = name placeholder) |
| `cmd_disable_player` | Command to disable ESP for one player |

### `ui`

| Field | Type | Default | Description |
|---|---|---|---|
| `rules_display_seconds` | float | 15 | Duration of the rules overlay on connect |
| `rules_delay_on_connect_seconds` | float | 3 | Delay before showing rules |
| `report_menu_open_delay_seconds` | float | 3 | Delay before opening the report menu |
| `wallhack_warning_before_end_seconds` | float | 60 | Advance warning before wallhack ends |
| `chat_prefix` | string | "[WallEye]" | Plugin chat message prefix |

### `dev`

| Field | Type | Default | Description |
|---|---|---|---|
| `enabled` | bool | false | Enables dev commands. `false` = no commands registered |
| `admin_steam_ids` | string[] | [] | SteamID64 of players with admin access |
| `skip_player_check` | bool | false | Skip player count wait at cycle start |

---

## Test procedure — Admin alone on the server

To verify full functionality without other players:

### Initial configuration

```json
"dev": {
  "enabled": true,
  "admin_steam_ids": ["YOUR_STEAMID64_HERE"],
  "skip_player_check": true
}
```

Apply: `make apply-config`

What it does now:
revalidates `config.json`, then recreates `cs2-server`, `scoring-service`, `web-leaderboard`, and `admin-panel` without rebuilding images.

### Test sequence

```
1. Connect to the server
   → With skip_player_check: true the wallhack phase starts automatically on login

2. Verify wallhack phase
   → Chat: "[WallEye] Dev: skip_player_check active — starting wallhack immediately."
   → ESP should be visible for all
   → Check: css_status → state=WallhackPhase

3. End wallhack
   css_phase → Live match
   → Chat: "Wallhack DISABLED. The real match starts now!"
   → If selected as cheater: ESP active only for you

4. Verify cheater assignment
   css_status → "Cheaters: <yourName>"
   Or manually assign: css_cheater <yourName>

5. End the match
   css_phase → Report phase
   → Report menu opens after report_menu_open_delay_seconds

6. Verify report menu
   → ChatMenu with player list
   → If report_scope=enemy_team: verify it shows only the opposing team
   Manual alternative: css_reports

7. End report phase
   css_phase → Leaderboard / next cycle
   → TOP 10 leaderboard appears for leaderboard_display_seconds
   → Cycle restarts automatically

8. Verify scoring
   → Open http://<server-ip>:8081/stats and log in
   → Match should appear with cheater IDs and report results
   → Or: check data/matches.json and data/players.json
```

### Quick config test

```
css_set wallhack_duration 15
css_set report_duration 15
css_set leaderboard_display 3
css_set restart_delay 2
css_status   ← verify the values
```

### Report scope test

```
css_set report_scope enemy_team
css_reports   ← menu shows only the opposing team

css_set report_scope all
css_reports   ← menu shows everyone
```

### Manual ESP test

```
css_xray   ← choose all-player ON/OFF or a player from the popup
```

### Map change test

```
css_map de_inferno   ← changes map (server restarts on the new map)
```

---

## Security notes

- Dev commands are accessible **only** to SteamID64 entries in `dev.admin_steam_ids`
- With `dev.enabled: false` (default) **no** dev commands are registered
- `css_set` modifies only **in-process memory** — it does not alter `config.json` on disk
- `Dev.AdminSteamIds` and `Dev.Enabled` are **never updated** by `css_reload`
  (require `make apply-config` or `docker compose restart cs2-server` — prevents privilege escalation)
- The admin panel has **no authentication** — keep port 8081 bound to localhost only

---

## Recommended operator workflow

Use the Makefile as the single entrypoint:

```bash
make setup         # first-time template/config preparation
make start         # build + cs2-base if needed + start full stack
make status        # see endpoints and container state
make logs          # cs2-server logs
make update        # after code changes
make apply-config  # after config.json or admins.conf changes
make rebuild       # after forced full rebuild / CS2 refresh
make down          # stop everything
```

`make up` is safe to use after a reboot because it follows the same bootstrap flow as `make start` and skips `cs2-base` if the shared volume marker already exists.
