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
        string path = Path.Combine(Server.GameDirectory, "csgo", "cfg", "MatchZy", "raito.json");
        if (!File.Exists(path)) return new();

        var config = JsonSerializer.Deserialize<RaitoServerConfig>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new();

        config.ServerId = string.IsNullOrWhiteSpace(config.ServerId) ? "local-cs2-server" : config.ServerId.Trim();
        config.ServerName = string.IsNullOrWhiteSpace(config.ServerName) ? "BETHECHAMP 5v5" : config.ServerName.Trim();
        config.ServerIpAddress = string.IsNullOrWhiteSpace(config.ServerIpAddress) ? "127.0.0.1" : config.ServerIpAddress.Trim();
        config.ServerPort = Math.Clamp(config.ServerPort, 1, 65535);
        config.MaxPlayers = Math.Max(1, config.MaxPlayers);
        config.PublicSlots = Math.Max(1, config.PublicSlots);
        config.ReservedSlots = Math.Clamp(config.ReservedSlots, 0, 4);
        config.MaxPlayers = config.PublicSlots;
        config.MapVoteDurationSeconds = Math.Clamp(config.MapVoteDurationSeconds, 10, 60);
        config.HeartbeatIntervalSeconds = Math.Max(5, config.HeartbeatIntervalSeconds);
        return config;
    }
}
