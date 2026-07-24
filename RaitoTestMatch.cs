using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace MatchZy;

public partial class MatchZy
{
    private const int RaitoTestTeamSize = 5;
    private const int RaitoTestShortMaxRounds = 6;
    private const int RaitoTestFullMaxRounds = 24;
    private const ulong RaitoTestBotSteamIdBase = 900_000_000_000_000;

    private bool isRaitoTestMatch;
    private bool raitoTestFullLength;
    private bool raitoTestPreviousKnifeRequired;
    private int raitoTestPreviousReadyRequired = 10;
    private string raitoTestPreviousHostname = string.Empty;
    private int raitoTestTerroristBotTarget;
    private int raitoTestCounterTerroristBotTarget;
    private bool raitoTestStatsRosterReady;
    private int raitoTestRosterCheckAttempts;

    [ConsoleCommand("css_testmatch", "Starts, stops, or reports a BETHECHAMP bot test match")]
    public void OnRaitoTestMatchCommand(CCSPlayerController? player, CommandInfo command) =>
        ExecuteRaitoTestMatchCommand(player, GetCommandArguments(command));

    internal void ExecuteRaitoTestMatchCommand(CCSPlayerController? player, IReadOnlyList<string> arguments)
    {
        if (!CanControlRaitoTestMatch(player))
        {
            ReplyToUserCommand(player, "Only the BETHECHAMP owner or server console can control test matches.");
            return;
        }

        string action = arguments.Count == 0 ? "short" : arguments[0].Trim().ToLowerInvariant();
        switch (action)
        {
            case "stop":
            case "end":
                StopRaitoTestMatch(player);
                return;
            case "status":
                ReplyToUserCommand(
                    player,
                    isRaitoTestMatch
                        ? $"Test match active: {(raitoTestFullLength ? "MR12" : "MR3")}, match ID {liveMatchId}."
                        : "No test match is active.");
                return;
            case "short":
            case "mr3":
                StartRaitoTestMatch(player, fullLength: false);
                return;
            case "full":
            case "mr12":
                StartRaitoTestMatch(player, fullLength: true);
                return;
            default:
                ReplyToUserCommand(player, $"Command format: {ChatColors.Green}.testmatch [short|full|status|stop]{ChatColors.Default}");
                return;
        }
    }

    private bool CanControlRaitoTestMatch(CCSPlayerController? player) =>
        player is null || GetRaitoRole(player.SteamID) == "OWNER";

