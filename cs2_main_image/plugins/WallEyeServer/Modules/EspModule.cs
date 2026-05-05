using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace WallEyeServer;

/// <summary>
/// Native ESP implementation using prop_dynamic glow entities + CheckTransmit filtering.
/// Mirrors the AdminESP (aquevadis/kgarri) approach that was proven to work.
///
/// Design:
///   - For every alive player a pair of prop_dynamic entities is created (relay + glow).
///     Both are teleported to the player's current world position immediately on creation
///     so they enter the network PVS at once; FollowEntity then takes over tracking.
///   - CheckTransmit hides the glow props from every viewer whose slot is NOT in _viewerSlots.
///     Only slots in _viewerSlots (= the cheater) can see the glow through walls.
///   - Glow props are rebuilt on EventRoundStart via Server.NextFrame (matches AdminESP).
///     On EventPlayerSpawn (HookMode.Pre) we attempt immediate creation, with a 0.5 s
///     fallback timer for the case where the pawn model is not yet assigned in Pre.
///     Props are destroyed on EventPlayerDeath.
/// </summary>
public class EspModule
{
    private readonly WallEyeServer _plugin;
    private readonly WallEyeLog    _log;

    // slot → (relay prop, glow prop)
    private readonly Dictionary<int, (CBaseModelEntity Relay, CBaseModelEntity Glow)> _glowProps = new();

    // slots that are allowed to see the glow props (= cheater slots)
    private readonly HashSet<int> _viewerSlots = new();

    // when non-empty, glow props are only built for players in this set (= cheater slots during reveal)
    private readonly HashSet<int> _targetSlots = new();

    // when non-null, overrides the per-team glow colour (used for the reveal phase)
    private Color? _overrideGlowColor = null;

    // when true, glow colour is updated every tick based on target player's health
    private bool _healthBasedColor = false;

    // cached list for CheckTransmit — rebuilt whenever _glowProps changes
    private readonly List<(CBaseModelEntity Relay, CBaseModelEntity Glow)> _propList = new();

    public EspModule(WallEyeServer plugin, string dataPath)
    {
        _plugin = plugin;
        _log    = new WallEyeLog(dataPath, nameof(EspModule));
    }

    public void Initialize()
    {
        // HookMode.Pre: attempt prop creation as early as possible, same as AdminESP.
        _plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Pre);
        _plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        _plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        _plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        _plugin.RegisterListener<Listeners.OnTick>(UpdateHealthColors);
        _log.Info("Initialized.");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Set which player slots can see the glow (the cheaters).</summary>
    public void SetViewerSlots(IEnumerable<int> slots)
    {
        _viewerSlots.Clear();
        foreach (var s in slots) _viewerSlots.Add(s);
        _log.Info($"Viewer slots set: [{string.Join(",", _viewerSlots)}]");
    }

    /// <summary>
    /// Restrict glow prop creation to the given slots only.
    /// Pass an empty enumerable (or call with no targets) to build props for everyone (default).
    /// </summary>
    public void SetTargetSlots(IEnumerable<int> slots)
    {
        _targetSlots.Clear();
        foreach (var s in slots) _targetSlots.Add(s);
        _log.Info($"Target slots set: [{string.Join(",", _targetSlots)}]");
    }

    /// <summary>Override the glow colour for all props. Pass null to restore per-team colours.</summary>
    public void SetGlowColorOverride(Color? color)
    {
        _overrideGlowColor = color;
        _log.Info($"Glow colour override: {(color.HasValue ? color.Value.ToString() : "none")}");
    }

    /// <summary>
    /// When enabled, the glow colour of each prop is updated every tick to reflect
    /// the target player's current health: green (100 HP) → yellow (50 HP) → red (0 HP).
    /// Has no effect if _overrideGlowColor is set.
    /// </summary>
    public void SetHealthBasedColor(bool enabled)
    {
        _healthBasedColor = enabled;
        _log.Info($"Health-based glow colour: {enabled}");
    }

    /// <summary>Stop all ESP: clear viewer/target slots, colour override, and destroy all glow props.</summary>
    public void DisableAll()
    {
        _viewerSlots.Clear();
        _targetSlots.Clear();
        _overrideGlowColor = null;
        _healthBasedColor  = false;
        DestroyAllProps();
        _log.Info("ESP disabled for all.");
    }

