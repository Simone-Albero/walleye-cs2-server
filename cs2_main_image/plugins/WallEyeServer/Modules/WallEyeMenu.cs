using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Menu;

namespace WallEyeServer;

public static class WallEyeMenu
{
    public static CenterHtmlMenu Create(BasePlugin plugin, string title, string enabledColor = "lime")
    {
        return new CenterHtmlMenu(title, plugin)
        {
            ExitButton = true,
            PostSelectAction = PostSelectAction.Nothing,
            TitleColor = "yellow",
            EnabledColor = enabledColor,
            DisabledColor = "white",
            PrevPageColor = "yellow",
            NextPageColor = "yellow",
            CloseColor = "red"
        };
    }

    public static CenterHtmlMenu CreateInfo(BasePlugin plugin, string title, IEnumerable<string> lines)
    {
        var menu = Create(plugin, title, "white");
        foreach (var line in lines)
            AddInfo(menu, line);
        return menu;
    }

    public static void AddInfo(CenterHtmlMenu menu, string line)
    {
        menu.AddMenuOption(TrimLine(line), (_, _) => { }, true);
    }

    public static void Open(BasePlugin plugin, CCSPlayerController player, CenterHtmlMenu menu,
        float autoCloseSeconds = 0, bool replaceExisting = true)
    {
        if (!player.IsValid) return;

        if (MenuManager.GetActiveMenu(player) != null)
        {
            if (!replaceExisting) return;
            MenuManager.CloseActiveMenu(player);
        }

        MenuManager.OpenCenterHtmlMenu(plugin, player, menu);

        if (autoCloseSeconds <= 0) return;
        var openedMenu = MenuManager.GetActiveMenu(player);
        plugin.AddTimer(autoCloseSeconds, () =>
        {
            if (player.IsValid && ReferenceEquals(MenuManager.GetActiveMenu(player), openedMenu))
                MenuManager.CloseActiveMenu(player);
        });
    }

    public static void Close(CCSPlayerController player)
    {
        if (player.IsValid && MenuManager.GetActiveMenu(player) != null)
            MenuManager.CloseActiveMenu(player);
    }

    private static string TrimLine(string line)
    {
        line = (line ?? "").Trim();
        return line.Length <= 96 ? line : $"{line[..93]}...";
    }
}
