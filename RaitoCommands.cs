using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using System.Text.RegularExpressions;

namespace MatchZy;

public sealed record RaitoTarget(CCSPlayerController? Player, ulong SteamId64, string PlayerName);

public partial class MatchZy
{
    internal bool TryHandleRaitoChatCommand(CCSPlayerController? player, string rawMessage)
    {
        string[] tokens = Regex.Matches(rawMessage.Trim(), "\\\"([^\\\"]*)\\\"|(\\S+)")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .ToArray();
        // The CounterStrikeSharp registration supplies the ! form; chat parsing only handles the dot alias
        // so a command is never executed twice when both hooks observe the same message.
        if (tokens.Length == 0 || tokens[0][0] != '.') return false;

        string command = tokens[0][1..].ToLowerInvariant();
        string[] arguments = tokens.Skip(1).ToArray();
        switch (command)
        {
            case "testmatch": ExecuteRaitoTestMatchCommand(player, arguments); return true;
            case "ban": ExecuteRaitoBanCommand(player, arguments); return true;
            case "unban": ExecuteRaitoUnbanCommand(player, arguments); return true;
            case "mute": ExecuteRaitoModerationCommand(player, arguments, "css_mute", "Mute", "mutedBy", "player.mute", "You are muted and cannot use chat."); return true;
            case "unmute": ExecuteRaitoRemoveModerationCommand(player, arguments, "css_unmute", "Mute", "player.unmute"); return true;
            case "gag": ExecuteRaitoModerationCommand(player, arguments, "css_gag", "Gag", "gaggedBy", "player.gag", "You are voice-gagged.", true); return true;
            case "ungag": ExecuteRaitoRemoveModerationCommand(player, arguments, "css_ungag", "Gag", "player.ungag", true); return true;
            case "kick": ExecuteRaitoKickCommand(player, arguments); return true;
            case "team": ExecuteRaitoTeamCommand(player, arguments); return true;
            default: return false;
        }
    }

    [ConsoleCommand("css_ban", "Bans a player")]
    public void OnRaitoBanCommand(CCSPlayerController? player, CommandInfo command)
        => ExecuteRaitoBanCommand(player, GetCommandArguments(command));

    private void ExecuteRaitoBanCommand(CCSPlayerController? player, IReadOnlyList<string> arguments)
    {
        if (!RaitoCanExecute(player, "css_ban", "@css/ban"))
        {
            SendPlayerNotAdminMessage(player);
            return;
        }

        if (raitoDatabase is null)
        {
            ReplyToUserCommand(player, "Discipline service offline. Ban not applied.");
            return;
        }

        if (arguments.Count < 1)
        {
            ReplyToUserCommand(player, $"Command format: {ChatColors.Green}.ban <player> [30m|2h|7d|perm] [reason]{ChatColors.Default}");
            return;
        }

        string targetArg = arguments[0];
        RaitoTarget? target = ResolveTarget(targetArg, allowOfflineSteamId: true);
        if (target is null)
        {
            ReplyToUserCommand(player, "Player lookup failed. Try a slot, name, or SteamID64.");
            return;
        }

        ParseDurationAndReason(arguments, 1, out TimeSpan? duration, out string reason);
        try
        {
            raitoDatabase.UpsertModeration("Ban", "bannedBy", target.SteamId64, target.PlayerName, reason, player?.PlayerName ?? "Console", player?.SteamID, duration, raitoConfig.ServerId);
            WriteRaitoAdminLog(player, "player.ban", target, reason, new { duration = FormatDuration(duration) });
            KickTarget(target.Player, $"Banned: {reason}");
            Server.PrintToChatAll($" {ChatColors.Green}[BETHECHAMP]{ChatColors.Default} Discipline | {ChatColors.Red}{target.PlayerName}{ChatColors.Default} banned for {FormatDuration(duration)} | {reason}");
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Ban failed: {ex.Message}");
            ReplyToUserCommand(player, "Discipline service rejected the ban request.");
        }
    }

    [ConsoleCommand("css_unban", "Removes a player ban by SteamID64")]
    public void OnRaitoUnbanCommand(CCSPlayerController? player, CommandInfo command)
        => ExecuteRaitoUnbanCommand(player, GetCommandArguments(command));

