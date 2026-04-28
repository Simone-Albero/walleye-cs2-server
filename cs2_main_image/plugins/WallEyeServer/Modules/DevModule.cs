using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;

namespace WallEyeServer;

public class DevModule
{
    private readonly WallEyeServer _plugin;
    private readonly WallEyeConfig _cfg;
    private readonly MatchManager  _matchManager;
    private readonly ReportModule  _reportModule;
    private readonly WallEyeLog    _log;

    public DevModule(WallEyeServer plugin, WallEyeConfig cfg, MatchManager matchManager, ReportModule reportModule)
    {
        _plugin       = plugin;
        _cfg          = cfg;
        _matchManager = matchManager;
        _reportModule = reportModule;
        _log          = new WallEyeLog(cfg.Server.DataPath, nameof(DevModule));
    }

    public void Initialize()
    {
        if (!_cfg.Dev.Enabled) return;

        _plugin.AddCommand("css_help",         "[Dev] List commands",                 OnHelp);
        _plugin.AddCommand("css_status",       "[Dev] Full plugin status",            OnStatus);
        _plugin.AddCommand("css_players",      "[Dev] List players + SteamID",        OnListPlayers);
        _plugin.AddCommand("css_phase",        "[Dev] Choose match phase",            OnPhaseMenu);
        _plugin.AddCommand("css_cheater",      "[Dev] Assign cheater by name",        OnSetCheater);
        _plugin.AddCommand("css_xray",         "[Dev] Open ESP control menu",         OnXrayMenu);
        _plugin.AddCommand("css_reports",      "[Dev] Open report menu for all",      OnOpenReports);
        _plugin.AddCommand("css_set",          "[Dev] Modify config parameter",       OnSet);
        _plugin.AddCommand("css_reload",       "[Dev] Reload config.json",            OnReloadConfig);
        _plugin.AddCommand("css_map",          "[Dev] Change map",                    OnMap);
        _log.Info("Initialized.");
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
            "!status                 - plugin status",
            "!players                - players + SteamID + team",
            "!phase                  - choose match phase",
            "!cheater <name>         - assign ESP to player",
            "!xray                   - ESP control menu",
            "!reports                - open report menu",
            "!set <key> <val>        - modify in-memory config",
            "!reload                 - reload config.json from disk",
            "!map <map>              - change map",
        };
        foreach (var c in cmds) Reply(player, c);
    }

    private void OnStatus(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        var cheaterNames = _matchManager.GetCurrentCheaterNames();
        var lines = new[]
        {
            _matchManager.GetStatusString(),
            $"Cheaters: {(cheaterNames.Count > 0 ? string.Join(", ", cheaterNames) : "none")}",
            $"ReportScope={_cfg.Match.ReportScope}",
            $"CheatersCount={_cfg.Match.CheatersCount}",
            $"Selection={_cfg.Match.CheaterSelection}",
            $"Map={_cfg.Match.Map}",
            $"SkipPlayerCheck={_cfg.Dev.SkipPlayerCheck}",
            $"WarmupDuration={_cfg.Match.WarmupDuration}s"
        };

        if (player != null) OpenInfoPopup(player, "WallEye Status", lines);
        else foreach (var line in lines) Reply(player, line);
    }

    private void OnListPlayers(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        var players = PlayerLookup.ActivePlayers();
        if (!players.Any()) { Reply(player, "No players connected."); return; }

        var lines = players.Select(p =>
        {
            var team = p.TeamNum switch { 2 => "T", 3 => "CT", _ => "?" };
            return $"[{team}] {p.PlayerName} - {p.AuthorizedSteamID?.SteamId64 ?? 0}";
        }).ToList();

        if (player != null) OpenInfoPopup(player, $"Players ({players.Count})", lines);
        else foreach (var line in lines) Reply(player, line);
    }

    private void OpenInfoPopup(CCSPlayerController player, string title, IEnumerable<string> lines)
    {
        if (!player.IsValid) return;

        var menu = WallEyeMenu.CreateInfo(_plugin, title, lines);
        menu.ExitButton = false;
        WallEyeMenu.Open(_plugin, player, menu, autoCloseSeconds: 30);
    }

    private CenterHtmlMenu CreatePopup(string title, string enabledColor) =>
        WallEyeMenu.Create(_plugin, title, enabledColor);

    private static List<CCSPlayerController> GetActivePlayers() =>
        PlayerLookup.ActivePlayers();

    // ── Cycle control commands ───────────────────────────────────────────────

    private void OnPhaseMenu(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        if (player == null) { Reply(player, "Open this from in-game chat to use the phase popup."); return; }

        var menu = CreatePopup("Choose Phase", "lime");
        AddPhaseOption(menu, "Waiting for players", MatchState.WaitingForPlayers);
        AddPhaseOption(menu, "Warmup phase", MatchState.WarmupPhase);
        AddPhaseOption(menu, "Live match", MatchState.MatchRunning);
        AddPhaseOption(menu, "Report phase", MatchState.ReportPhase);
        WallEyeMenu.Open(_plugin, player, menu);
    }

    private void AddPhaseOption(CenterHtmlMenu menu, string label, MatchState target)
    {
        menu.AddMenuOption(label, (reporter, _) =>
        {
            WallEyeMenu.Close(reporter);
            var ok = _matchManager.ForcePhase(target);
            _log.Info($"Admin {reporter.PlayerName} forced phase {target}. success={ok}");
            reporter.PrintToChat(ok
                ? $"{_cfg.Ui.ChatPrefix} [Dev] Phase changed to {target}."
                : $"{_cfg.Ui.ChatPrefix} [Dev] Could not change phase.");
        });
    }

    // ── Cheater / ESP commands ─────────────────────────────────────────────────

    private void OnSetCheater(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        if (info.ArgCount < 2) { Reply(player, "Usage: !cheater <partial name>"); return; }
        var name = info.GetArg(1);
        var ok   = _matchManager.ForceAssignCheater(name);
        _log.Info($"Set cheater command. requester={(player?.PlayerName ?? "console")} query={name} success={ok}");
        Reply(player, ok ? $"ESP assigned to '{name}'." : $"Player '{name}' not found.");
    }

    private void OnXrayMenu(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        if (player == null) { Reply(player, "Open this from in-game chat to use the ESP popup."); return; }

        var menu = CreatePopup("ESP Controls", "lime");
        menu.AddMenuOption("<font color='#7CFF8A'><b>ENABLE ALL PLAYERS</b></font>", (reporter, _) =>
        {
            _matchManager.SetDeveloperEspForAll(true);
            _log.Info($"Admin {reporter.PlayerName} enabled ESP for all.");
            reporter.PrintToChat($"{_cfg.Ui.ChatPrefix} [Dev] ESP enabled for all.");
            OnXrayMenu(reporter, info);
        });
        menu.AddMenuOption("<font color='#FF6B6B'><b>DISABLE ALL PLAYERS</b></font>", (reporter, _) =>
        {
            _matchManager.SetDeveloperEspForAll(false);
            _log.Info($"Admin {reporter.PlayerName} disabled ESP for all.");
            reporter.PrintToChat($"{_cfg.Ui.ChatPrefix} [Dev] ESP disabled for all.");
            OnXrayMenu(reporter, info);
        });

        foreach (var target in GetActivePlayers())
        {
            var name = target.PlayerName;
            var enabled = _matchManager.IsDeveloperEspEnabled(target);
            var action = enabled ? "DISABLE" : "ENABLE";
            var color = enabled ? "#FF6B6B" : "#7CFF8A";
            menu.AddMenuOption($"<font color='{color}'><b>{action}</b></font> {name}", (reporter, _) =>
            {
                var ok = _matchManager.SetDeveloperEspForPlayer(name, !enabled);
                _log.Info($"Admin {reporter.PlayerName} toggled ESP for {name}. enabled={!enabled} success={ok}");
                reporter.PrintToChat(ok
                    ? $"{_cfg.Ui.ChatPrefix} [Dev] ESP {(enabled ? "disabled" : "enabled")} for {name}."
                    : $"{_cfg.Ui.ChatPrefix} [Dev] Player '{name}' not found.");
                OnXrayMenu(reporter, info);
            });
        }

        WallEyeMenu.Open(_plugin, player, menu);
    }

    // ── Report commands ────────────────────────────────────────────────────────

    private void OnOpenReports(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        _reportModule.OpenReportMenuForAll(Array.Empty<ulong>());
        _log.Info($"Admin {(player?.PlayerName ?? "console")} opened report menu for all players.");
        Reply(player, "Report menu opened for all players.");
    }

    // ── Config commands ────────────────────────────────────────────────

    private void OnSet(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        if (info.ArgCount < 3) { Reply(player, "Usage: !set <key> <value>"); return; }
        var key = info.GetArg(1).ToLower();
        var val = info.GetArg(2);
        var ok = ApplyConfigSet(key, val, out var msg);
        _log.Info($"Config set command. requester={(player?.PlayerName ?? "console")} key={key} value={val} success={ok} message={msg}");
        Reply(player, ok ? $"✅ {msg}" : $"❌ {msg}");
    }

    private bool ApplyConfigSet(string key, string value, out string message)
    {
        switch (key)
        {
            case "required_players":
                if (!int.TryParse(value, out int rp) || rp < 1) { message = "Integer >= 1 required."; return false; }
                _cfg.Match.RequiredPlayers = rp; message = $"required_players = {rp}"; return true;

            case "warmup_duration":
                if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float wd) || wd < 1)
                { message = "Float >= 1 required."; return false; }
                _cfg.Match.WarmupDuration = wd; message = $"warmup_duration_seconds = {wd}"; return true;

            case "report_duration":
                if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float rd) || rd < 1)
                { message = "Float >= 1 required."; return false; }
                _cfg.Match.ReportDuration = rd; message = $"report_phase_duration_seconds = {rd}"; return true;

            case "cheaters_count":
                if (!int.TryParse(value, out int ct) || ct < 0) { message = "Integer >= 0 required."; return false; }
                _cfg.Match.CheatersCount = ct; message = $"cheaters_count = {ct}"; return true;

            case "max_rounds":
                if (!int.TryParse(value, out int mr) || mr < 1) { message = "Integer >= 1 required."; return false; }
                _cfg.Match.MaxRounds = mr;
                Server.ExecuteCommand($"mp_maxrounds {_matchManager.GetEngineMaxRounds()}");
                message = $"max_rounds = {mr}";
                return true;

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
                message = $"Unknown key: '{key}'. Use !help for the list.";
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
            _cfg.Match.WarmupDuration     = fresh.Match.WarmupDuration;
            _cfg.Match.ReportDuration     = fresh.Match.ReportDuration;
            _cfg.Match.RestartDelay       = fresh.Match.RestartDelay;
            _cfg.Match.Map                = fresh.Match.Map;
            _cfg.Match.MaxRounds          = fresh.Match.MaxRounds;
            _cfg.Match.CheatersCount      = fresh.Match.CheatersCount;
            _cfg.Match.CheaterSelection   = fresh.Match.CheaterSelection;
            _cfg.Match.ReportScope        = fresh.Match.ReportScope;

            _cfg.Scoring.PtsParticipation = fresh.Scoring.PtsParticipation;
            _cfg.Scoring.PtsCorrect       = fresh.Scoring.PtsCorrect;
            _cfg.Scoring.PtsWrong         = fresh.Scoring.PtsWrong;
            _cfg.Scoring.PtsNoCheater     = fresh.Scoring.PtsNoCheater;
            _cfg.Scoring.PtsKill          = fresh.Scoring.PtsKill;
            _cfg.Scoring.PtsAssist        = fresh.Scoring.PtsAssist;
            _cfg.Scoring.PtsDeath         = fresh.Scoring.PtsDeath;

            _cfg.Ui.ReportMenuDelay                = fresh.Ui.ReportMenuDelay;
            _cfg.Ui.ChatPrefix                     = fresh.Ui.ChatPrefix;

            _cfg.Dev.SkipPlayerCheck      = fresh.Dev.SkipPlayerCheck;
            // Dev.Enabled and Dev.AdminSteamIds are NOT updated (security — require restart)

            Server.ExecuteCommand($"mp_maxrounds {_matchManager.GetEngineMaxRounds()}");
            _log.Info($"Config reloaded from disk by {(player?.PlayerName ?? "console")}.");
            Reply(player, "✅ Config reloaded from disk.");
        }
        catch (Exception e)
        {
            _log.Error("Config reload failed", e);
            Reply(player, $"❌ Error: {e.Message}");
        }
    }

    private void OnMap(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsAdmin(player)) { Deny(player); return; }
        if (info.ArgCount < 2) { Reply(player, "Usage: !map <map_name>"); return; }
        var mapName = info.GetArg(1);
        if (!IsSafeMapName(mapName))
        {
            Reply(player, "Invalid map name. Use letters, numbers, underscore, hyphen, or slash.");
            return;
        }

        _cfg.Match.Map = mapName;
        _log.Info($"Map change requested by {(player?.PlayerName ?? "console")}. map={mapName}");
        Reply(player, $"Changing map to '{mapName}' in 2 seconds...");
        _plugin.AddTimer(2f, () => Server.ExecuteCommand($"changelevel {mapName}"));
    }

    private static bool IsSafeMapName(string mapName) =>
        !string.IsNullOrWhiteSpace(mapName) &&
        mapName.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '/');
}