    private void StartRaitoTestMatch(CCSPlayerController? actor, bool fullLength)
    {
        if (isRaitoTestMatch)
        {
            ReplyToUserCommand(actor, "A test match is already active. Use .testmatch stop first.");
            return;
        }

        if (matchStarted || isMatchSetup || isVeto || isPractice || isDryRun)
        {
            ReplyToUserCommand(actor, "Finish the current match or mode before starting a test match.");
            return;
        }

        List<CCSPlayerController> humans = Utilities.GetPlayers()
            .Where(player => player.IsValid && !player.IsBot && !player.IsHLTV && player.Connected == PlayerConnectedState.PlayerConnected)
            .ToList();
        if (humans.Count > RaitoTestTeamSize * 2)
        {
            ReplyToUserCommand(actor, "Test match rejected because more than ten human players are connected.");
            return;
        }

        int terrorists = humans.Count(player => player.TeamNum == (int)CsTeam.Terrorist);
        int counterTerrorists = humans.Count(player => player.TeamNum == (int)CsTeam.CounterTerrorist);
        if (terrorists > RaitoTestTeamSize || counterTerrorists > RaitoTestTeamSize)
        {
            ReplyToUserCommand(actor, "Test match rejected because a team already has more than five human players.");
            return;
        }

        foreach (CCSPlayerController spectator in humans.Where(player => player.TeamNum is not (int)CsTeam.Terrorist and not (int)CsTeam.CounterTerrorist))
        {
            CsTeam destination = terrorists <= counterTerrorists ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            SwitchPlayerTeam(spectator, destination);
            if (destination == CsTeam.Terrorist) terrorists++;
            else counterTerrorists++;
        }

        int terroristBots = RaitoTestTeamSize - terrorists;
        int counterTerroristBots = RaitoTestTeamSize - counterTerrorists;

        raitoTestPreviousKnifeRequired = isKnifeRequired;
        raitoTestPreviousReadyRequired = minimumReadyRequired;
        raitoTestPreviousHostname = GetConvarStringValue(ConVar.Find("hostname"));
        raitoTestFullLength = fullLength;
        raitoTestTerroristBotTarget = terroristBots;
        raitoTestCounterTerroristBotTarget = counterTerroristBots;
        raitoTestStatsRosterReady = false;
        raitoTestRosterCheckAttempts = 0;
        isRaitoTestMatch = true;
        isKnifeRequired = false;
        minimumReadyRequired = 0;
        isPreVeto = false;
        isVeto = false;

        matchzyTeam1.teamName = "BETHECHAMP_TEST_CT";
        matchzyTeam2.teamName = "BETHECHAMP_TEST_T";
        matchConfig.NumMaps = 1;
        matchConfig.CurrentMapNumber = 0;
        matchConfig.SeriesCanClinch = true;

        PopulateRaitoTestBots(terroristBots, counterTerroristBots);
        AddTimer(1.5f, BeginRaitoTestMatchLive, TimerFlags.STOP_ON_MAPCHANGE);

        WriteRaitoAdminLog(actor, "match.test.start", metadata: new
        {
            format = fullLength ? "MR12" : "MR3",
            humans = humans.Count,
            bots = terroristBots + counterTerroristBots,
            map = Server.MapName
        });
        ReplyToUserCommand(actor, $"Starting BETHECHAMP {(fullLength ? "MR12" : "MR3")} test match with {terroristBots + counterTerroristBots} bots.");
        Logger.LogInformation("[BETHECHAMP TEST] Preparing {Format} with {Humans} humans and {Bots} bots.", fullLength ? "MR12" : "MR3", humans.Count, terroristBots + counterTerroristBots);
        PrintToAllChat($"{ChatColors.Green}TEST MATCH{ChatColors.Default} | {(fullLength ? "MR12" : "MR3")} | Filling teams to 5v5. Ranking is enabled for human players.");
    }

    private void BeginRaitoTestMatchLive()
    {
        if (!isRaitoTestMatch) return;

        try
        {
            HandleMatchStart();
            ApplyRaitoTestMatchConfig();
            AddTimer(1.25f, () =>
            {
                ApplyRaitoTestMatchConfig();
                EnsureRaitoTestBots();
            }, TimerFlags.STOP_ON_MAPCHANGE);
            AddTimer(3.0f, ReportRaitoTestRoster, TimerFlags.STOP_ON_MAPCHANGE);
            Logger.LogInformation("[BETHECHAMP TEST] Live transition complete for match ID {MatchId}.", liveMatchId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BETHECHAMP TEST] Failed to start the test match.");
            ResetMatch();
        }
    }

    private void ApplyRaitoTestMatchConfig()
    {
        if (!isRaitoTestMatch) return;

        int maxRounds = raitoTestFullLength ? RaitoTestFullMaxRounds : RaitoTestShortMaxRounds;
        int totalBots = raitoTestTerroristBotTarget + raitoTestCounterTerroristBotTarget;
        bool nativeMapVote = raitoConfig.MapVoteEnabled && raitoConfig.NativeEndMatchMapVoteEnabled;
        Server.ExecuteCommand($"mp_maxrounds {maxRounds};mp_overtime_enable 0;mp_match_can_clinch 1;mp_halftime 1;mp_limitteams 0;mp_autoteambalance 0;mp_friendlyfire 0;mp_endmatch_votenextmap {(nativeMapVote ? 1 : 0)};mp_endmatch_votenextmap_keepcurrent 0;mp_endmatch_votenextleveltime {raitoConfig.MapVoteDurationSeconds};mp_match_end_restart {(nativeMapVote ? 0 : 1)};{(nativeMapVote ? "mapgroup mg_active;" : string.Empty)}bot_quota_mode normal;bot_quota {totalBots};bot_defer_to_human_goals 0;bot_defer_to_human_items 0;bot_difficulty 2;");
        if (!raitoTestFullLength)
        {
            Server.ExecuteCommand("mp_freezetime 2;mp_round_restart_delay 2;mp_halftime_duration 3;mp_team_intro_time 0;");
        }
    }