    private void ExecuteRaitoUnbanCommand(CCSPlayerController? player, IReadOnlyList<string> arguments)
    {
        if (!RaitoCanExecute(player, "css_unban", "@css/unban"))
        {
            SendPlayerNotAdminMessage(player);
            return;
        }

        if (raitoDatabase is null || arguments.Count < 1 || !ulong.TryParse(arguments[0], out ulong steamId64))
        {
            ReplyToUserCommand(player, $"Command format: {ChatColors.Green}.unban <steamid64>{ChatColors.Default}");
            return;
        }

        try
        {
            raitoDatabase.ResolveModeration("Ban", steamId64, player?.PlayerName ?? "Console", player?.SteamID, "Ban revoked by administrator.");
            WriteRaitoAdminLog(player, "player.unban", new RaitoTarget(null, steamId64, steamId64.ToString()));
            ReplyToUserCommand(player, $"Ban cleared for SteamID64 {ChatColors.Green}{steamId64}{ChatColors.Default}.");
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Unban failed: {ex.Message}");
            ReplyToUserCommand(player, "Discipline service rejected the unban request.");
        }
    }

    [ConsoleCommand("css_mute", "Mutes a player's chat")]
    public void OnRaitoMuteCommand(CCSPlayerController? player, CommandInfo command)
        => ExecuteRaitoModerationCommand(player, GetCommandArguments(command), "css_mute", "Mute", "mutedBy", "player.mute", "You are muted and cannot use chat.");

    [ConsoleCommand("css_unmute", "Removes a chat mute")]
    public void OnRaitoUnmuteCommand(CCSPlayerController? player, CommandInfo command)
        => ExecuteRaitoRemoveModerationCommand(player, GetCommandArguments(command), "css_unmute", "Mute", "player.unmute");

    [ConsoleCommand("css_gag", "Gags a player's voice")]
    public void OnRaitoGagCommand(CCSPlayerController? player, CommandInfo command)
        => ExecuteRaitoModerationCommand(player, GetCommandArguments(command), "css_gag", "Gag", "gaggedBy", "player.gag", "You are voice-gagged.", true);

    [ConsoleCommand("css_ungag", "Removes a voice gag")]
    public void OnRaitoUngagCommand(CCSPlayerController? player, CommandInfo command)
        => ExecuteRaitoRemoveModerationCommand(player, GetCommandArguments(command), "css_ungag", "Gag", "player.ungag", true);

    [ConsoleCommand("css_kick", "Kicks a player")]
    public void OnRaitoKickCommand(CCSPlayerController? player, CommandInfo command)
        => ExecuteRaitoKickCommand(player, GetCommandArguments(command));

    private void ExecuteRaitoKickCommand(CCSPlayerController? player, IReadOnlyList<string> arguments)
    {
        if (!RaitoCanExecute(player, "css_kick", "@css/kick"))
        {
            SendPlayerNotAdminMessage(player);
            return;
        }

        if (arguments.Count < 1)
        {
            ReplyToUserCommand(player, $"Command format: {ChatColors.Green}.kick <player> [reason]{ChatColors.Default}");
            return;
        }

        RaitoTarget? target = ResolveTarget(arguments[0], allowOfflineSteamId: false);
        if (target?.Player is null)
        {
            ReplyToUserCommand(player, "No matching online player.");
            return;
        }

        string reason = GetRemainingArgs(arguments, 1, "Kicked by BETHECHAMP admin");
        KickTarget(target.Player, reason);
        try
        {
            raitoDatabase?.CreateCompletedModerationCase("GAME_KICK", target.SteamId64, target.PlayerName, player?.PlayerName ?? "Console", player?.SteamID, reason, raitoConfig.ServerId);
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Kick case logging failed: {ex.Message}");
        }
        WriteRaitoAdminLog(player, "player.kick", target, reason);
        Server.PrintToChatAll($" {ChatColors.Green}[BETHECHAMP]{ChatColors.Default} Discipline | {ChatColors.Red}{target.PlayerName}{ChatColors.Default} removed | {reason}");
    }

    [ConsoleCommand("css_team", "Moves player(s) to CT, T, or spectator")]
    public void OnRaitoTeamCommand(CCSPlayerController? player, CommandInfo command)
        => ExecuteRaitoTeamCommand(player, GetCommandArguments(command));

