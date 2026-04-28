using CounterStrikeSharp.API.Core;

namespace WallEyeServer;

public class RulesModule
{
    private readonly WallEyeServer _plugin;
    private readonly WallEyeConfig _cfg;
    private readonly WallEyeLog    _log;

    // ── Rules text — edit here ──────────────────────────────────────────────────────────────────
    private static readonly string[] RulesLines =
    [
        "A match can have zero or more hidden cheaters.",
        "During warmup, cheats are disabled for everyone.",
        "When the match ends, vote for who you think the cheater is.",
        "Correct report = +points  |  Wrong report = -points",
        "Sometimes the correct report is No cheater.",
    ];
    // ─────────────────────────────────────────────────────────────────────────

    public RulesModule(WallEyeServer plugin, WallEyeConfig cfg)
    {
        _plugin = plugin;
        _cfg    = cfg;
        _log    = new WallEyeLog(cfg.Server.DataPath, nameof(RulesModule));
    }

    public void Initialize()
    {
        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect);
        _log.Info("Initialized.");
    }

    private HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        ShowRules(player);

        return HookResult.Continue;
    }

    private void ShowRules(CCSPlayerController player)
    {
        player.PrintToChat($"{_cfg.Ui.ChatPrefix} — WALLEYE RULES —");
        _log.Info($"Showing rules to {player.PlayerName}.");
        foreach (var line in RulesLines)
            player.PrintToChat($"{_cfg.Ui.ChatPrefix} {line}");
    }
}