    private void PopulateRaitoTestBots(int terroristBots, int counterTerroristBots)
    {
        if (!isRaitoTestMatch) return;

        int totalBots = terroristBots + counterTerroristBots;
        Server.ExecuteCommand("bot_kick;bot_quota_mode normal;bot_quota 0;bot_join_after_player 0;");
        for (int i = 0; i < terroristBots; i++) Server.ExecuteCommand("bot_add_t");
        for (int i = 0; i < counterTerroristBots; i++) Server.ExecuteCommand("bot_add_ct");
        Server.ExecuteCommand($"bot_quota {totalBots};bot_join_team any;");
    }

    private void EnsureRaitoTestBots()
    {
        if (!isRaitoTestMatch) return;

        List<CCSPlayerController> bots = Utilities.GetPlayers().Where(player => player.IsValid && player.IsBot && !player.IsHLTV).ToList();
        int terroristBots = bots.Count(player => player.TeamNum == (int)CsTeam.Terrorist);
        int counterTerroristBots = bots.Count(player => player.TeamNum == (int)CsTeam.CounterTerrorist);
        for (int i = terroristBots; i < raitoTestTerroristBotTarget; i++) Server.ExecuteCommand("bot_add_t");
        for (int i = counterTerroristBots; i < raitoTestCounterTerroristBotTarget; i++) Server.ExecuteCommand("bot_add_ct");
        Server.ExecuteCommand($"bot_quota {raitoTestTerroristBotTarget + raitoTestCounterTerroristBotTarget};bot_join_team any;");
    }

    private void ReportRaitoTestRoster()
    {
        if (!isRaitoTestMatch) return;

        List<CCSPlayerController> players = Utilities.GetPlayers().Where(player => player.IsValid && !player.IsHLTV).ToList();
        int humans = players.Count(player => !player.IsBot);
        int bots = players.Count(player => player.IsBot);
        int terrorists = players.Count(player => player.TeamNum == (int)CsTeam.Terrorist);
        int counterTerrorists = players.Count(player => player.TeamNum == (int)CsTeam.CounterTerrorist);
        int terroristBots = players.Count(player => player.IsBot && player.TeamNum == (int)CsTeam.Terrorist);
        int counterTerroristBots = players.Count(player => player.IsBot && player.TeamNum == (int)CsTeam.CounterTerrorist);
        bool stableRoster = terrorists == RaitoTestTeamSize
            && counterTerrorists == RaitoTestTeamSize
            && terroristBots == raitoTestTerroristBotTarget
            && counterTerroristBots == raitoTestCounterTerroristBotTarget;

        if (!stableRoster)
        {
            raitoTestRosterCheckAttempts++;
            Logger.LogWarning(
                "[BETHECHAMP TEST] Roster not stable yet (attempt {Attempt}/5): humans={Humans}, bots={Bots}, T={Terrorists}, CT={CounterTerrorists}.",
                raitoTestRosterCheckAttempts,
                humans,
                bots,
                terrorists,
                counterTerrorists);

            if (raitoTestRosterCheckAttempts < 5)
            {
                EnsureRaitoTestBots();
                AddTimer(1.0f, ReportRaitoTestRoster, TimerFlags.STOP_ON_MAPCHANGE);
                return;
            }

            Logger.LogError("[BETHECHAMP TEST] Could not establish a stable 5v5 roster; aborting test match {MatchId}.", liveMatchId);
            StopRaitoTestMatch(null);
            return;
        }

        raitoTestStatsRosterReady = true;
        Log($"[BETHECHAMP TEST] Roster ready: humans={humans}, bots={bots}, T={terrorists}, CT={counterTerrorists}, matchId={liveMatchId}");
        Logger.LogInformation("[BETHECHAMP TEST] Roster ready: humans={Humans}, bots={Bots}, T={Terrorists}, CT={CounterTerrorists}, matchId={MatchId}.", humans, bots, terrorists, counterTerrorists, liveMatchId);
        PrintToAllChat($"{ChatColors.Green}TEST ROSTER{ChatColors.Default} | T {terrorists}/5 | CT {counterTerrorists}/5 | {bots} bots.");
    }