    private void ExecuteRaitoTeamCommand(CCSPlayerController? player, IReadOnlyList<string> arguments)
    {
        if (!RaitoCanExecute(player, "css_team", "@css/generic"))
        {
            SendPlayerNotAdminMessage(player);
            return;
        }

        if (arguments.Count < 2 || !TryParseTeam(arguments[1], out CsTeam team, out string teamName))
        {
            ReplyToUserCommand(player, $"Command format: {ChatColors.Green}.team <player|@all> <ct|t|spec>{ChatColors.Default}");
            return;
        }

        var targets = ResolveTargets(arguments[0]).ToList();
        if (targets.Count == 0)
        {
            ReplyToUserCommand(player, "No matching online player.");
            return;
        }

        foreach (var target in targets)
        {
            target.SwitchTeam(team);
            WriteRaitoAdminLog(player, "player.team", new RaitoTarget(target, target.SteamID, target.PlayerName), metadata: new { team = teamName });
        }

        Server.PrintToChatAll($" {ChatColors.Green}[BETHECHAMP]{ChatColors.Default} Team control | {targets.Count} player(s) moved to {ChatColors.Green}{teamName}{ChatColors.Default}.");
    }

    private void ExecuteRaitoModerationCommand(
        CCSPlayerController? player,
        IReadOnlyList<string> arguments,
        string commandName,
        string tableName,
        string actorColumn,
        string action,
        string confirmation,
        bool applyVoiceState = false)
    {
        if (!RaitoCanExecute(player, commandName, "@css/chat"))
        {
            SendPlayerNotAdminMessage(player);
            return;
        }

        if (raitoDatabase is null || arguments.Count < 1)
        {
            ReplyToUserCommand(player, $"Command format: {ChatColors.Green}.{commandName[4..]} <player> [30m|2h|7d|perm] [reason]{ChatColors.Default}");
            return;
        }

        RaitoTarget? target = ResolveTarget(arguments[0], allowOfflineSteamId: false);
        if (target?.Player is null)
        {
            ReplyToUserCommand(player, "No matching online player.");
            return;
        }

        ParseDurationAndReason(arguments, 1, out TimeSpan? duration, out string reason);
        try
        {
            raitoDatabase.UpsertModeration(tableName, actorColumn, target.SteamId64, target.PlayerName, reason, player?.PlayerName ?? "Console", player?.SteamID, duration, raitoConfig.ServerId);
            if (applyVoiceState) target.Player.VoiceFlags = VoiceFlags.Muted;
            WriteRaitoAdminLog(player, action, target, reason, new { duration = FormatDuration(duration) });
            Server.PrintToChatAll($" {ChatColors.Green}[BETHECHAMP]{ChatColors.Default} Discipline | {ChatColors.Red}{target.PlayerName}{ChatColors.Default}: {confirmation} ({FormatDuration(duration)})");
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] {action} failed: {ex.Message}");
            ReplyToUserCommand(player, "Discipline service rejected the moderation request.");
        }
    }

    private void ExecuteRaitoRemoveModerationCommand(CCSPlayerController? player, IReadOnlyList<string> arguments, string commandName, string tableName, string action, bool clearVoiceState = false)
    {
        if (!RaitoCanExecute(player, commandName, "@css/chat"))
        {
            SendPlayerNotAdminMessage(player);
            return;
        }

        if (raitoDatabase is null || arguments.Count < 1)
        {
            ReplyToUserCommand(player, $"Command format: {ChatColors.Green}.{commandName[4..]} <player>{ChatColors.Default}");
            return;
        }

        RaitoTarget? target = ResolveTarget(arguments[0], allowOfflineSteamId: true);
        if (target is null)
        {
            ReplyToUserCommand(player, "Player lookup failed.");
            return;
        }

        try
        {
            raitoDatabase.ResolveModeration(tableName, target.SteamId64, player?.PlayerName ?? "Console", player?.SteamID, "Punishment revoked by administrator.");
            if (clearVoiceState && target.Player is not null) target.Player.VoiceFlags = VoiceFlags.Normal;
            WriteRaitoAdminLog(player, action, target);
            ReplyToUserCommand(player, $"Discipline cleared for {ChatColors.Green}{target.PlayerName}{ChatColors.Default}.");
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] {action} failed: {ex.Message}");
            ReplyToUserCommand(player, "Discipline service rejected the moderation request.");
        }
    }

    private RaitoTarget? ResolveTarget(string argument, bool allowOfflineSteamId)
    {
        if (int.TryParse(argument, out int userId))
        {
            var player = Utilities.GetPlayers().FirstOrDefault(candidate => candidate.IsValid && candidate.UserId == userId);
            if (player is not null) return new RaitoTarget(player, player.SteamID, player.PlayerName);
        }

        if (ulong.TryParse(argument, out ulong steamId64) && steamId64 > 10000000000000000)
        {
            var player = Utilities.GetPlayers().FirstOrDefault(candidate => candidate.IsValid && candidate.SteamID == steamId64);
            return player is null && allowOfflineSteamId
                ? new RaitoTarget(null, steamId64, steamId64.ToString())
                : player is null ? null : new RaitoTarget(player, player.SteamID, player.PlayerName);
        }

        var exact = Utilities.GetPlayers().FirstOrDefault(candidate => candidate.IsValid && candidate.PlayerName.Equals(argument, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return new RaitoTarget(exact, exact.SteamID, exact.PlayerName);

        var partial = Utilities.GetPlayers().FirstOrDefault(candidate => candidate.IsValid && candidate.PlayerName.Contains(argument, StringComparison.OrdinalIgnoreCase));
        return partial is null ? null : new RaitoTarget(partial, partial.SteamID, partial.PlayerName);
    }

    private IEnumerable<CCSPlayerController> ResolveTargets(string argument)
    {
        if (argument.Equals("@all", StringComparison.OrdinalIgnoreCase))
        {
            return Utilities.GetPlayers().Where(player => player.IsValid && !player.IsBot);
        }

        var target = ResolveTarget(argument, allowOfflineSteamId: false);
        return target?.Player is null ? Enumerable.Empty<CCSPlayerController>() : new[] { target.Player };
    }

    private static bool TryParseTeam(string argument, out CsTeam team, out string teamName)
    {
        switch (argument.ToLowerInvariant())
        {
            case "ct": team = CsTeam.CounterTerrorist; teamName = "CT"; return true;
            case "t": team = CsTeam.Terrorist; teamName = "T"; return true;
            case "spec":
            case "spectator": team = CsTeam.Spectator; teamName = "Spectator"; return true;
            default: team = CsTeam.None; teamName = string.Empty; return false;
        }
    }

    private static void ParseDurationAndReason(IReadOnlyList<string> arguments, int firstOptionalArg, out TimeSpan? duration, out string reason)
    {
        duration = null;
        int reasonStart = firstOptionalArg;
        if (arguments.Count > firstOptionalArg && TryParseDuration(arguments[firstOptionalArg], out TimeSpan? parsed))
        {
            duration = parsed;
            reasonStart++;
        }

        reason = GetRemainingArgs(arguments, reasonStart, "Action applied by BETHECHAMP admin");
    }

    private static bool TryParseDuration(string value, out TimeSpan? duration)
    {
        duration = null;
        if (value.Equals("perm", StringComparison.OrdinalIgnoreCase) || value.Equals("permanent", StringComparison.OrdinalIgnoreCase) || value == "0") return true;
        if (value.Length < 2 || !int.TryParse(value[..^1], out int amount) || amount <= 0) return false;

        duration = char.ToLowerInvariant(value[^1]) switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            'w' => TimeSpan.FromDays(amount * 7),
            _ => null
        };
        return duration is not null;
    }

    private static string GetRemainingArgs(IReadOnlyList<string> arguments, int firstArgIndex, string fallback)
    {
        if (arguments.Count <= firstArgIndex) return fallback;
        string remaining = string.Join(' ', arguments.Skip(firstArgIndex)).Trim().Trim('"');
        return string.IsNullOrWhiteSpace(remaining) ? fallback : remaining;
    }

    private static string[] GetCommandArguments(CommandInfo command) =>
        Enumerable.Range(1, Math.Max(0, command.ArgCount - 1))
            .Select(command.ArgByIndex)
            .ToArray();

    private static string FormatDuration(TimeSpan? duration) => duration is null ? "permanent" : duration.Value.TotalDays >= 1 ? $"{duration.Value.TotalDays:0.#}d" : duration.Value.TotalHours >= 1 ? $"{duration.Value.TotalHours:0.#}h" : $"{duration.Value.TotalMinutes:0.#}m";

    private static void KickTarget(CCSPlayerController? player, string reason)
    {
        if (player?.UserId is null) return;
        string safeReason = reason.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Trim();
        Server.ExecuteCommand($"kickid {player.UserId} \"{safeReason}\"");
    }
}
