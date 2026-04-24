using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WallEyeServer;

public class LeaderboardModule
{
    private readonly WallEyeServer _plugin;
    private readonly string        _dataPath;
    private readonly WallEyeConfig _cfg;

    public LeaderboardModule(WallEyeServer plugin, string dataPath, WallEyeConfig cfg)
    {
        _plugin   = plugin;
        _dataPath = dataPath;
        _cfg      = cfg;
    }

    public void Initialize()
    {
        _plugin.AddCommand("css_top",  "TOP 10 WallEye", OnTopCommand);
        _plugin.AddCommand("css_rank", "Personal rank", OnRankCommand);
    }

    /// <summary>Shows TOP 10 in center HTML to all players for LeaderboardDisplay seconds.</summary>
    public void ShowLeaderboardForAll()
    {
        var top = LoadTopPlayers(10);
        if (top.Count == 0) return;

        var html = "<font color='#FFD700' size='18'><b>🏆 TOP 10 WALLEYE</b></font><br>" +
                   "<font color='#FFFFFF' size='13'>" +
                   string.Join("<br>", top.Select((p, i) =>
                       $"{i + 1}. {p.nickname} — {p.total_points} pt (✅{p.correct_reports} ❌{p.wrong_reports})")) +
                   "</font>";

        foreach (var player in Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.Connected))
        {
            player.PrintToCenterHtml(html, (int)_cfg.Match.LeaderboardDisplay);
        }
    }

    private void OnTopCommand(CCSPlayerController? player, CommandInfo info)
    {
        var top = LoadTopPlayers(10);
        if (top.Count == 0) { player?.PrintToChat($"{_cfg.Ui.ChatPrefix} Leaderboard is empty."); return; }

        var html = "<font color='#FFD700' size='18'><b>TOP 10 WALLEYE</b></font><br>" +
                   "<font color='#FFFFFF' size='13'>" +
                   string.Join("<br>", top.Select((p, i) =>
                       $"{i + 1}. {p.nickname} — {p.total_points} pt (✅{p.correct_reports} ❌{p.wrong_reports})")) +
                   "</font>";

        player?.PrintToCenterHtml(html, 12);
    }

    private void OnRankCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player?.AuthorizedSteamID == null) return;
        var all    = LoadAllPlayers();
        var sorted = all.OrderByDescending(p => p.total_points).ToList();
        var myId   = player.AuthorizedSteamID.SteamId64.ToString();
        var me     = all.FirstOrDefault(p => p.steam_id == myId);

        if (me == null) { player.PrintToChat($"{_cfg.Ui.ChatPrefix} You are not on the leaderboard yet."); return; }

        int rank = sorted.FindIndex(p => p.steam_id == myId) + 1;
        player.PrintToChat(
            $"{_cfg.Ui.ChatPrefix} #{rank}/{sorted.Count} | {me.total_points} pt | ✅{me.correct_reports} ❌{me.wrong_reports}");
    }

    private record PlayerEntry(string steam_id, string nickname, int total_points, int correct_reports, int wrong_reports);

    private List<PlayerEntry> LoadTopPlayers(int n) =>
        LoadAllPlayers().OrderByDescending(p => p.total_points).Take(n).ToList();

    private List<PlayerEntry> LoadAllPlayers()
    {
        var path = Path.Combine(_dataPath, "players.json");
        if (!File.Exists(path)) return [];
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is not JsonObject obj) return [];
            return obj.Select(kv => new PlayerEntry(
                kv.Key,
                kv.Value?["nickname"]?.GetValue<string>()     ?? "",
                kv.Value?["total_points"]?.GetValue<int>()    ?? 0,
                kv.Value?["correct_reports"]?.GetValue<int>() ?? 0,
                kv.Value?["wrong_reports"]?.GetValue<int>()   ?? 0
            )).ToList();
        }
        catch { return []; }
    }
}
