using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallEyeServer;

// ── Config classes (mutable — support css_we_set runtime modifications) ──────────

public class WallEyeConfig
{
    [JsonPropertyName("match")]   public MatchConfig   Match   { get; set; } = new();
    [JsonPropertyName("scoring")] public ScoringConfig Scoring { get; set; } = new();
    [JsonPropertyName("server")]  public ServerConfig  Server  { get; set; } = new();
    [JsonPropertyName("esp")]     public EspConfig     Esp     { get; set; } = new();
    [JsonPropertyName("ui")]      public UiConfig      Ui      { get; set; } = new();
    [JsonPropertyName("dev")]     public DevConfig     Dev     { get; set; } = new();
}

public class MatchConfig
{
    [JsonPropertyName("required_players")]              public int    RequiredPlayers    { get; set; } = 10;
    [JsonPropertyName("wallhack_duration_seconds")]     public float  WallhackDuration   { get; set; } = 300;
    [JsonPropertyName("report_phase_duration_seconds")] public float  ReportDuration     { get; set; } = 60;
    [JsonPropertyName("restart_delay_seconds")]         public float  RestartDelay       { get; set; } = 5;
    [JsonPropertyName("leaderboard_display_seconds")]   public float  LeaderboardDisplay { get; set; } = 10;
    [JsonPropertyName("map")]                           public string Map                { get; set; } = "de_dust2";
    [JsonPropertyName("max_rounds")]                    public int    MaxRounds          { get; set; } = 30;
    [JsonPropertyName("cheaters_count")]              public int    CheatersCount      { get; set; } = 1;
    [JsonPropertyName("cheater_selection")]            public string CheaterSelection    { get; set; } = "per_team";
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

public class EspConfig
{
    [JsonPropertyName("cmd_enable_all")]     public string CmdEnableAll     { get; set; } = "css_esp_on";
    [JsonPropertyName("cmd_disable_all")]    public string CmdDisableAll    { get; set; } = "css_esp_off";
    [JsonPropertyName("cmd_enable_player")]  public string CmdEnablePlayer  { get; set; } = "css_esp \"{name}\" true";
    [JsonPropertyName("cmd_disable_player")] public string CmdDisablePlayer { get; set; } = "css_esp \"{name}\" false";
}

public class UiConfig
{
    [JsonPropertyName("rules_display_seconds")]               public float  RulesDisplay                   { get; set; } = 15;
    [JsonPropertyName("rules_delay_on_connect_seconds")]      public float  RulesDelay                     { get; set; } = 3;
    [JsonPropertyName("report_menu_open_delay_seconds")]      public float  ReportMenuDelay                { get; set; } = 3;
    [JsonPropertyName("wallhack_warning_before_end_seconds")] public float  WallhackWarningBeforeEndSeconds { get; set; } = 60;
    [JsonPropertyName("chat_prefix")]                         public string ChatPrefix                     { get; set; } = "[WallEye]";
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
    private LeaderboardModule _leaderboardModule = null!;
    private RulesModule       _rulesModule       = null!;
    private DevModule         _devModule         = null!;

    public override void Load(bool hotReload)
    {
        var cfg      = LoadConfig("/config/config.json");
        var dataPath = cfg.Server.DataPath;

        _reportModule      = new ReportModule(this, dataPath, cfg);
        _leaderboardModule = new LeaderboardModule(this, dataPath, cfg);
        _rulesModule       = new RulesModule(this, cfg);
        _matchManager      = new MatchManager(this, dataPath, cfg);
        _devModule         = new DevModule(this, cfg, _matchManager, _reportModule);

        _reportModule.Initialize();
        _leaderboardModule.Initialize();
        _rulesModule.Initialize();
        _matchManager.Initialize(_reportModule, _leaderboardModule);
        _devModule.Initialize();

        AddCommand("css_walleye_status", "WallEye status", (player, _) =>
            Server.PrintToChatAll($"{cfg.Ui.ChatPrefix} {_matchManager.GetStatusString()}"));
    }

    /// <summary>Loads config.json from the given path. Public so DevModule can call it for css_we_reload_config.</summary>
    public static WallEyeConfig LoadConfig(string path) =>
        JsonSerializer.Deserialize<WallEyeConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        ) ?? throw new Exception($"Invalid config.json at: {path}");
}
