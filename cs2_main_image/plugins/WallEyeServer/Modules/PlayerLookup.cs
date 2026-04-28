using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace WallEyeServer;

public static class PlayerLookup
{
    public static List<CCSPlayerController> ActivePlayers() =>
        Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.Connected)
            .ToList();

    public static int ActivePlayerCount() => ActivePlayers().Count;

    public static CCSPlayerController? FindActiveByPartialName(string playerName) =>
        ActivePlayers().FirstOrDefault(p =>
            p.PlayerName.Contains(playerName, StringComparison.OrdinalIgnoreCase));

    public static bool IsConnectedHuman(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, Connected: PlayerConnectedState.Connected };
}