    /// <summary>Rebuild glow props for all alive players including bots (e.g. after round start).</summary>
    public void RebuildForAll()
    {
        DestroyAllProps();
        foreach (var p in GetAllPlayersForGlow())
            BuildPropsForPlayer(p);
        _log.Info($"Rebuilt glow props for {_glowProps.Count} players.");
    }

    /// <summary>Returns true if the given slot is currently a viewer (can see ESP).</summary>
    public bool IsViewer(int slot) => _viewerSlots.Contains(slot);

    /// <summary>Returns the number of active glow prop pairs currently tracked.</summary>
    public int PropCount => _glowProps.Count;

    // ── Tick: health-based colour update ─────────────────────────────────────

    private void UpdateHealthColors()
    {
        // Skip if feature is off, overridden by a fixed colour, or no props exist.
        if (!_healthBasedColor || _overrideGlowColor.HasValue || _glowProps.Count == 0) return;

        foreach (var (slot, (_, glow)) in _glowProps)
        {
            if (!glow.IsValid) continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || !player.PawnIsAlive) continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) continue;

            var newColor = HealthToColor(pawn.Health);
            glow.Glow.GlowColorOverride = newColor;
            // Always mark the network property dirty so the client receives
            // the update every tick — ensures smooth, immediate colour transitions.
            Utilities.SetStateChanged(glow, "CBaseModelEntity", "m_Glow");
        }
    }

    /// <summary>
    /// Maps HP to a colour along the green → yellow → red gradient.
    ///   100 HP → green  (0, 255, 0)
    ///    50 HP → yellow (255, 255, 0)
    ///     0 HP → red    (255, 0, 0)
    /// </summary>
    private static Color HealthToColor(int hp)
    {
        hp = Math.Clamp(hp, 0, 100);

        if (hp >= 50)
        {
            // Green → Yellow: increase red channel from 0 to 255
            float t = (100 - hp) / 50f;   // 0 at 100 HP, 1 at 50 HP
            int r = (int)(t * 255);
            return Color.FromArgb(r, 255, 0);
        }
        else
        {
            // Yellow → Red: decrease green channel from 255 to 0
            float t = hp / 50f;            // 1 at 50 HP, 0 at 0 HP
            int g = (int)(t * 255);
            return Color.FromArgb(255, g, 0);
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Server.NextFrame: matches AdminESP — rebuild on the very next frame so we
        // don't race against pawn initialisation with a fixed-duration timer.
        Server.NextFrame(RebuildForAll);
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;

        // Immediate attempt in Pre hook (matches AdminESP). May fail if the pawn model
        // is not yet assigned; the fallback timer below covers that case.
        BuildPropsForPlayer(player);

        // Fallback: only runs if the immediate attempt did not create the props.
        _plugin.AddTimer(0.5f, () =>
        {
            if (player.IsValid && player.PawnIsAlive && !_glowProps.ContainsKey(player.Slot))
                BuildPropsForPlayer(player);
        });
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;
        DestroyPropsForSlot(player.Slot);
        return HookResult.Continue;
    }

    // ── CheckTransmit: hide glow props from non-viewers ───────────────────────

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_propList.Count == 0) return;

        foreach ((CCheckTransmitInfo info, CCSPlayerController? viewer) in infoList)
        {
            if (viewer == null || !viewer.IsValid) continue;

            // Viewers (cheaters) see everything — skip.
            if (_viewerSlots.Contains(viewer.Slot)) continue;

            // For every other player: hide glow props.
            foreach (var (relay, glow) in _propList)
            {
                if (relay.IsValid) info.TransmitEntities.Remove((int)relay.Index);
                if (glow.IsValid)  info.TransmitEntities.Remove((int)glow.Index);
            }
        }
    }

    // ── Glow prop management ──────────────────────────────────────────────────

    private void BuildPropsForPlayer(CCSPlayerController player)
    {
        if (player == null || !player.IsValid) return;

        // If target slots are restricted, skip players not in the set.
        if (_targetSlots.Count > 0 && !_targetSlots.Contains(player.Slot)) return;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
        {
            _log.Info($"BuildProps: skip slot={player.Slot} name={player.PlayerName} — pawn null/invalid.");
            return;
        }

        var bodyComponent = pawn.CBodyComponent?.SceneNode;
        if (bodyComponent == null)
        {
            _log.Info($"BuildProps: skip slot={player.Slot} name={player.PlayerName} — CBodyComponent null.");
            return;
        }

        var modelName = bodyComponent.GetSkeletonInstance().ModelState.ModelName;
        if (string.IsNullOrEmpty(modelName))
        {
            _log.Info($"BuildProps: skip slot={player.Slot} name={player.PlayerName} — model name empty.");
            return;
        }

        // Destroy any existing props for this slot first.
        DestroyPropsForSlot(player.Slot);

        var relay = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
        var glow  = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
        if (relay == null || glow == null || !relay.IsValid || !glow.IsValid)
        {
            _log.Warn($"BuildProps: CreateEntityByName failed for slot={player.Slot} name={player.PlayerName}.");
            relay?.AcceptInput("Kill");
            glow?.AcceptInput("Kill");
            return;
        }

        // Relay: invisible, just anchors the follow chain.
        relay.SetModel(modelName);
        relay.Spawnflags = 256u;
        relay.RenderMode = RenderMode_t.kRenderNone;
        relay.DispatchSpawn();

        // Glow: visible only through walls.
        glow.SetModel(modelName);
        glow.Spawnflags = 256u;
        glow.DispatchSpawn();

        // Prefer: fixed override → health-based (read current HP immediately) → team colour.
        Color initialColor;
        if (_overrideGlowColor.HasValue)
        {
            initialColor = _overrideGlowColor.Value;
        }
        else if (_healthBasedColor && player.PawnIsAlive && player.PlayerPawn?.Value is { IsValid: true } activePawn)
        {
            initialColor = HealthToColor(activePawn.Health);
        }
        else
        {
            initialColor = player.TeamNum == (byte)CsTeam.Terrorist ? Color.Orange : Color.SkyBlue;
        }
        glow.Glow.GlowColorOverride = initialColor;
        glow.Glow.GlowRange    = 5000;
        glow.Glow.GlowTeam     = -1;
        glow.Glow.GlowType     = 3;
        glow.Glow.GlowRangeMin = 100;

        // Teleport both props to the pawn's current world position so they enter
        // the network PVS immediately. Without this, props spawn at origin (0,0,0)
        // and may fall outside the transmit range of players far from origin —
        // FollowEntity would then move them to the player too late for the current tick.
        if (pawn.AbsOrigin != null)
        {
            relay.Teleport(pawn.AbsOrigin, pawn.AbsRotation, null);
            glow.Teleport(pawn.AbsOrigin, pawn.AbsRotation, null);
        }

        relay.AcceptInput("FollowEntity", pawn,  relay, "!activator");
        glow.AcceptInput("FollowEntity",  relay, glow,  "!activator");

        _glowProps[player.Slot] = (relay, glow);
        RefreshPropList();
        _log.Info($"BuildProps: OK slot={player.Slot} name={player.PlayerName} " +
                  $"relay={relay.Index} glow={glow.Index} pos={pawn.AbsOrigin}");
    }

    private void DestroyPropsForSlot(int slot)
    {
        if (!_glowProps.TryGetValue(slot, out var pair)) return;

        if (pair.Relay.IsValid) pair.Relay.AcceptInput("Kill");
        if (pair.Glow.IsValid)  pair.Glow.AcceptInput("Kill");

        _glowProps.Remove(slot);
        RefreshPropList();
    }

    private void DestroyAllProps()
    {
        foreach (var (relay, glow) in _glowProps.Values)
        {
            if (relay.IsValid) relay.AcceptInput("Kill");
            if (glow.IsValid)  glow.AcceptInput("Kill");
        }
        _glowProps.Clear();
        _propList.Clear();
    }

    private void RefreshPropList()
    {
        _propList.Clear();
        _propList.AddRange(_glowProps.Values);
    }

    // For glow props we include bots as targets (so the cheater can see them through walls).
    // Bots are never added to _viewerSlots, so they can never see other glows.
    private static List<CCSPlayerController> GetAllPlayersForGlow() =>
        Utilities.GetPlayers()
            .Where(p => p.IsValid && p.Connected == PlayerConnectedState.Connected)
            .ToList();
}
