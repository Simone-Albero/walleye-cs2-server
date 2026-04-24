using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WallEyeServer;

public enum MatchState
{
    WaitingForPlayers,
    WallhackPhase,
    MatchRunning,
    ReportPhase
}

public class MatchManager
{
    private readonly WallEyeServer _plugin;
    private readonly string        _dataPath;
    private readonly WallEyeConfig _cfg;

    private MatchState      _state           = MatchState.WaitingForPlayers;
    private List<ulong>     _cheaterSteamIds = new();
    private int             _matchCounter    = 1;
    private Timer?          _activeTimer;
    private ReportModule      _reportModule      = null!;
    private LeaderboardModule _leaderboardModule = null!;

    public MatchManager(WallEyeServer plugin, string dataPath, WallEyeConfig cfg)
    {
        _plugin   = plugin;
        _dataPath = dataPath;
        _cfg      = cfg;
    }

    public void Initialize(ReportModule reportModule, LeaderboardModule leaderboardModule)
    {
        _reportModule      = reportModule;
        _leaderboardModule = leaderboardModule;

        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect);
        _plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        _plugin.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchWinPanel);

        LoadMatchCounter();
        Server.NextFrame(EnterWaitingState);
    }

    // ── State 1: Waiting for players ───────────────────────────────────────────

    private void EnterWaitingState()
    {
        _state = MatchState.WaitingForPlayers;
        Server.ExecuteCommand("mp_warmup_pausetimer 1");

        if (_cfg.Dev.SkipPlayerCheck)
        {
            Chat("Dev: skip_player_check active — starting wallhack immediately.");
            StartWallhackPhase();
            return;
        }

        Chat("Waiting for players... ({0}/{1})", GetPlayerCount(), _cfg.Match.RequiredPlayers);
    }

    private HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (_state == MatchState.WaitingForPlayers)
        {
            var count = GetPlayerCount();
            Chat("Players connected: {0}/{1}", count, _cfg.Match.RequiredPlayers);

            if (count >= _cfg.Match.RequiredPlayers)
                StartWallhackPhase();

            return HookResult.Continue;
        }

        if (player == null || player.IsBot || !player.IsValid) return HookResult.Continue;

        _plugin.AddTimer(2f, () => ApplyCurrentEspStateToPlayer(player));

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (_state == MatchState.WaitingForPlayers)
            Chat("Players connected: {0}/{1}", GetPlayerCount() - 1, _cfg.Match.RequiredPlayers);
        return HookResult.Continue;
    }

    // ── State 2: Wallhack phase ──────────────────────────────────────────────

    private void StartWallhackPhase()
    {
        _state = MatchState.WallhackPhase;

        // Cheater is selected at the start and will not change for the duration of the match
        _cheaterSteamIds = SelectCheaters();

        SetEspForAllPlayers(true);

        foreach (var p in GetActivePlayers())
        {
            p.PrintToChat($"{_cfg.Ui.ChatPrefix} ⚠ WALLHACK active for {_cfg.Match.WallhackDuration / 60:F0} minutes. Watch for suspicious movement!");
            p.PrintToCenterHtml(
                "<font color='#FF4444' size='22'><b>⚠ WALLHACK PHASE ⚠</b></font><br>" +
                $"<font color='#FFFFFF'>Real match starts in {_cfg.Match.WallhackDuration / 60:F0}:00</font>", 8);
        }

        // Private notification to selected cheaters (only they know)
        foreach (var p in GetActivePlayers())
        {
            if (p.AuthorizedSteamID == null) continue;
            if (_cheaterSteamIds.Contains(p.AuthorizedSteamID.SteamId64))
                p.PrintToChat($"{_cfg.Ui.ChatPrefix} 🎯 You have been selected as the CHEATER for this match. When the wallhack ends, only you will have ESP!");
        }

        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand($"mp_warmuptime {(int)_cfg.Match.WallhackDuration}");

        // Guard: avoid negative timer if WallhackDuration is very short (e.g. testing with 10s)
        if (_cfg.Match.WallhackDuration > _cfg.Ui.WallhackWarningBeforeEndSeconds)
        {
            _plugin.AddTimer(_cfg.Match.WallhackDuration - _cfg.Ui.WallhackWarningBeforeEndSeconds, () =>
                Chat("⏳ {0} seconds until wallhack ends!", (int)_cfg.Ui.WallhackWarningBeforeEndSeconds));
        }

        _activeTimer = _plugin.AddTimer(_cfg.Match.WallhackDuration, EndWallhackPhase);
    }

    private void EndWallhackPhase()
    {
        _state = MatchState.MatchRunning;

        SetEspForAllPlayers(false);
        Chat("Wallhack DISABLED. The real match starts now!");

        // _cheaterSteamIds was already fixed at the start of the wallhack phase
        foreach (var p in GetActivePlayers())
        {
            if (p.AuthorizedSteamID == null) continue;
            if (_cheaterSteamIds.Contains(p.AuthorizedSteamID.SteamId64))
            {
                var cmd = _cfg.Esp.CmdEnablePlayer.Replace("{name}", p.PlayerName);
                Server.ExecuteCommand(cmd);
                p.PrintToChat($"{_cfg.Ui.ChatPrefix} You have been selected as the CHEATER. Use ESP wisely.");
            }
        }

        Server.ExecuteCommand("mp_warmup_end");
        Chat("Match STARTED. Good luck!");
    }

    // ── State 3: End of match ───────────────────────────────────────────────────

    private HookResult OnMatchWinPanel(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (_state != MatchState.MatchRunning) return HookResult.Continue;
        SetEspForAllPlayers(false);
        StartReportPhase();
        return HookResult.Continue;
    }

    // ── State 4: Report phase (fully automatic) ───────────────────────────────

    private void StartReportPhase()
    {
        _state = MatchState.ReportPhase;
        // NOTE: WritePendingMatchFile() is intentionally NOT called here.
        // It is called in StartNewCycle(), after FlushReports() has written the
        // reports file.  If pending_match.json were written now the scoring
        // service would race to read the reports file before it exists and
        // would see 0 reports → everyone gets 0 points.

        foreach (var p in GetActivePlayers())
        {
            p.PrintToCenterHtml(
                "<font color='#FFD700' size='22'><b>MATCH OVER</b></font><br>" +
                $"<font color='#FFFFFF' size='15'>Report menu opens automatically in {_cfg.Ui.ReportMenuDelay:F0} seconds...<br>" +
                $"You have {_cfg.Match.ReportDuration:F0} seconds to vote</font>", 4);
        }
        Chat("🏁 Match over! Report menu opens in {0} seconds.", (int)_cfg.Ui.ReportMenuDelay);

        // Open the menu for all players after 3s
        _plugin.AddTimer(_cfg.Ui.ReportMenuDelay, () => _reportModule.OpenReportMenuForAll());

        // Remind players who haven't voted yet at the halfway point
        _plugin.AddTimer(_cfg.Match.ReportDuration / 2, () =>
        {
            Chat("⏳ {0} seconds remaining. Report the cheater!", (int)(_cfg.Match.ReportDuration / 2));
            _reportModule.OpenReportMenuForPending();
        });

        _activeTimer = _plugin.AddTimer(_cfg.Match.ReportDuration, StartNewCycle);
    }

    // ── Cycle: automatic restart ─────────────────────────────────────────────

    private void StartNewCycle()
    {
        var matchId = $"match_{_matchCounter:D3}";
        _reportModule.FlushReports(matchId);

        // Write pending_match.json only AFTER FlushReports so the scoring
        // service always finds the reports file when it wakes up.
        WritePendingMatchFile();

        _cheaterSteamIds.Clear();
        _matchCounter++;
        SaveMatchCounter();

        // Show leaderboard for LeaderboardDisplay seconds, then restart
        _leaderboardModule.ShowLeaderboardForAll();
        Chat("📊 Leaderboard updated! New match in {0} seconds...",
            (int)(_cfg.Match.RestartDelay + _cfg.Match.LeaderboardDisplay));

        _plugin.AddTimer(_cfg.Match.LeaderboardDisplay + _cfg.Match.RestartDelay, () =>
        {
            Server.ExecuteCommand("mp_restartgame 1");
            EnterWaitingState();
        });
    }

    // ── Selezione cheater con peso anti-ripetizione ──────────────────────────

    private List<ulong> SelectCheaters() =>
        _cfg.Match.CheaterSelection == "global"
            ? SelectGlobal()
            : SelectPerTeam();

    /// <summary>Returns inverse-frequency weights for weighted-random cheater selection.</summary>
    private static List<double> ComputeWeights(List<CCSPlayerController> players, Dictionary<string, int> history) =>
        players.Select(p =>
        {
            var id    = p.AuthorizedSteamID!.SteamId64.ToString();
            var times = history.TryGetValue(id, out var t) ? t : 0;
            return 1.0 / (1.0 + times);
        }).ToList();

    /// <summary>Selects CheatersCount cheaters from the global pool (all players).</summary>
    private List<ulong> SelectGlobal()
    {
        var allPlayers = GetActivePlayers().Where(p => p.AuthorizedSteamID != null).ToList();
        if (allPlayers.Count == 0) return [];

        var history    = LoadCheatHistory();
        var picked     = WeightedRandomSelect(allPlayers, ComputeWeights(allPlayers, history), _cfg.Match.CheatersCount);
        var selectedIds = picked.Select(p => p.AuthorizedSteamID!.SteamId64).ToList();

        foreach (var id in selectedIds)
        {
            var key = id.ToString();
            history[key] = history.TryGetValue(key, out var t) ? t + 1 : 1;
        }
        SaveCheatHistory(history);
        return selectedIds;
    }

    /// <summary>Selects CheatersCount cheaters per team (CT and T separately). Falls back to global selection if no one is on a team.</summary>
    private List<ulong> SelectPerTeam()
    {
        var allPlayers = GetActivePlayers().Where(p => p.AuthorizedSteamID != null).ToList();
        if (allPlayers.Count == 0) return [];

        var history  = LoadCheatHistory();
        var selected = new List<ulong>();

        foreach (var teamNum in new[] { (byte)CsTeam.CounterTerrorist, (byte)CsTeam.Terrorist })
        {
            var teamPlayers = allPlayers.Where(p => p.TeamNum == teamNum).ToList();
            if (teamPlayers.Count == 0) continue;

            var count  = Math.Min(_cfg.Match.CheatersCount, teamPlayers.Count);
            var picked = WeightedRandomSelect(teamPlayers, ComputeWeights(teamPlayers, history), count);
            selected.AddRange(picked.Select(p => p.AuthorizedSteamID!.SteamId64));
        }

        // Fallback: no players on any team (e.g. solo admin test) → global selection
        if (selected.Count == 0)
        {
            var count  = Math.Min(_cfg.Match.CheatersCount, allPlayers.Count);
            var picked = WeightedRandomSelect(allPlayers, ComputeWeights(allPlayers, history), count);
            selected.AddRange(picked.Select(p => p.AuthorizedSteamID!.SteamId64));
        }

        foreach (var id in selected)
        {
            var key = id.ToString();
            history[key] = history.TryGetValue(key, out var t) ? t + 1 : 1;
        }
        SaveCheatHistory(history);
        return selected;
    }

    private static List<T> WeightedRandomSelect<T>(List<T> items, List<double> weights, int count)
    {
        var rng    = new Random();
        var result = new List<T>();
        var pool   = items.Zip(weights, (i, w) => (item: i, weight: w)).ToList();

        for (int n = 0; n < count && pool.Count > 0; n++)
        {
            double total = pool.Sum(x => x.weight);
            double roll  = rng.NextDouble() * total;
            double acc   = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                acc += pool[i].weight;
                if (acc >= roll) { result.Add(pool[i].item); pool.RemoveAt(i); break; }
            }
        }
        return result;
    }

    // ── Metodi pubblici per DevModule ─────────────────────────────────────────

    public MatchState GetCurrentState() => _state;

    public List<string> GetCurrentCheaterNames() =>
        GetActivePlayers()
            .Where(p => p.AuthorizedSteamID != null && _cheaterSteamIds.Contains(p.AuthorizedSteamID.SteamId64))
            .Select(p => p.PlayerName)
            .ToList();

    /// <summary>Skip player waiting and start wallhack. Only valid from WaitingForPlayers.</summary>
    public bool ForceSkipWaiting()
    {
        if (_state != MatchState.WaitingForPlayers) return false;
        _activeTimer?.Kill();
        StartWallhackPhase();
        return true;
    }

    /// <summary>End wallhack and start the match. Only valid from WallhackPhase.</summary>
    public bool ForceEndWallhack()
    {
        if (_state != MatchState.WallhackPhase) return false;
        _activeTimer?.Kill();
        EndWallhackPhase();
        return true;
    }

    /// <summary>End match and start report phase. Only valid from MatchRunning.</summary>
    public bool ForceMatchEnd()
    {
        if (_state != MatchState.MatchRunning) return false;
        _activeTimer?.Kill();
        SetEspForAllPlayers(false);
        StartReportPhase();
        return true;
    }

    /// <summary>End report phase and start a new cycle. Only valid from ReportPhase.</summary>
    public bool ForceEndReportPhase()
    {
        if (_state != MatchState.ReportPhase) return false;
        _activeTimer?.Kill();
        StartNewCycle();
        return true;
    }

    /// <summary>Assign ESP to a player by name (partial, case-insensitive). Works in any state.</summary>
    public bool ForceAssignCheater(string playerName)
    {
        var player = GetActivePlayers().FirstOrDefault(p =>
            p.PlayerName.Contains(playerName, StringComparison.OrdinalIgnoreCase));
        if (player?.AuthorizedSteamID == null) return false;

        var id = player.AuthorizedSteamID.SteamId64;
        if (!_cheaterSteamIds.Contains(id)) _cheaterSteamIds.Add(id);

        var cmd = _cfg.Esp.CmdEnablePlayer.Replace("{name}", player.PlayerName);
        Server.ExecuteCommand(cmd);
        player.PrintToChat($"{_cfg.Ui.ChatPrefix} [Dev] You have been assigned as CHEATER.");
        return true;
    }

    public void SetDeveloperEspForAll(bool enabled) => SetEspForAllPlayers(enabled);

    public bool SetDeveloperEspForPlayer(string playerName, bool enabled)
    {
        var player = GetActivePlayers().FirstOrDefault(p =>
            p.PlayerName.Contains(playerName, StringComparison.OrdinalIgnoreCase));
        if (player == null) return false;

        ExecuteEspCommandForPlayer(player, enabled);
        return true;
    }

    private void ApplyCurrentEspStateToPlayer(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot || player.Connected != PlayerConnectedState.Connected) return;

        if (_state == MatchState.WallhackPhase)
        {
            ExecuteEspCommandForPlayer(player, true);
            player.PrintToChat($"{_cfg.Ui.ChatPrefix} ⚠ WALLHACK active for {_cfg.Match.WallhackDuration / 60:F0} minutes. Watch for suspicious movement!");
            return;
        }

        if (_state == MatchState.MatchRunning &&
            player.AuthorizedSteamID != null &&
            _cheaterSteamIds.Contains(player.AuthorizedSteamID.SteamId64))
        {
            ExecuteEspCommandForPlayer(player, true);
            player.PrintToChat($"{_cfg.Ui.ChatPrefix} 🎯 You have been selected as the CHEATER. Use ESP wisely.");
        }
    }

    private void SetEspForAllPlayers(bool enabled)
    {
        foreach (var player in GetActivePlayers())
            ExecuteEspCommandForPlayer(player, enabled);
    }

    private void ExecuteEspCommandForPlayer(CCSPlayerController targetPlayer, bool enabled)
    {
        var cmd = enabled
            ? _cfg.Esp.CmdEnablePlayer.Replace("{name}", targetPlayer.PlayerName)
            : _cfg.Esp.CmdDisablePlayer.Replace("{name}", targetPlayer.PlayerName);

        if (TryExecuteEspCommandFromAdminClient(cmd)) return;

        Server.ExecuteCommand(cmd);
    }

    private bool TryExecuteEspCommandFromAdminClient(string command)
    {
        var adminPlayer = GetActivePlayers().FirstOrDefault(player =>
            player.AuthorizedSteamID != null &&
            _cfg.Dev.AdminSteamIds.Contains(player.AuthorizedSteamID.SteamId64.ToString()));
        if (adminPlayer == null) return false;

        adminPlayer.ExecuteClientCommandFromServer(command);
        return true;
    }

    // ── I/O ───────────────────────────────────────────────────────────────────

    private void WritePendingMatchFile()
    {
        var matchId = $"match_{_matchCounter:D3}";
        var players = GetActivePlayers()
            .Where(p => p.AuthorizedSteamID != null)
            .Select(p => new { steam_id = p.AuthorizedSteamID!.SteamId64.ToString(), nickname = p.PlayerName });

        var data = new
        {
            match_id  = matchId,
            cheaters  = _cheaterSteamIds.Select(id => id.ToString()),
            players,
            timestamp = DateTime.UtcNow.ToString("o")
        };

        Directory.CreateDirectory(_dataPath);
        File.WriteAllText(
            Path.Combine(_dataPath, "pending_match.json"),
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Dictionary<string, int> LoadCheatHistory()
    {
        var path = Path.Combine(_dataPath, "cheat_history.json");
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? []; }
        catch { return []; }
    }

    private void SaveCheatHistory(Dictionary<string, int> h)
    {
        Directory.CreateDirectory(_dataPath);
        File.WriteAllText(Path.Combine(_dataPath, "cheat_history.json"),
            JsonSerializer.Serialize(h, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LoadMatchCounter()
    {
        // Prefer match_counter.json (written by C# on every cycle), fall back to matches.json (written by scorer)
        var sources = new[]
        {
            Path.Combine(_dataPath, "match_counter.json"),
            Path.Combine(_dataPath, "matches.json"),
        };
        foreach (var path in sources)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var val = JsonNode.Parse(File.ReadAllText(path))?["next_id"]?.GetValue<int>();
                if (val.HasValue) { _matchCounter = val.Value; return; }
            }
            catch { /* try next source */ }
        }
        _matchCounter = 1;
    }

    private void SaveMatchCounter() =>
        File.WriteAllText(Path.Combine(_dataPath, "match_counter.json"),
            JsonSerializer.Serialize(new { next_id = _matchCounter }));

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static List<CCSPlayerController> GetActivePlayers() =>
        Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.Connected)
            .ToList();

    private static int GetPlayerCount() => GetActivePlayers().Count;

    private void Chat(string fmt, params object[] args) =>
        Server.PrintToChatAll($"{_cfg.Ui.ChatPrefix} {string.Format(fmt, args)}");

    public string GetStatusString() =>
        $"State: {_state} | Players: {GetPlayerCount()}/{_cfg.Match.RequiredPlayers} | Match: {_matchCounter}";
}
