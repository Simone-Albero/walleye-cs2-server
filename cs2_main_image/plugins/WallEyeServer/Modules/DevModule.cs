using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace WallEyeServer;

public class DevModule
{
    private readonly WallEyeServer _plugin;
    private readonly WallEyeConfig _cfg;
    private readonly MatchManager  _matchManager;
    private readonly ReportModule  _reportModule;

    public DevModule(WallEyeServer plugin, WallEyeConfig cfg, MatchManager matchManager, ReportModule reportModule)
    {
        _plugin       = plugin;
        _cfg          = cfg;
        _matchManager = matchManager;
        _reportModule = reportModule;
    }

    public void Initialize()
    {
        if (!_cfg.Dev.Enabled) return;

        _plugin.AddCommand("css_we_help",               "[Dev] List commands",                 OnHelp);
        _plugin.AddCommand("css_we_status",             "[Dev] Full plugin status",            OnStatus);
        _plugin.AddCommand("css_we_list_players",       "[Dev] List players + SteamID",        OnListPlayers);
        _plugin.AddCommand("css_we_skip_waiting",       "[Dev] Skip player wait",              OnSkipWaiting);
        _plugin.AddCommand("css_we_force_wallhack_end", "[Dev] End wallhack phase",            OnForceWallhackEnd);
        _plugin.AddCommand("css_we_force_match_end",    "[Dev] End match",                     OnForceMatchEnd);
        _plugin.AddCommand("css_we_force_report_end",   "[Dev] End report phase",              OnForceReportEnd);
        _plugin.AddCommand("css_we_set_cheater",        "[Dev] Assign cheater by name",        OnSetCheater);
        _plugin.AddCommand("css_we_esp_on",             "[Dev] ESP on for all",                OnEspOn);
        _plugin.AddCommand("css_we_esp_off",            "[Dev] ESP off for all",               OnEspOff);
        _plugin.AddCommand("css_we_esp_player",         "[Dev] Toggle ESP for single player",  OnEspPlayer);
        _plugin.AddCommand("css_we_open_reports",       "[Dev] Open report menu for all",      OnOpenReports);
        _plugin.AddCommand("css_we_set",                "[Dev] Modify config parameter",       OnSet);
        _plugin.AddCommand("css_we_reload_config",      "[Dev] Reload config.json",            OnReloadConfig);
        _plugin.AddCommand("css_we_map",                "[Dev] Change map",                    OnMap);
    }

    // ── Admin guard ───────────────────────────────────────────────────────────

    private bool IsAdmin(CCSPlayerController? player)
    {
        if (player == null) return true; // console = always admin
        if (player.AuthorizedSteamID == null) return false;
        return _cfg.Dev.AdminSteamIds.Contains(player.AuthorizedSteamID.SteamId64.ToString());
    }

    private void Deny(CCSPlayerController? player) =>
        player?.PrintToChat($"{_cfg.Ui.ChatPrefix} [Dev] Access denied.");

    private void Reply(CCSPlayerController? player, string msg) =>
        player?.PrintToChat($"{_cfg.Ui.ChatPrefix} [Dev] {msg}");

    // ── Informational commands ───────────────────────────────────────────────────

    private void OnHelp(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        var cmds = new[]
        {
            "css_we_status              — plugin status",
            "css_we_list_players        — players + SteamID + team",
            "css_we_skip_waiting        — start wallhack immediately",
            "css_we_force_wallhack_end  — end wallhack → match",
            "css_we_force_match_end     — end match → report",
            "css_we_force_report_end    — end report → new cycle",
            "css_we_set_cheater <name>  — assign ESP to player",
            "css_we_esp_on              — ESP on for all",
            "css_we_esp_off             — ESP off for all",
            "css_we_esp_player <n> on|off — single player ESP",
            "css_we_open_reports        — open report menu",
            "css_we_set <key> <val>     — modify in-memory config",
            "css_we_reload_config       — reload config.json from disk",
            "css_we_map <map>           — change map",
        };
        foreach (var c in cmds) Reply(player, c);
    }

    private void OnStatus(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        var cheaterNames = _matchManager.GetCurrentCheaterNames();
        Reply(player, _matchManager.GetStatusString());
        Reply(player, $"Cheaters: {(cheaterNames.Count > 0 ? string.Join(", ", cheaterNames) : "none")}");
        Reply(player, $"ReportScope={_cfg.Match.ReportScope} | CheatersCount={_cfg.Match.CheatersCount} | CheaterSelection={_cfg.Match.CheaterSelection} | Map={_cfg.Match.Map}");
        Reply(player, $"SkipPlayerCheck={_cfg.Dev.SkipPlayerCheck} | WallhackDuration={_cfg.Match.WallhackDuration}s");
    }