    private void StopRaitoTestMatch(CCSPlayerController? actor)
    {
        if (!isRaitoTestMatch)
        {
            ReplyToUserCommand(actor, "No test match is active.");
            return;
        }

        long stoppedMatchId = liveMatchId;
        int stoppedMapNumber = matchConfig.CurrentMapNumber;
        (int team1MapScore, int team2MapScore) = GetTeamsScore();
        int team1SeriesScore = matchzyTeam1.seriesScore;
        int team2SeriesScore = matchzyTeam2.seriesScore;
        WriteRaitoAdminLog(actor, "match.test.stop", metadata: new { matchId = stoppedMatchId, map = Server.MapName });

        if (stoppedMatchId > 0)
        {
            Task.Run(async () =>
            {
                await database.SetMapEndData(stoppedMatchId, stoppedMapNumber, "Aborted", team1MapScore, team2MapScore, team1SeriesScore, team2SeriesScore);
                await database.SetMatchEndData(stoppedMatchId, "Aborted", team1SeriesScore, team2SeriesScore);
            });
        }

        ResetMatch();
        PrintToAllChat($"{ChatColors.Green}TEST MATCH{ChatColors.Default} | Stopped. Normal ten-player warmup restored.");
    }

    private void ResetRaitoTestMatchState()
    {
        if (!isRaitoTestMatch) return;

        isRaitoTestMatch = false;
        raitoTestFullLength = false;
        raitoTestTerroristBotTarget = 0;
        raitoTestCounterTerroristBotTarget = 0;
        raitoTestStatsRosterReady = false;
        raitoTestRosterCheckAttempts = 0;
        isKnifeRequired = raitoTestPreviousKnifeRequired;
        minimumReadyRequired = raitoTestPreviousReadyRequired;
        if (!string.IsNullOrWhiteSpace(raitoTestPreviousHostname))
        {
            string escapedHostname = raitoTestPreviousHostname.Replace("\\", "\\\\").Replace("\"", "\\\"");
            Server.ExecuteCommand($"hostname \"{escapedHostname}\"");
        }
        raitoTestPreviousHostname = string.Empty;
        Server.ExecuteCommand("bot_kick;bot_quota 0;bot_quota_mode normal;bot_join_after_player 1;");
    }

    private IEnumerable<CCSPlayerController> GetRaitoStatsPlayers()
    {
        List<CCSPlayerController> players = playerData.Values.ToList();
        if (isRaitoTestMatch && raitoTestStatsRosterReady)
        {
            players.AddRange(Utilities.GetPlayers().Where(player => player.IsValid && player.IsBot && !player.IsHLTV));
        }
        return players;
    }

    private static ulong GetRaitoStatsSteamId(CCSPlayerController player)
    {
        if (!player.IsBot) return player.SteamID;
        int uniqueId = player.UserId ?? player.Slot + 1;
        return RaitoTestBotSteamIdBase + (ulong)Math.Max(1, uniqueId);
    }
}
