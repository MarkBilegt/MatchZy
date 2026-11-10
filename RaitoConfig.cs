using CounterStrikeSharp.API;
using System.Text.Json;

namespace MatchZy;

public sealed class RaitoServerConfig
{
    public string ServerId { get; set; } = "local-cs2-server";
    public string ServerName { get; set; } = "BETHECHAMP 5v5";
    public string ServerIpAddress { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 27015;
    public string Region { get; set; } = "Local";
    public string ServerPurpose { get; set; } = "PUBLIC";
    public string DatabaseConfigPath { get; set; } = "csgo/cfg/MatchZy/database.json";
    public int MaxPlayers { get; set; } = 11;
    public int PublicSlots { get; set; } = 11;
    public int ReservedSlots { get; set; } = 1;
    public bool AdminCallsEnabled { get; set; } = true;
    public bool VoteKickImmunityEnabled { get; set; } = true;
    public bool ReservedSlotEnabled { get; set; } = true;
    public bool MapVoteEnabled { get; set; } = true;
    public bool NativeEndMatchMapVoteEnabled { get; set; } = true;
    public int MapVoteDurationSeconds { get; set; } = 20;
    public int HeartbeatIntervalSeconds { get; set; } = 10;

    public static RaitoServerConfig Load()
    {
        string configuredPath = Environment.GetEnvironmentVariable("RAITO_CONFIG_PATH")
            ?? "csgo/cfg/MatchZy/raito.json";
        string path = ResolvePath(configuredPath);
        var config = File.Exists(path)
            ? JsonSerializer.Deserialize<RaitoServerConfig>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
            : new();

        config.ApplyEnvironment();

        config.ServerId = string.IsNullOrWhiteSpace(config.ServerId) ? "local-cs2-server" : config.ServerId.Trim();
        config.ServerName = string.IsNullOrWhiteSpace(config.ServerName) ? "BETHECHAMP 5v5" : config.ServerName.Trim();
        config.ServerIpAddress = string.IsNullOrWhiteSpace(config.ServerIpAddress) ? "127.0.0.1" : config.ServerIpAddress.Trim();
        config.Region = string.IsNullOrWhiteSpace(config.Region) ? "Unknown" : config.Region.Trim();
        config.ServerPurpose = string.Equals(config.ServerPurpose, "CLAN_WAR", StringComparison.OrdinalIgnoreCase)
            ? "CLAN_WAR"
            : "PUBLIC";
        config.DatabaseConfigPath = string.IsNullOrWhiteSpace(config.DatabaseConfigPath)
            ? "csgo/cfg/MatchZy/database.json"
            : config.DatabaseConfigPath.Trim();
        config.ServerPort = Math.Clamp(config.ServerPort, 1, 65535);
        config.MaxPlayers = Math.Max(1, config.MaxPlayers);
        config.PublicSlots = Math.Max(1, config.PublicSlots);
        config.ReservedSlots = Math.Clamp(config.ReservedSlots, 0, 4);
        config.MaxPlayers = config.PublicSlots;
        config.MapVoteDurationSeconds = Math.Clamp(config.MapVoteDurationSeconds, 10, 60);
        config.HeartbeatIntervalSeconds = Math.Max(5, config.HeartbeatIntervalSeconds);
        return config;
    }

    public string ResolveDatabaseConfigPath() => ResolvePath(DatabaseConfigPath);

    private static string ResolvePath(string value)
    {
        string normalized = value.Trim().Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(Server.GameDirectory, normalized);
    }

    private void ApplyEnvironment()
    {
        ServerId = EnvironmentValue("RAITO_SERVER_ID", ServerId);
        ServerName = EnvironmentValue("RAITO_SERVER_NAME", ServerName);
        ServerIpAddress = EnvironmentValue("RAITO_SERVER_IP", ServerIpAddress);
        Region = EnvironmentValue("RAITO_SERVER_REGION", Region);
        ServerPurpose = EnvironmentValue("RAITO_SERVER_PURPOSE", ServerPurpose);
        DatabaseConfigPath = EnvironmentValue("RAITO_DATABASE_CONFIG_PATH", DatabaseConfigPath);
        ServerPort = EnvironmentInt("RAITO_SERVER_PORT", ServerPort);
        MaxPlayers = EnvironmentInt("RAITO_MAX_PLAYERS", MaxPlayers);
        PublicSlots = EnvironmentInt("RAITO_PUBLIC_SLOTS", PublicSlots);
        ReservedSlots = EnvironmentInt("RAITO_RESERVED_SLOTS", ReservedSlots);
        HeartbeatIntervalSeconds = EnvironmentInt("RAITO_HEARTBEAT_SECONDS", HeartbeatIntervalSeconds);
        AdminCallsEnabled = EnvironmentBool("RAITO_ADMIN_CALLS_ENABLED", AdminCallsEnabled);
        MapVoteEnabled = EnvironmentBool("RAITO_MAP_VOTE_ENABLED", MapVoteEnabled);
        NativeEndMatchMapVoteEnabled = EnvironmentBool(
            "RAITO_NATIVE_MAP_VOTE_ENABLED",
            NativeEndMatchMapVoteEnabled);
        ReservedSlotEnabled = EnvironmentBool("RAITO_RESERVED_SLOT_ENABLED", ReservedSlotEnabled);
    }

    private static string EnvironmentValue(string name, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int EnvironmentInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int parsed) ? parsed : fallback;
    }

    private static bool EnvironmentBool(string name, bool fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return bool.TryParse(value, out bool parsed) ? parsed : fallback;
    }
}