    private void OnListPlayers(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        var players = Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.Connected)
            .ToList();
        if (!players.Any()) { Reply(player, "No players connected."); return; }
        foreach (var p in players)
        {
            var team = p.TeamNum switch { 2 => "T", 3 => "CT", _ => "?" };
            Reply(player, $"[{team}] {p.PlayerName} — {p.AuthorizedSteamID?.SteamId64 ?? 0}");
        }
    }

    // ── Cycle control commands ───────────────────────────────────────────────

    private void OnSkipWaiting(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        var ok = _matchManager.ForceSkipWaiting();
        Reply(player, ok ? "Wallhack started!" : $"Not possible: state={_matchManager.GetCurrentState()}");
    }

    private void OnForceWallhackEnd(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        var ok = _matchManager.ForceEndWallhack();
        Reply(player, ok ? "Wallhack phase ended." : $"Not possible: state={_matchManager.GetCurrentState()}");
    }

    private void OnForceMatchEnd(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        var ok = _matchManager.ForceMatchEnd();
        Reply(player, ok ? "Match ended — report phase started." : $"Not possible: state={_matchManager.GetCurrentState()}");
    }

    private void OnForceReportEnd(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        var ok = _matchManager.ForceEndReportPhase();
        Reply(player, ok ? "Report phase ended — new cycle started." : $"Not possible: state={_matchManager.GetCurrentState()}");
    }

    // ── Cheater / ESP commands ─────────────────────────────────────────────────

    private void OnSetCheater(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        if (info.ArgCount < 2) { Reply(player, "Usage: css_we_set_cheater <partial name>"); return; }
        var name = info.GetArg(1);
        var ok   = _matchManager.ForceAssignCheater(name);
        Reply(player, ok ? $"ESP assigned to '{name}'." : $"Player '{name}' not found.");
    }

    private void OnEspOn(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        _matchManager.SetDeveloperEspForAll(true);
        Reply(player, "ESP enabled for all.");
    }

    private void OnEspOff(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        _matchManager.SetDeveloperEspForAll(false);
        Reply(player, "ESP disabled for all.");
    }

    private void OnEspPlayer(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        if (info.ArgCount < 3) { Reply(player, "Usage: css_we_esp_player <name> on|off"); return; }
        var name   = info.GetArg(1);
        var state  = info.GetArg(2).ToLower();
        var enabled = state == "on";
        if (state != "on" && state != "off") { Reply(player, "Usage: css_we_esp_player <name> on|off"); return; }
        var ok = _matchManager.SetDeveloperEspForPlayer(name, enabled);
        Reply(player, ok ? $"ESP {state.ToUpper()} per {name}." : $"Player '{name}' not found.");
    }

    // ── Report commands ────────────────────────────────────────────────────────

    private void OnOpenReports(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        _reportModule.OpenReportMenuForAll();
        Reply(player, "Report menu opened for all players.");
    }

    // ── Config commands ────────────────────────────────────────────────

    private void OnSet(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        if (info.ArgCount < 3) { Reply(player, "Usage: css_we_set <key> <value>"); return; }
        var key = info.GetArg(1).ToLower();
        var val = info.GetArg(2);
        Reply(player, ApplyConfigSet(key, val, out var msg) ? $"✅ {msg}" : $"❌ {msg}");
    }

    private bool ApplyConfigSet(string key, string value, out string message)
    {
        switch (key)
        {
            case "required_players":
                if (!int.TryParse(value, out int rp) || rp < 1) { message = "Integer >= 1 required."; return false; }
                _cfg.Match.RequiredPlayers = rp; message = $"required_players = {rp}"; return true;

            case "wallhack_duration":
                if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float wd) || wd < 1)
                { message = "Float >= 1 required."; return false; }
                _cfg.Match.WallhackDuration = wd; message = $"wallhack_duration_seconds = {wd}"; return true;

            case "report_duration":
                if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float rd) || rd < 1)
                { message = "Float >= 1 required."; return false; }
                _cfg.Match.ReportDuration = rd; message = $"report_phase_duration_seconds = {rd}"; return true;

            case "cheaters_count":
                if (!int.TryParse(value, out int ct) || ct < 0) { message = "Integer >= 0 required."; return false; }
                _cfg.Match.CheatersCount = ct; message = $"cheaters_count = {ct}"; return true;

            case "cheater_selection":
                if (value != "global" && value != "per_team") { message = "Valid values: global, per_team"; return false; }
                _cfg.Match.CheaterSelection = value; message = $"cheater_selection = {value}"; return true;

            case "report_scope":
                if (value != "all" && value != "enemy_team") { message = "Valid values: all, enemy_team"; return false; }
                _cfg.Match.ReportScope = value; message = $"report_scope = {value}"; return true;

            case "restart_delay":
                if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float rsd) || rsd < 0)
                { message = "Float >= 0 required."; return false; }
                _cfg.Match.RestartDelay = rsd; message = $"restart_delay_seconds = {rsd}"; return true;

            case "leaderboard_display":
                if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float ld) || ld < 0)
                { message = "Float >= 0 required."; return false; }
                _cfg.Match.LeaderboardDisplay = ld; message = $"leaderboard_display_seconds = {ld}"; return true;

            case "skip_player_check":
                if (!bool.TryParse(value, out bool spc)) { message = "Valid values: true, false"; return false; }
                _cfg.Dev.SkipPlayerCheck = spc; message = $"skip_player_check = {spc}"; return true;

            case "points_participation":
                if (!int.TryParse(value, out int pp)) { message = "Integer required."; return false; }
                _cfg.Scoring.PtsParticipation = pp; message = $"points_participation = {pp}"; return true;

            case "points_correct_report":
                if (!int.TryParse(value, out int pcr)) { message = "Integer required."; return false; }
                _cfg.Scoring.PtsCorrect = pcr; message = $"points_correct_report = {pcr}"; return true;

            case "points_wrong_report":
                if (!int.TryParse(value, out int pwr)) { message = "Integer required."; return false; }
                _cfg.Scoring.PtsWrong = pwr; message = $"points_wrong_report = {pwr}"; return true;

            default:
                message = $"Unknown key: '{key}'. Use css_we_help for the list.";
                return false;
        }
    }

    private void OnReloadConfig(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        try
        {
            var fresh = WallEyeServer.LoadConfig("/config/config.json");

            // Copia tutti i parametri mutabili nel cfg condiviso (stesso oggetto usato da tutti i moduli)
            _cfg.Match.RequiredPlayers    = fresh.Match.RequiredPlayers;
            _cfg.Match.WallhackDuration   = fresh.Match.WallhackDuration;
            _cfg.Match.ReportDuration     = fresh.Match.ReportDuration;
            _cfg.Match.RestartDelay       = fresh.Match.RestartDelay;
            _cfg.Match.LeaderboardDisplay = fresh.Match.LeaderboardDisplay;
            _cfg.Match.Map                = fresh.Match.Map;
            _cfg.Match.MaxRounds          = fresh.Match.MaxRounds;
            _cfg.Match.CheatersCount     = fresh.Match.CheatersCount;
            _cfg.Match.CheaterSelection   = fresh.Match.CheaterSelection;
            _cfg.Match.ReportScope        = fresh.Match.ReportScope;

            _cfg.Scoring.PtsParticipation = fresh.Scoring.PtsParticipation;
            _cfg.Scoring.PtsCorrect       = fresh.Scoring.PtsCorrect;
            _cfg.Scoring.PtsWrong         = fresh.Scoring.PtsWrong;
            _cfg.Scoring.PtsNoCheater     = fresh.Scoring.PtsNoCheater;
            _cfg.Scoring.PtsKill          = fresh.Scoring.PtsKill;
            _cfg.Scoring.PtsAssist        = fresh.Scoring.PtsAssist;
            _cfg.Scoring.PtsDeath         = fresh.Scoring.PtsDeath;

            _cfg.Dev.SkipPlayerCheck      = fresh.Dev.SkipPlayerCheck;
            // Dev.Enabled and Dev.AdminSteamIds are NOT updated (security — require restart)

            Reply(player, "✅ Config reloaded from disk.");
        }
        catch (Exception e)
        {
            Reply(player, $"❌ Error: {e.Message}");
        }
    }

    private void OnMap(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        if (info.ArgCount < 2) { Reply(player, "Usage: css_we_map <map_name>"); return; }
        var mapName = info.GetArg(1);
        _cfg.Match.Map = mapName;
        Reply(player, $"Changing map to '{mapName}' in 2 seconds...");
        _plugin.AddTimer(2f, () => Server.ExecuteCommand($"changelevel {mapName}"));
    }
}
