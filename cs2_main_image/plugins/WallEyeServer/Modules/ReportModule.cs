using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Menu;
using System.Text.Json;

namespace WallEyeServer;

public class ReportModule
{
    private readonly WallEyeServer _plugin;
    private readonly string        _dataPath;
    private readonly WallEyeConfig _cfg;

    // SteamID reporter → list of reported SteamIDs
    private readonly Dictionary<ulong, List<ulong>> _currentReports = new();

    public ReportModule(WallEyeServer plugin, string dataPath, WallEyeConfig cfg)
    {
        _plugin   = plugin;
        _dataPath = dataPath;
        _cfg      = cfg;
    }

    public void Initialize()
    {
        // Admin fallback command — not visible to regular players
        _plugin.AddCommand("css_report", "[Admin] Open report menu",
            (player, _) => { if (player != null) OpenMenuForPlayer(player); });
    }

    /// <summary>Opens the report menu for ALL active players.</summary>
    public void OpenReportMenuForAll()
    {
        foreach (var p in GetActivePlayers()) OpenMenuForPlayer(p);
    }

    /// <summary>Reopens the menu only for players who have NOT yet voted.</summary>
    public void OpenReportMenuForPending()
    {
        foreach (var p in GetActivePlayers())
        {
            if (p.AuthorizedSteamID == null) continue;
            if (!_currentReports.ContainsKey(p.AuthorizedSteamID.SteamId64))
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

        if (others.Count == 0) { SubmitNoReport(player); return; }

        var menu = new ChatMenu($"{_cfg.Ui.ChatPrefix} Who do you suspect is the cheater?");

        foreach (var suspect in others)
        {
            var s = suspect;
            menu.AddMenuOption(s.PlayerName, (reporter, _) =>
            {
                AddReport(reporter, s);
                _plugin.AddTimer(0.5f, () => { if (reporter.IsValid) OpenMenuForPlayer(reporter); });
            });
        }

        menu.AddMenuOption("✅ Confirm selection", (reporter, _) =>
        {
            if (reporter.AuthorizedSteamID == null) return;
            var rid = reporter.AuthorizedSteamID.SteamId64;
            // If the player confirmed without selecting any suspect, treat it as
            // a "no cheater" vote so FlushReports includes them and they receive
            // participation points.
            if (!_currentReports.ContainsKey(rid))
                SubmitNoReport(reporter);
            else
            {
                var n = _currentReports[rid].Count;
                reporter.PrintToChat($"{_cfg.Ui.ChatPrefix} Report submitted ({n} suspect{(n != 1 ? "s" : "")}).");
            }
        });

        menu.AddMenuOption("No cheater", (reporter, _) => SubmitNoReport(reporter));

        MenuManager.OpenChatMenu(player, menu);
    }

    private void AddReport(CCSPlayerController reporter, CCSPlayerController suspect)
    {
        if (reporter.AuthorizedSteamID == null || suspect.AuthorizedSteamID == null) return;
        var rid = reporter.AuthorizedSteamID.SteamId64;
        var sid = suspect.AuthorizedSteamID.SteamId64;
        if (!_currentReports.ContainsKey(rid)) _currentReports[rid] = [];
        if (!_currentReports[rid].Contains(sid)) _currentReports[rid].Add(sid);
        reporter.PrintToChat($"{_cfg.Ui.ChatPrefix} + {suspect.PlayerName} added. Select more or confirm.");
    }

    private void SubmitNoReport(CCSPlayerController reporter)
    {
        if (reporter.AuthorizedSteamID == null) return;
        _currentReports[reporter.AuthorizedSteamID.SteamId64] = [];
        reporter.PrintToChat($"{_cfg.Ui.ChatPrefix} You reported: no cheater.");
    }

    /// <summary>Serializes reports to disk and resets for the next match.</summary>
    public void FlushReports(string matchId)
    {
        var allPlayers = Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && p.AuthorizedSteamID != null)
            .ToDictionary(p => p.AuthorizedSteamID!.SteamId64, p => p.PlayerName);

        var list = _currentReports.Select(kv => new
        {
            reporter_steam_id   = kv.Key.ToString(),
            reporter_nickname   = allPlayers.TryGetValue(kv.Key, out var n) ? n : "Unknown",
            suspected_steam_ids = kv.Value.Select(id => id.ToString()).ToList()
        }).ToList();

        var dir = Path.Combine(_dataPath, "reports");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, $"{matchId}_reports.json"),
            JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));

        _currentReports.Clear();
    }

    private static List<CCSPlayerController> GetActivePlayers() =>
        Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.Connected)
            .ToList();
}
