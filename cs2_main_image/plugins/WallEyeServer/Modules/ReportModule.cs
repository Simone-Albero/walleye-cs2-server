using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System.Text.Json;

namespace WallEyeServer;

public class ReportModule
{
    private readonly WallEyeServer _plugin;
    private readonly string        _dataPath;
    private readonly WallEyeConfig _cfg;
    private readonly WallEyeLog    _log;

    // SteamID reporter → list of reported SteamIDs
    private readonly Dictionary<ulong, List<ulong>> _currentReports = new();
    private readonly HashSet<ulong> _confirmedReports = new();

    public ReportModule(WallEyeServer plugin, string dataPath, WallEyeConfig cfg)
    {
        _plugin   = plugin;
        _dataPath = dataPath;
        _cfg      = cfg;
        _log      = new WallEyeLog(dataPath, nameof(ReportModule));
    }

    public void Initialize()
    {
        // Admin fallback command — not visible to regular players
        _plugin.AddCommand("css_report", "[Admin] Open report menu",
            (player, _) => { if (player != null) OpenMenuForPlayer(player); });
        _log.Info("Initialized.");
    }

    /// <summary>Opens the report menu for ALL active players.</summary>
    public void OpenReportMenuForAll()
    {
        foreach (var p in GetActivePlayers()) OpenMenuForPlayer(p);
        _log.Info("Opened report menu for all active players.");
    }

    /// <summary>Reopens the menu only for players who have NOT yet voted.</summary>
    public void OpenReportMenuForPending()
    {
        foreach (var p in GetActivePlayers())
        {
            if (p.AuthorizedSteamID == null) continue;
            if (!_confirmedReports.Contains(p.AuthorizedSteamID.SteamId64))
                OpenMenuForPlayer(p);
        }
    }

    private void OpenMenuForPlayer(CCSPlayerController player)
    {
        if (!player.IsValid) return;

        var allOthers = GetActivePlayers()
            .Where(p => p.AuthorizedSteamID != null && p != player)
            .ToList();

        // Filter by opposing team if report_scope = "enemy_team"
        // player.TeamNum == 1 → spectator, don't filter (show all)
        var others = (_cfg.Match.ReportScope == "enemy_team" && player.TeamNum != 1)
            ? allOthers.Where(p => p.TeamNum != player.TeamNum).ToList()
            : allOthers;

        if (others.Count == 0 && allOthers.Count > 0)
            others = allOthers;

        var menu = WallEyeMenu.Create(_plugin, "Who do you suspect is the cheater?");
        menu.ExitButton = false;
        var reporterId = player.AuthorizedSteamID?.SteamId64;
        var selectedIds = reporterId.HasValue && _currentReports.TryGetValue(reporterId.Value, out var current)
            ? current
            : [];
        var noCheaterSelected = reporterId.HasValue && _currentReports.ContainsKey(reporterId.Value) && selectedIds.Count == 0;

        if (others.Count == 0)
        {
            menu.AddMenuOption("No eligible suspects online", (_, _) => { }, true);
        }
        else foreach (var suspect in others)
        {
            var s = suspect;
            var suspectId = s.AuthorizedSteamID!.SteamId64;
            var selected = selectedIds.Contains(suspectId);
            menu.AddMenuOption($"{(selected ? "[x]" : "[ ]")} {s.PlayerName}", (reporter, _) =>
            {
                ToggleReport(reporter, s);
                OpenMenuForPlayer(reporter);
            });
        }

        menu.AddMenuOption($"{(noCheaterSelected ? "[x]" : "[ ]")} No cheater", (reporter, _) =>
        {
            SelectNoCheater(reporter);
            OpenMenuForPlayer(reporter);
        });

        menu.AddMenuOption("Confirm selection", (reporter, _) =>
        {
            if (reporter.AuthorizedSteamID == null) return;
            var rid = reporter.AuthorizedSteamID.SteamId64;
            if (!_currentReports.ContainsKey(rid))
            {
                reporter.PrintToChat($"{_cfg.Ui.ChatPrefix} Select a suspect or No cheater before confirming.");
                OpenMenuForPlayer(reporter);
                return;
            }
            _confirmedReports.Add(rid);
            var n = _currentReports[rid].Count;
            _log.Info($"Report confirmed. reporter={rid} suspects={n}");
            reporter.PrintToChat(n == 0
                ? $"{_cfg.Ui.ChatPrefix} Report submitted: no cheater."
                : $"{_cfg.Ui.ChatPrefix} Report submitted ({n} suspect{(n != 1 ? "s" : "")}).");
            WallEyeMenu.Close(reporter);
        });

        WallEyeMenu.Open(_plugin, player, menu);
    }

    private void ToggleReport(CCSPlayerController reporter, CCSPlayerController suspect)
    {
        if (reporter.AuthorizedSteamID == null || suspect.AuthorizedSteamID == null) return;
        var rid = reporter.AuthorizedSteamID.SteamId64;
        var sid = suspect.AuthorizedSteamID.SteamId64;
        if (!_currentReports.ContainsKey(rid)) _currentReports[rid] = [];
        if (_currentReports[rid].Contains(sid))
        {
            _currentReports[rid].Remove(sid);
            _log.Info($"Report suspect removed. reporter={rid} suspect={sid}");
            reporter.PrintToChat($"{_cfg.Ui.ChatPrefix} - {suspect.PlayerName} removed.");
        }
        else
        {
            _currentReports[rid].Add(sid);
            _log.Info($"Report suspect added. reporter={rid} suspect={sid}");
            reporter.PrintToChat($"{_cfg.Ui.ChatPrefix} + {suspect.PlayerName} added.");
        }
    }

    private void SelectNoCheater(CCSPlayerController reporter)
    {
        if (reporter.AuthorizedSteamID == null) return;
        _currentReports[reporter.AuthorizedSteamID.SteamId64] = [];
        _log.Info($"No-cheater report selected. reporter={reporter.AuthorizedSteamID.SteamId64}");
        reporter.PrintToChat($"{_cfg.Ui.ChatPrefix} Selected: no cheater. Confirm to submit.");
    }

    /// <summary>Serializes reports to disk and resets for the next match.</summary>
    public void FlushReports(string matchId)
    {
        var allPlayers = PlayerLookup.ActivePlayers()
            .Where(p => p.AuthorizedSteamID != null)
            .ToDictionary(p => p.AuthorizedSteamID!.SteamId64, p => p.PlayerName);

        var list = _currentReports
            .Where(kv => _confirmedReports.Contains(kv.Key))
            .Select(kv => new
        {
            reporter_steam_id   = kv.Key.ToString(),
            reporter_nickname   = allPlayers.TryGetValue(kv.Key, out var n) ? n : "Unknown",
            suspected_steam_ids = kv.Value.Select(id => id.ToString()).ToList(),
            suspected_nicknames = kv.Value
                .Select(id => allPlayers.TryGetValue(id, out var suspectName) ? suspectName : id.ToString())
                .ToList()
        }).ToList();

        var dir = Path.Combine(_dataPath, "reports");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, $"{matchId}_reports.json"),
            JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        _log.Info($"Flushed reports. match_id={matchId} reports={list.Count}");

        _currentReports.Clear();
        _confirmedReports.Clear();
    }

    private static List<CCSPlayerController> GetActivePlayers() =>
        PlayerLookup.ActivePlayers();
}
