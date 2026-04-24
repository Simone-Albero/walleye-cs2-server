using CounterStrikeSharp.API.Core;

namespace WallEyeServer;

public class RulesModule
{
    private readonly WallEyeServer _plugin;
    private readonly WallEyeConfig _cfg;

    // ── Rules text — edit here ──────────────────────────────────────────────────────────────────
    private const string RULES_HTML =
        "<font color='#FFD700' size='22'><b>WELCOME TO WALLEYE CS2</b></font><br>" +
        "<font color='#FFFFFF' size='14'>" +
        "1. No external cheats. The server automatically assigns the cheater role.<br>" +
        "2. Wallhack phase: watch for suspicious movement (ESP active for all players).<br>" +
        "3. At match end the report menu opens automatically — vote who you suspect.<br>" +
        "4. Correct report = +30 pts. Wrong report = -20 pts.<br>" +
        "5. [PLACEHOLDER — customise your server rules here]" +
        "</font>";
    // ─────────────────────────────────────────────────────────────────────────

    public RulesModule(WallEyeServer plugin, WallEyeConfig cfg)
    {
        _plugin = plugin;
        _cfg    = cfg;
    }

    public void Initialize()
    {
        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect);
    }

    private HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        _plugin.AddTimer(_cfg.Ui.RulesDelay, () =>
        {
            if (player.IsValid)
                player.PrintToCenterHtml(RULES_HTML, (int)_cfg.Ui.RulesDisplay);
        });

        return HookResult.Continue;
    }
}
