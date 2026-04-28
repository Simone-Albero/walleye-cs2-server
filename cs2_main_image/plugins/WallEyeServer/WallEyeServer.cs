using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallEyeServer;

// ── Config classes (mutable — support css_set runtime modifications) ──────────

public class WallEyeConfig
{
    [JsonPropertyName("match")]   public MatchConfig   Match   { get; set; } = new();
    [JsonPropertyName("scoring")] public ScoringConfig Scoring { get; set; } = new();
    [JsonPropertyName("server")]  public ServerConfig  Server  { get; set; } = new();
    [JsonPropertyName("ui")]      public UiConfig      Ui      { get; set; } = new();
    [JsonPropertyName("dev")]     public DevConfig     Dev     { get; set; } = new();
}

public class MatchConfig
{
    [JsonPropertyName("required_players")]              public int    RequiredPlayers    { get; set; } = 10;
    [JsonPropertyName("warmup_duration_seconds")]       public float  WarmupDuration     { get; set; } = 300;
    [JsonPropertyName("report_phase_duration_seconds")] public float  ReportDuration     { get; set; } = 60;
    [JsonPropertyName("restart_delay_seconds")]         public float  RestartDelay       { get; set; } = 5;
    [JsonPropertyName("leaderboard_display_seconds")]   public float  LeaderboardDisplay { get; set; } = 10;
    [JsonPropertyName("map")]                           public string Map                { get; set; } = "de_dust2";
    [JsonPropertyName("max_rounds")]                    public int    MaxRounds          { get; set; } = 30;
    [JsonPropertyName("cheaters_count")]                public int    CheatersCount      { get; set; } = 1;
    [JsonPropertyName("cheater_selection")]             public string CheaterSelection    { get; set; } = "global";
    [JsonPropertyName("report_scope")]                  public string ReportScope        { get; set; } = "all";
}

public class ScoringConfig
{
    [JsonPropertyName("points_participation")]      public int PtsParticipation { get; set; } = 10;
    [JsonPropertyName("points_correct_report")]     public int PtsCorrect       { get; set; } = 30;
    [JsonPropertyName("points_wrong_report")]       public int PtsWrong         { get; set; } = -20;
    [JsonPropertyName("points_no_cheater_correct")] public int PtsNoCheater     { get; set; } = 30;
    [JsonPropertyName("points_kill")]               public int PtsKill          { get; set; } = 2;
    [JsonPropertyName("points_assist")]             public int PtsAssist        { get; set; } = 1;
    [JsonPropertyName("points_death")]              public int PtsDeath         { get; set; } = -1;
}

public class ServerConfig
{
    [JsonPropertyName("data_path")] public string DataPath { get; set; } = "/data";
}

public class UiConfig
{
    [JsonPropertyName("rules_display_seconds")]               public float  RulesDisplay                   { get; set; } = 15;
    [JsonPropertyName("rules_delay_on_connect_seconds")]      public float  RulesDelay                     { get; set; } = 3;
    [JsonPropertyName("report_menu_open_delay_seconds")]      public float  ReportMenuDelay                { get; set; } = 3;
    [JsonPropertyName("chat_prefix")] public string ChatPrefix { get; set; } = "[WallEye]";
}

public class DevConfig
{
    [JsonPropertyName("enabled")]           public bool         Enabled         { get; set; } = false;
    [JsonPropertyName("admin_steam_ids")]   public List<string> AdminSteamIds   { get; set; } = new();
    [JsonPropertyName("skip_player_check")] public bool         SkipPlayerCheck { get; set; } = false;
}

// ── Plugin entry point ──────────────────────────────────────────────────────────

public class WallEyeServer : BasePlugin
{
    public override string ModuleName    => "WallEyeServer";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor  => "kgarri";

    private MatchManager      _matchManager      = null!;
    private ReportModule      _reportModule      = null!;
    private RulesModule       _rulesModule       = null!;
    private DevModule         _devModule         = null!;
    private EspModule         _espModule         = null!;
    private WallEyeLog        _log               = null!;

    public override void Load(bool hotReload)
    {
        var cfg      = LoadConfig("/config/config.json");
        var dataPath = cfg.Server.DataPath;
        _log = new WallEyeLog(dataPath, "Plugin");
        _log.Info($"Loading WallEyeServer {ModuleVersion} (hotReload={hotReload})");

        _reportModule      = new ReportModule(this, dataPath, cfg);
        _rulesModule       = new RulesModule(this, cfg);
        _espModule         = new EspModule(this, dataPath);
        _matchManager      = new MatchManager(this, dataPath, cfg);
        _devModule         = new DevModule(this, cfg, _matchManager, _reportModule);

        _reportModule.Initialize();
        _rulesModule.Initialize();
        _espModule.Initialize();
        _matchManager.Initialize(_reportModule, _espModule);
        _devModule.Initialize();

        AddCommand("css_state", "WallEye state", (player, _) =>
            Server.PrintToChatAll($"{cfg.Ui.ChatPrefix} {_matchManager.GetStatusString()}"));

        _log.Info("WallEyeServer loaded.");
    }

    /// <summary>Loads config.json from the given path. Public so DevModule can call it for css_reload.</summary>
    public static WallEyeConfig LoadConfig(string path)
    {
        var cfg = JsonSerializer.Deserialize<WallEyeConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        ) ?? throw new Exception($"Invalid config.json at: {path}");

        NormalizeConfig(cfg);
        return cfg;
    }

    private static void NormalizeConfig(WallEyeConfig cfg)
    {
        cfg.Match.RequiredPlayers = Math.Max(1, cfg.Match.RequiredPlayers);
        cfg.Match.WarmupDuration = Math.Max(1, cfg.Match.WarmupDuration);
        cfg.Match.ReportDuration = Math.Max(1, cfg.Match.ReportDuration);
        cfg.Match.RestartDelay = Math.Max(0, cfg.Match.RestartDelay);
        cfg.Match.LeaderboardDisplay = Math.Max(0, cfg.Match.LeaderboardDisplay);
        cfg.Match.MaxRounds = Math.Max(1, cfg.Match.MaxRounds);
        cfg.Match.CheatersCount = Math.Max(0, cfg.Match.CheatersCount);
        cfg.Match.Map = string.IsNullOrWhiteSpace(cfg.Match.Map) ? "de_dust2" : cfg.Match.Map;

        if (cfg.Match.CheaterSelection is not ("global" or "per_team"))
            cfg.Match.CheaterSelection = "global";

        if (cfg.Match.ReportScope is not ("all" or "enemy_team"))
            cfg.Match.ReportScope = "all";

        cfg.Ui.RulesDisplay = Math.Max(1, cfg.Ui.RulesDisplay);
        cfg.Ui.RulesDelay = Math.Max(0, cfg.Ui.RulesDelay);
        cfg.Ui.ReportMenuDelay = Math.Max(0, cfg.Ui.ReportMenuDelay);
        cfg.Ui.ChatPrefix = string.IsNullOrWhiteSpace(cfg.Ui.ChatPrefix) ? "[WallEye]" : cfg.Ui.ChatPrefix;
    }
}
