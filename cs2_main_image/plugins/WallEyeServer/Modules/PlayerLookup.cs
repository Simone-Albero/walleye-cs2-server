using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace WallEyeServer;

public static class PlayerLookup
{
    public static List<CCSPlayerController> ActivePlayers() =>
        Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.Connected)
            .ToList();

    /// <summary>Like ActivePlayers but also includes bots (for cheater selection). Never includes HLTV/SourceTV.</summary>
    public static List<CCSPlayerController> ActivePlayersAndBots() =>
        Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsHLTV && p.Connected == PlayerConnectedState.Connected)
            .ToList();

    /// <summary>Stable fake SteamId64 for bot players who have no AuthorizedSteamID.</summary>
    public static ulong BotFakeId(int slot) => ulong.MaxValue - (uint)slot;

    /// <summary>Returns the canonical player ID: SteamId64 for real players, BotFakeId for bots.</summary>
    public static ulong PlayerId(CCSPlayerController p) =>
        p.IsBot ? BotFakeId(p.Slot) : p.AuthorizedSteamID!.SteamId64;

    public static int ActivePlayerCount() => ActivePlayers().Count;

    public static CCSPlayerController? FindActiveByPartialName(string playerName) =>
        ActivePlayers().FirstOrDefault(p =>
            p.PlayerName.Contains(playerName, StringComparison.OrdinalIgnoreCase));

    public static bool IsConnectedHuman(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, Connected: PlayerConnectedState.Connected };
}
