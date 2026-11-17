using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace MatchZy;

// Reports every finished map to the REIGN Discord bot so "Мөнгө Базуулах" (wager) tickets can be linked to the match
// automatically: the bot matches the two wagering SteamIDs against the players listed here.
public partial class MatchZy
{
    public string wagerUrl = "";
    public string wagerKey = "";
    private static readonly HttpClient wagerHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    [ConsoleCommand("matchzy_wager_url", "URL of the Discord bot's POST /wager/result endpoint. Empty = off.")]
    public void WagerUrlCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null) return;
        wagerUrl = GetSettingArgument(command).Trim();
    }

    [ConsoleCommand("matchzy_wager_key", "API key sent as x-api-key to the wager endpoint.")]
    public void WagerKeyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null) return;
        wagerKey = GetSettingArgument(command).Trim();
    }

    // Must run while players are still in their end-of-map teams (HandleMatchEnd).
    private void ReportWagerResult(int t1score, int t2score)
    {
        if (string.IsNullOrWhiteSpace(wagerUrl)) return;
        try
        {
            int ctScore = 0, tScore = 0;
            foreach (var team in Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager"))
            {
                if (team.TeamNum == (int)CsTeam.CounterTerrorist) ctScore = team.Score;
                else if (team.TeamNum == (int)CsTeam.Terrorist) tScore = team.Score;
            }

            int winnerTeam = ctScore > tScore ? (int)CsTeam.CounterTerrorist : tScore > ctScore ? (int)CsTeam.Terrorist : 0;

            var players = Utilities.GetPlayers()
                .Where(p => p != null && p.IsValid && !p.IsBot && !p.IsHLTV && p.SteamID > 0
                            && (p.TeamNum == (int)CsTeam.Terrorist || p.TeamNum == (int)CsTeam.CounterTerrorist))
                .Select(p => new { steamId = p.SteamID.ToString(), name = p.PlayerName, team = p.TeamNum, won = winnerTeam != 0 && p.TeamNum == winnerTeam })
                .ToList();

            var payload = new
            {
                matchId = liveMatchId,
                map = Server.MapName,
                ctScore,
                tScore,
                winnerTeam,
                players,
                reportedAt = DateTime.UtcNow
            };

            string json = JsonSerializer.Serialize(payload);
            string url = wagerUrl;
            string key = wagerKey;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    if (!string.IsNullOrEmpty(key)) request.Headers.Add("x-api-key", key);
                    var response = await wagerHttpClient.SendAsync(request);
                    Log($"[WagerReport] {(int)response.StatusCode}");
                }
                catch (Exception e)
                {
                    Log($"[WagerReport FATAL] {e.Message}");
                }
            });
        }
        catch (Exception e)
        {
            Log($"[WagerReport FATAL] {e.Message}");
        }
    }
}
