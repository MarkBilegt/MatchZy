using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace MatchZy;

public sealed record RaitoTarget(CCSPlayerController? Player, ulong SteamId64, string PlayerName);

public partial class MatchZy
{
    internal bool TryHandleRaitoChatCommand(CCSPlayerController? player, string rawMessage)
    {
        string[] tokens = Regex.Matches(rawMessage.Trim(), "\\\"([^\\\"]*)\\\"|(\\S+)")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .ToArray();

        // CounterStrikeSharp owns the ! form. This listener only supplies the dot
        // aliases for non-administrative BETHECHAMP community commands.
        if (tokens.Length == 0 || tokens[0][0] != '.')
            return false;

        string command = tokens[0][1..].ToLowerInvariant();
        string[] arguments = tokens.Skip(1).ToArray();
        switch (command)
        {
            case "admin":
                ExecuteRaitoAdminCall(player, arguments);
                return true;
            case "mapvote":
                ExecuteRaitoMapVote(player, arguments);
                return true;
            case "testmatch":
                ExecuteRaitoTestMatchCommand(player, arguments);
                return true;
            default:
                // Moderation commands intentionally fall through to SimpleAdmin.
                return false;
        }
    }

    private static string[] GetCommandArguments(CommandInfo command) =>
        Enumerable.Range(1, Math.Max(0, command.ArgCount - 1))
            .Select(command.ArgByIndex)
            .ToArray();

    private static void KickTarget(CCSPlayerController? player, string reason)
    {
        if (player?.UserId is null)
            return;

        string safeReason = reason
            .Replace("\"", "'")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        Server.ExecuteCommand($"kickid {player.UserId} \"{safeReason}\"");
    }
}
