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
    WarmupPhase,
    MatchRunning,
    ReportPhase
}

public class MatchManager
{
    private const int EngineRoundLimitBuffer = 1;

    private readonly WallEyeServer _plugin;
    private readonly string        _dataPath;
    private readonly WallEyeConfig _cfg;
    private readonly WallEyeLog    _log;

    private MatchState      _state            = MatchState.WaitingForPlayers;
    private List<ulong>     _cheaterSteamIds  = new();
    private int             _matchCounter     = 1;
    private int             _liveRoundsPlayed = 0;
    private bool            _matchEndQueued      = false;
    private int             _phaseVersion        = 0;
    private bool            _reportPhaseStarted  = false;
    private readonly List<Timer> _phaseTimers  = new();
    private ReportModule    _reportModule     = null!;
    private EspModule       _esp              = null!;

    public MatchManager(WallEyeServer plugin, string dataPath, WallEyeConfig cfg)
    {
        _plugin   = plugin;
        _dataPath = dataPath;
        _cfg      = cfg;
        _log      = new WallEyeLog(dataPath, nameof(MatchManager));
    }

    public void Initialize(ReportModule reportModule, EspModule esp)
    {
        _reportModule = reportModule;
        _esp = esp;

        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect);
        _plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        _plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        _plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        _plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        _plugin.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchWinPanel);

        LoadMatchCounter();
        _log.Info($"Initialized. next_match={_matchCounter}");
        Server.NextFrame(() => EnterWaitingState());
    }

    // ── State 1: Waiting for players ───────────────────────────────────────────

    private void EnterWaitingState(bool honorSkipPlayerCheck = true)
    {
        EnterState(MatchState.WaitingForPlayers);
        _liveRoundsPlayed = 0;
        _matchEndQueued = false;
        _cheaterSteamIds.Clear();
        _esp.DisableAll();
        StartPausedWarmup();

        if (honorSkipPlayerCheck && _cfg.Dev.SkipPlayerCheck)
        {
            Chat("Dev: skip_player_check active — starting warmup immediately.");
            StartWarmupPhase();
            return;
        }

        var count = GetPlayerCount();
        Chat("Waiting for players... ({0}/{1})", count, _cfg.Match.RequiredPlayers);

        // Players might already be connected (e.g. returning from leaderboard warmup)
        if (count >= _cfg.Match.RequiredPlayers)
            StartWarmupPhase();
    }

    private HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (_state == MatchState.WaitingForPlayers)
        {
            var count = GetPlayerCount();
            WriteStatusFile();
            Chat("Players connected: {0}/{1}", count, _cfg.Match.RequiredPlayers);
            _log.Info($"Player connected during waiting. players={count}/{_cfg.Match.RequiredPlayers}");

            if (count >= _cfg.Match.RequiredPlayers)
                StartWarmupPhase();

            return HookResult.Continue;
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (_state == MatchState.WaitingForPlayers)
        {
            WriteStatusFile();
            Chat("Players connected: {0}/{1}", GetPlayerCount(), _cfg.Match.RequiredPlayers);
            _log.Info($"Player disconnected during waiting. players={GetPlayerCount()}/{_cfg.Match.RequiredPlayers}");
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        if (_state == MatchState.ReportPhase)
            AddPhaseTimer(0.3f, () => FreezePlayer(player));

        return HookResult.Continue;
    }

    // ── State 2: Warmup phase ──────────────────────────────────────────────

    private void StartWarmupPhase()
    {
        if (!_cfg.Dev.SkipPlayerCheck && GetPlayerCount() < _cfg.Match.RequiredPlayers)
        {
            Chat("Not enough players to start warmup ({0}/{1}).", GetPlayerCount(), _cfg.Match.RequiredPlayers);
            _log.Warn($"Warmup start refused. players={GetPlayerCount()}/{_cfg.Match.RequiredPlayers}");
            EnterWaitingState();
            return;
        }

        EnterState(MatchState.WarmupPhase);
        _log.Info($"Warmup phase started. players={GetPlayerCount()}");
        _esp.DisableAll();

        Chat("Warmup started! Match goes live in {0:F0} min.", _cfg.Match.WarmupDuration / 60);
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand($"mp_warmuptime {(int)_cfg.Match.WarmupDuration}");
        Server.ExecuteCommand("mp_warmup_start");

        AddPhaseTimer(_cfg.Match.WarmupDuration, EndWarmupPhase);
    }

    private void EndWarmupPhase()
    {
        // Select cheaters at the end of warmup, right before the live match starts.
        _cheaterSteamIds = SelectCheaters();
        _log.Info($"Live match starting. cheaters={string.Join(",", _cheaterSteamIds)}");

        EnterState(MatchState.MatchRunning);
        _liveRoundsPlayed = 0;
        _matchEndQueued = false;

        _esp.DisableAll();
        // Keep the engine round limit above WallEye's limit so CS2 does not open its own
        // match result panel while WallEye needs report/leaderboard popups.
        Server.ExecuteCommand("mp_match_end_restart 0");
        Server.ExecuteCommand("mp_match_restart_delay 600");
        Server.ExecuteCommand($"mp_maxrounds {GetEngineMaxRounds()}");
        Server.ExecuteCommand("mp_winlimit 0");
        Server.ExecuteCommand("mp_match_can_clinch 0");

        // Notify cheaters with a popup before the live round starts.
        foreach (var p in GetActivePlayers())
        {
            if (p.AuthorizedSteamID != null && _cheaterSteamIds.Contains(p.AuthorizedSteamID.SteamId64))
                OpenCheaterPopup(p);
        }

        Server.ExecuteCommand("mp_warmup_end");
        Chat("Match LIVE! Good luck!");
        // ESP viewer slots are applied in OnRoundStart once round 1 is fully settled.
        // Safety net: if the native warmup timer fired EventRoundStart before EnterState(MatchRunning),
        // OnRoundStart would have skipped the ESP setup (state was still WarmupPhase).
        // This fallback phase timer guarantees viewer slots are always applied.
        AddPhaseTimer(2f, () =>
        {
            var cheaterSlots = GetActivePlayers()
                .Where(p => p.AuthorizedSteamID != null &&
                            _cheaterSteamIds.Contains(p.AuthorizedSteamID.SteamId64))
                .Select(p => p.Slot)
                .ToList();
            _esp.SetViewerSlots(cheaterSlots);
            if (_esp.PropCount == 0) _esp.RebuildForAll();
            _log.Info($"ESP viewer slots applied (EndWarmupPhase fallback). slots=[{string.Join(",", cheaterSlots)}]");
        });
    }

    // ── State 3: End of match ───────────────────────────────────────────────────

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_state != MatchState.MatchRunning) return HookResult.Continue;

        _liveRoundsPlayed++;
        WriteStatusFile();

        if (_liveRoundsPlayed < _cfg.Match.MaxRounds)
        {
            Chat("Round {0}/{1} completed.", _liveRoundsPlayed, _cfg.Match.MaxRounds);
            _log.Info($"Round completed. round={_liveRoundsPlayed}/{_cfg.Match.MaxRounds}");
            return HookResult.Continue;
        }

        if (_matchEndQueued) return HookResult.Continue;
        _matchEndQueued = true;
        Chat("Match rounds completed. Opening report phase...");
        _log.Info($"Max rounds reached. rounds={_liveRoundsPlayed}");
        _esp.DisableAll();
        AddPhaseTimer(0.5f, StartReportPhase);
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Re-apply cheater viewer slots at every live round start.
        // EspModule's own EventRoundStart handler already calls RebuildForAll;
        // here we only (re)set which slots can see the glow.
        if (_state == MatchState.MatchRunning && _cheaterSteamIds.Count > 0)
        {
            AddPhaseTimer(1f, () =>
            {
                var cheaterSlots = GetActivePlayers()
                    .Where(p => p.AuthorizedSteamID != null &&
                                _cheaterSteamIds.Contains(p.AuthorizedSteamID.SteamId64))
                    .Select(p => p.Slot);
                _esp.SetViewerSlots(cheaterSlots);
            });
            return HookResult.Continue;
        }

        if (_state != MatchState.ReportPhase || _reportPhaseStarted) return HookResult.Continue;
        _reportPhaseStarted = true;
        _log.Info("Report phase warmup round started — opening voting.");

        // Small delay so the warmup round is fully settled before freezing/opening menus.
        AddPhaseTimer(0.5f, () =>
        {
            FreezeAllActivePlayers();

            foreach (var p in GetActivePlayers())
                p.PrintToCenterHtml(
                    "<font color='#FFD700' size='22'><b>🗳 REPORT PHASE</b></font><br>" +
                    $"<font color='#FFFFFF' size='14'>Vote window: {_cfg.Match.ReportDuration:F0}s</font>", 8);

            Chat("🗳 Report menu opens in {0}s. You have {1}s to vote.",
                (int)_cfg.Ui.ReportMenuDelay, (int)_cfg.Match.ReportDuration);

            AddPhaseTimer(_cfg.Ui.ReportMenuDelay, () => _reportModule.OpenReportMenuForAll());

            AddPhaseTimer(_cfg.Ui.ReportMenuDelay + (_cfg.Match.ReportDuration / 2), () =>
                Chat("⏳ {0}s left to vote!", (int)(_cfg.Match.ReportDuration / 2)));

            AddPhaseTimer(_cfg.Ui.ReportMenuDelay + _cfg.Match.ReportDuration, CloseReportPhase);
        });

        return HookResult.Continue;
    }

    private HookResult OnMatchWinPanel(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (_state != MatchState.MatchRunning) return HookResult.Continue;
        if (_matchEndQueued) return HookResult.Continue;
        _matchEndQueued = true;
        _log.Info("Match win panel opened; starting report phase.");
        _esp.DisableAll();
        StartReportPhase();
        return HookResult.Continue;
    }

    // ── State 4: Report phase ─────────────────────────────────────────────────
    // Voting runs in the warmup of the next restart.
    // mp_restartgame closes the win panel; OnRoundStart fires when the warmup
    // round begins and opens the menus — no fragile fixed-delay timer.

    private void StartReportPhase()
    {
        EnterState(MatchState.ReportPhase);
        _esp.DisableAll();

        Chat("🏁 Match over! Voting opens shortly...");
        _log.Info("Report phase started.");

        // Set warmup cvars BEFORE restart so they apply on the new round.
        Server.ExecuteCommand("mp_warmuptime 999");
        Server.ExecuteCommand("mp_warmup_pausetimer 1");
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0");
        Server.ExecuteCommand("mp_restartgame 1");
        // OnRoundStart takes over from here.
    }

    // ── Cycle: close reports then next match ──────────────────────────────────
    // In-game leaderboard is intentionally disabled. The web platform shows
    // final standings after the scoring service processes reports and demos.

    private void CloseReportPhase()
    {
        var matchId = $"match_{_matchCounter:D3}";
        _reportModule.FlushReports(matchId);

        // Voting closed: give players control back before the next cycle starts.
        UnfreezeAllActivePlayers();

        Chat("✅ Reports closed. Next match starts in {0}s...",
            (int)_cfg.Match.RestartDelay);

        AddPhaseTimer(_cfg.Match.RestartDelay, () =>
        {
            // Stop the current demo before the restart creates a new one.
            Server.ExecuteCommand("tv_stoprecord");
            _log.Info("Stopped demo recording.");

            // Trigger backend scoring only when the next cycle begins, so demo
            // parsing never competes with the report phase.
            WritePendingMatchFile();

            _cheaterSteamIds.Clear();
            _matchCounter++;
            SaveMatchCounter();
            WriteStatusFile();
            _log.Info($"Cycle closed. match_id={matchId} next_match={_matchCounter}");

            EnterWaitingState();
        });
    }

    // ── Selezione cheater con peso anti-ripetizione ──────────────────────────

    private List<ulong> SelectCheaters()
    {
        if (GetConfiguredCheaterCount() == 0) return [];

        return _cfg.Match.CheaterSelection switch
        {
            "global" => SelectGlobal(),
            "per_team" => SelectPerTeam(),
            _ => SelectGlobal()
        };
    }

    /// <summary>Selects CheatersCount cheaters from the global pool (all players).</summary>
    private List<ulong> SelectGlobal()
    {
        var allPlayers = GetActivePlayers().Where(p => p.AuthorizedSteamID != null).ToList();
        if (allPlayers.Count == 0) return [];

        var history    = LoadCheatHistory();
        var count      = Math.Min(GetConfiguredCheaterCount(), allPlayers.Count);
        var picked     = FairRandomSelect(allPlayers, history, count);
        var selectedIds = picked.Select(p => p.AuthorizedSteamID!.SteamId64).ToList();

        foreach (var id in selectedIds)
        {
            var key = id.ToString();
            history[key] = history.TryGetValue(key, out var t) ? t + 1 : 1;
        }
        SaveCheatHistory(history);
        _log.Info($"Selected global cheaters count={selectedIds.Count}");
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

            var count  = Math.Min(GetConfiguredCheaterCount(), teamPlayers.Count);
            var picked = FairRandomSelect(teamPlayers, history, count);
            selected.AddRange(picked.Select(p => p.AuthorizedSteamID!.SteamId64));
        }

        // Fallback: no players on any team (e.g. solo admin test) → global selection
        if (selected.Count == 0)
        {
            var count  = Math.Min(GetConfiguredCheaterCount(), allPlayers.Count);
            var picked = FairRandomSelect(allPlayers, history, count);
            selected.AddRange(picked.Select(p => p.AuthorizedSteamID!.SteamId64));
        }

        foreach (var id in selected)
        {
            var key = id.ToString();
            history[key] = history.TryGetValue(key, out var t) ? t + 1 : 1;
        }
        SaveCheatHistory(history);
        _log.Info($"Selected per-team cheaters count={selected.Count}");
        return selected;
    }

    private int GetConfiguredCheaterCount() => Math.Max(0, _cfg.Match.CheatersCount);

    private static List<CCSPlayerController> FairRandomSelect(
        List<CCSPlayerController> players,
        Dictionary<string, int> history,
        int count) =>
        players
            .Select(player => new
            {
                Player = player,
                Times = history.TryGetValue(player.AuthorizedSteamID!.SteamId64.ToString(), out var times) ? times : 0,
                Roll = Random.Shared.NextDouble()
            })
            .OrderBy(item => item.Times)
            .ThenBy(item => item.Roll)
            .Take(count)
            .Select(item => item.Player)
            .ToList();

    // ── Metodi pubblici per DevModule ─────────────────────────────────────────

    public List<string> GetCurrentCheaterNames() =>
        GetActivePlayers()
            .Where(p => p.AuthorizedSteamID != null && _cheaterSteamIds.Contains(p.AuthorizedSteamID.SteamId64))
            .Select(p => p.PlayerName)
            .ToList();

    /// <summary>Force the dev-selected phase, regardless of the current phase.</summary>
    public bool ForcePhase(MatchState target)
    {
        switch (target)
        {
            case MatchState.WaitingForPlayers:
                _esp.DisableAll();
                _cheaterSteamIds.Clear();
                EnterWaitingState(honorSkipPlayerCheck: false);
                return true;

            case MatchState.WarmupPhase:
                StartWarmupPhase();
                return true;

            case MatchState.MatchRunning:
                if (_cheaterSteamIds.Count == 0) _cheaterSteamIds = SelectCheaters();
                EndWarmupPhase();
                return true;

            case MatchState.ReportPhase:
                _esp.DisableAll();
                StartReportPhase();
                return true;

            default:
                return false;
        }
    }

    /// <summary>Assign ESP to a player by name (partial, case-insensitive). Works in any state.</summary>
    public bool ForceAssignCheater(string playerName)
    {
        var player = PlayerLookup.FindActiveByPartialName(playerName);
        if (player?.AuthorizedSteamID == null) return false;

        var id = player.AuthorizedSteamID.SteamId64;
        if (!_cheaterSteamIds.Contains(id)) _cheaterSteamIds.Add(id);

        // Rebuild viewer slots so the newly added cheater sees ESP immediately.
        var cheaterSlots = GetActivePlayers()
            .Where(p => p.AuthorizedSteamID != null && _cheaterSteamIds.Contains(p.AuthorizedSteamID.SteamId64))
            .Select(p => p.Slot);
        _esp.SetViewerSlots(cheaterSlots);

        _log.Info($"Developer assigned cheater. steam_id={id} name={player.PlayerName}");
        player.PrintToChat($"{_cfg.Ui.ChatPrefix} [Dev] You have been assigned as CHEATER.");
        return true;
    }

    private void OpenCheaterPopup(CCSPlayerController player)
    {
        var menu = WallEyeMenu.CreateInfo(_plugin, "YOU ARE THE CHEATER", [
            "!9 to close"
        ]);
        menu.ExitButton = false;
        WallEyeMenu.Open(_plugin, player, menu);
    }

    // ── Dev-facing ESP accessors (used by DevModule) ──────────────────────────

    public void SetDeveloperEspForAll(bool enabled)
    {
        if (enabled)
        {
            _esp.SetViewerSlots(GetActivePlayers().Select(p => p.Slot));
            _esp.RebuildForAll();
        }
        else
        {
            _esp.DisableAll();
        }
    }

    public bool IsDeveloperEspEnabled(CCSPlayerController player) => _esp.IsViewer(player.Slot);

    public bool SetDeveloperEspForPlayer(string playerName, bool enabled)
    {
        var player = PlayerLookup.FindActiveByPartialName(playerName);
        if (player == null) return false;

        if (enabled)
        {
            // Add this slot to viewer slots without removing others.
            var slots = GetActivePlayers()
                .Where(p => _esp.IsViewer(p.Slot) || p.Slot == player.Slot)
                .Select(p => p.Slot);
            _esp.SetViewerSlots(slots);
        }
        else
        {
            var slots = GetActivePlayers()
                .Where(p => _esp.IsViewer(p.Slot) && p.Slot != player.Slot)
                .Select(p => p.Slot);
            _esp.SetViewerSlots(slots);
        }
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
        _log.Info($"Wrote pending_match.json. match_id={matchId}");
    }

    private Dictionary<string, int> LoadCheatHistory()
    {
        var path = Path.Combine(_dataPath, "cheat_history.json");
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e)
        {
            _log.Warn($"Could not read cheat_history.json: {e.Message}");
            return [];
        }
    }

    private void SaveCheatHistory(Dictionary<string, int> h)
    {
        Directory.CreateDirectory(_dataPath);
        File.WriteAllText(Path.Combine(_dataPath, "cheat_history.json"),
            JsonSerializer.Serialize(h, new JsonSerializerOptions { WriteIndented = true }));
        _log.Info("Saved cheat_history.json.");
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
                if (val.HasValue)
                {
                    _matchCounter = val.Value;
                    _log.Info($"Loaded match counter {_matchCounter} from {Path.GetFileName(path)}.");
                    return;
                }
            }
            catch (Exception e)
            {
                _log.Warn($"Could not read match counter from {Path.GetFileName(path)}: {e.Message}");
            }
        }
        _matchCounter = 1;
        _log.Warn("No valid match counter found; starting from match_001.");
    }

    private void SaveMatchCounter()
    {
        Directory.CreateDirectory(_dataPath);
        File.WriteAllText(Path.Combine(_dataPath, "match_counter.json"),
            JsonSerializer.Serialize(new { next_id = _matchCounter }));
        _log.Info($"Saved match_counter.json. next_id={_matchCounter}");
    }

    private void EnterState(MatchState state)
    {
        CancelPhaseTimers();
        _state = state;
        _phaseVersion++;
        _reportPhaseStarted = false;
        WriteStatusFile();
        _log.Info($"State changed to {state}.");
    }

    private Timer AddPhaseTimer(float seconds, Action callback)
    {
        var version = _phaseVersion;
        var timer = _plugin.AddTimer(seconds, () =>
        {
            if (version != _phaseVersion) return;
            callback();
        });
        _phaseTimers.Add(timer);
        return timer;
    }

    private void CancelPhaseTimers()
    {
        foreach (var timer in _phaseTimers)
            timer.Kill();
        _phaseTimers.Clear();
    }

    private void StartPausedWarmup()
    {
        Server.ExecuteCommand("mp_warmuptime 999");
        Server.ExecuteCommand("mp_warmup_pausetimer 1");
        Server.ExecuteCommand("mp_warmup_start");
    }

    private void WriteStatusFile()
    {
        try
        {
            Directory.CreateDirectory(_dataPath);
            File.WriteAllText(Path.Combine(_dataPath, "status.json"),
                JsonSerializer.Serialize(new
                {
                    phase = _state.ToString(),
                    players = GetPlayerCount(),
                    required_players = _cfg.Match.RequiredPlayers,
                    match = _matchCounter,
                    rounds_played = _liveRoundsPlayed,
                    max_rounds = _cfg.Match.MaxRounds,
                    cheaters = GetCurrentCheaterNames()
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            _log.Warn($"Could not write status.json: {e.Message}");
        }
    }

    // ── Player freeze (used during ReportPhase) ──────────────────────────────

    private void FreezeAllActivePlayers()
    {
        foreach (var p in GetActivePlayers())
            FreezePlayer(p);
        _log.Info("Players frozen for report phase.");
    }

    private void UnfreezeAllActivePlayers()
    {
        foreach (var p in GetActivePlayers())
            UnfreezePlayer(p);
        _log.Info("Players unfrozen after report phase.");
    }

    private static void FreezePlayer(CCSPlayerController player) =>
        SetPlayerMoveType(player, MoveType_t.MOVETYPE_NONE);

    private static void UnfreezePlayer(CCSPlayerController player) =>
        SetPlayerMoveType(player, MoveType_t.MOVETYPE_WALK);

    private static void SetPlayerMoveType(CCSPlayerController player, MoveType_t moveType)
    {
        if (!player.IsValid) return;
        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;
        pawn.MoveType = moveType;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static List<CCSPlayerController> GetActivePlayers() =>
        PlayerLookup.ActivePlayers();

    private static int GetPlayerCount() => PlayerLookup.ActivePlayerCount();

    public int GetEngineMaxRounds() => Math.Max(_cfg.Match.MaxRounds + EngineRoundLimitBuffer, 2);

    private void Chat(string fmt, params object[] args) =>
        Server.PrintToChatAll($"{_cfg.Ui.ChatPrefix} {string.Format(fmt, args)}");

    public string GetStatusString() =>
        $"State: {_state} | Players: {GetPlayerCount()}/{_cfg.Match.RequiredPlayers} | Match: {_matchCounter}";
}
