using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace MatchZy;

public partial class MatchZy
{
    private sealed class RaitoPublicMapVote
    {
        public required string Id { get; init; }
        public required IReadOnlyList<RaitoMapPoolItem> Candidates { get; init; }
        public required DateTime EndsAt { get; init; }
        public required string WinnerName { get; init; }
        public required int Team1Score { get; init; }
        public required int Team2Score { get; init; }
        public Dictionary<ulong, string> Ballots { get; } = new();
        public bool Finalized { get; set; }
    }

    private RaitoPublicMapVote? raitoPublicMapVote;

    private void InitializeRaitoCommunityFeatures()
    {
        AddCommandListener("callvote", OnRaitoCallVote);
        Server.ExecuteCommand($"sv_visiblemaxplayers {raitoConfig.PublicSlots}");
        ApplyRaitoServerRules();
        RegisterEventHandler<EventRoundStart>(OnRaitoEnforceServerRules, HookMode.Pre);
    }

    private HookResult OnRaitoEnforceServerRules(EventRoundStart @event, GameEventInfo info)
    {
        ApplyRaitoServerRules();
        return HookResult.Continue;
    }

    private void ApplyRaitoServerRules()
    {
        Server.ExecuteCommand("mp_friendlyfire 0");
        if (!raitoConfig.MapVoteEnabled ||
            !raitoConfig.NativeEndMatchMapVoteEnabled ||
            isMatchSetup)
        {
            Server.ExecuteCommand("mp_endmatch_votenextmap 0");
            return;
        }

        Server.ExecuteCommand(
            $"mapgroup mg_active;mp_endmatch_votenextmap 1;mp_endmatch_votenextmap_keepcurrent 0;" +
            $"mp_endmatch_votenextleveltime {raitoConfig.MapVoteDurationSeconds};mp_match_end_restart 0;");
    }

    private HookResult OnRaitoCallVote(CCSPlayerController? initiator, CommandInfo command)
    {
        if (!raitoConfig.VoteKickImmunityEnabled || !IsPlayerValid(initiator)) return HookResult.Continue;
        if (!command.ArgByIndex(1).Equals("kick", StringComparison.OrdinalIgnoreCase)) return HookResult.Continue;
        if (!int.TryParse(command.ArgByIndex(2), out int targetUserId)) return HookResult.Continue;

        CCSPlayerController? target = Utilities.GetPlayers().FirstOrDefault(candidate =>
            candidate.IsValid && candidate.UserId == targetUserId);
        if (!IsPlayerValid(target) || target!.IsBot || target.IsHLTV) return HookResult.Continue;

        if (AdminManager.CanPlayerTarget(initiator!, target))
            return HookResult.Continue;

        try
        {
            ReplyToUserCommand(initiator, $"{target!.PlayerName} is protected by SimpleAdmin immunity.");
            WriteRaitoAdminLog(
                initiator,
                "vote.kick.blocked",
                new RaitoTarget(target, target.SteamID, target.PlayerName),
                metadata: new { authority = "SimpleAdmin" });
            return HookResult.Stop;
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] SimpleAdmin immunity audit failed for {target!.SteamID}: {ex.Message}");
            return HookResult.Stop;
        }
    }

    private void EnforceRaitoReservedSlot(ulong steamId64)
    {
        if (!raitoConfig.ReservedSlotEnabled || raitoConfig.ReservedSlots <= 0) return;
        CCSPlayerController? joining = Utilities.GetPlayers().FirstOrDefault(player =>
            player.IsValid && !player.IsBot && !player.IsHLTV && player.SteamID == steamId64);
        if (!IsPlayerValid(joining)) return;

        int humans = Utilities.GetPlayers().Count(player => player.IsValid && !player.IsBot && !player.IsHLTV);
        if (humans <= raitoConfig.PublicSlots) return;

        string role;
        try { role = GetRaitoRoleFresh(steamId64); }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Reserved-slot authorization failed for {steamId64}: {ex.Message}");
            role = "USER";
        }
        bool hasSimpleAdminReservation = AdminManager.PlayerHasPermissions(
            new SteamID(steamId64),
            "@css/reservation");

        if (hasSimpleAdminReservation || IsRaitoPrivilegedRole(role))
        {
            joining!.ChangeTeam(CsTeam.Spectator);
            ReplyToUserCommand(joining, "You are using the reserved privileged slot as a spectator.");
            return;
        }

        if (joining!.UserId is not null)
            Server.ExecuteCommand($"kickid {joining.UserId} \"Server is 11/11. The final slot is reserved for VIP and staff.\"");
    }

    [ConsoleCommand("css_admin", "Calls an administrator with a private reason")]
    public void OnRaitoAdminCallCommand(CCSPlayerController? player, CommandInfo command)
        => ExecuteRaitoAdminCall(player, GetCommandArguments(command));

    private void ExecuteRaitoAdminCall(CCSPlayerController? player, IReadOnlyList<string> arguments)
    {
        if (!raitoConfig.AdminCallsEnabled || !IsPlayerValid(player) || player!.IsBot || player.IsHLTV) return;
        string reason = string.Join(' ', arguments).Trim();
        reason = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (reason.Length < 5 || reason.Length > 200)
        {
            ReplyToUserCommand(player, "Command format: !admin <5-200 character reason>");
            return;
        }
        if (raitoDatabase is null)
        {
            ReplyToUserCommand(player, "The admin-call service is temporarily offline.");
            return;
        }

        try
        {
            raitoDatabase.CreateAdminCall(player.SteamID, player.PlayerName, raitoConfig.ServerId, Server.MapName, GetRaitoMatchState(), reason);
            WriteRaitoAdminLog(player, "admin_call.created", new RaitoTarget(player, player.SteamID, player.PlayerName), reason, new { serverId = raitoConfig.ServerId, map = Server.MapName });
            ReplyToUserCommand(player, "Your admin call was sent privately. Staff will be notified in Discord.");
        }
        catch (InvalidOperationException ex) { WriteRaitoAdminLog(player, "admin_call.rejected", new RaitoTarget(player, player.SteamID, player.PlayerName), ex.Message); ReplyToUserCommand(player, ex.Message); }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Admin call failed: {ex.Message}");
            ReplyToUserCommand(player, "The admin-call service could not accept the request.");
        }
    }

    [ConsoleCommand("css_mapvote", "Votes for the next public map")]
    public void OnRaitoMapVoteCommand(CCSPlayerController? player, CommandInfo command)
        => ExecuteRaitoMapVote(player, GetCommandArguments(command));

    private void ExecuteRaitoMapVote(CCSPlayerController? player, IReadOnlyList<string> arguments)
    {
        RaitoPublicMapVote? vote = raitoPublicMapVote;
        if (!IsPlayerValid(player) || player!.IsBot || player.IsHLTV || vote is null || vote.Finalized || vote.EndsAt <= DateTime.UtcNow)
        {
            ReplyToUserCommand(player, "There is no active public map vote.");
            return;
        }
        if (arguments.Count != 1)
        {
            ReplyToUserCommand(player, $"Vote with !mapvote <map>. Choices: {string.Join(", ", vote.Candidates.Select(item => item.MapName))}");
            return;
        }
        RaitoMapPoolItem? selected = vote.Candidates.FirstOrDefault(item =>
            item.MapName.Equals(arguments[0], StringComparison.OrdinalIgnoreCase) ||
            item.DisplayName.Equals(arguments[0], StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            ReplyToUserCommand(player, "That map is not a candidate in this vote.");
            return;
        }

        vote.Ballots[player.SteamID] = selected.MapName;
        try { raitoDatabase?.UpsertMapVoteBallot(vote.Id, player.SteamID, player.PlayerName, selected.MapName); }
        catch (Exception ex) { Log($"[BETHECHAMP] Could not persist map ballot: {ex.Message}"); }
        ReplyToUserCommand(player, $"Your vote is now {selected.DisplayName}. You may change it until voting closes.");
    }

    private bool BeginRaitoPublicMapVote(string winnerName, int team1Score, int team2Score)
    {
        if (!raitoConfig.MapVoteEnabled ||
            raitoConfig.NativeEndMatchMapVoteEnabled ||
            raitoDatabase is null ||
            raitoPublicMapVote is not null) return false;
        try
        {
            IReadOnlyList<RaitoMapPoolItem> pool = raitoDatabase.GetEnabledMapPool();
            if (pool.Count < 2) return false;
            HashSet<string> recent = raitoDatabase.GetRecentMaps(raitoConfig.ServerId, 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<RaitoMapPoolItem> preferred = pool.Where(item =>
                !item.MapName.Equals(Server.MapName, StringComparison.OrdinalIgnoreCase) && !recent.Contains(item.MapName)).ToList();
            List<RaitoMapPoolItem> relaxed = pool.Where(item =>
                !item.MapName.Equals(Server.MapName, StringComparison.OrdinalIgnoreCase) && preferred.All(first => !first.MapName.Equals(item.MapName, StringComparison.OrdinalIgnoreCase))).ToList();
            List<RaitoMapPoolItem> candidates = preferred.OrderBy(_ => Random.Shared.Next())
                .Concat(relaxed.OrderBy(_ => Random.Shared.Next())).Take(5).ToList();
            if (candidates.Count < Math.Min(3, pool.Count - 1)) return false;

            DateTime endsAt = DateTime.UtcNow.AddSeconds(raitoConfig.MapVoteDurationSeconds);
            string id = raitoDatabase.CreateMapVote(raitoConfig.ServerId, Server.MapName, candidates.Select(item => item.MapName).ToList(), endsAt);
            raitoPublicMapVote = new RaitoPublicMapVote { Id = id, Candidates = candidates, EndsAt = endsAt, WinnerName = winnerName, Team1Score = team1Score, Team2Score = team2Score };
            WriteRaitoAdminLog(null, "map_vote.started", reason: $"Public map vote {id} opened.", metadata: new { voteId = id, candidates = candidates.Select(item => item.MapName).ToArray() });
            Server.PrintToChatAll($" {ChatColors.Green}[BETHECHAMP]{ChatColors.Default} Next-map vote is open for {raitoConfig.MapVoteDurationSeconds}s.");
            foreach (RaitoMapPoolItem item in candidates)
                Server.PrintToChatAll($" {ChatColors.Green}!mapvote {item.MapName}{ChatColors.Default} - {item.DisplayName}");
            foreach (CCSPlayerController player in Utilities.GetPlayers().Where(player => player.IsValid && !player.IsBot && !player.IsHLTV))
            {
                var menu = new ChatMenu("BETHECHAMP - Vote for the next map");
                foreach (RaitoMapPoolItem item in candidates)
                {
                    RaitoMapPoolItem choice = item;
                    menu.AddMenuOption(choice.DisplayName, (voter, _) => CastRaitoMapVote(voter, choice));
                }
                MenuManager.OpenChatMenu(player, menu);
            }
            AddTimer(raitoConfig.MapVoteDurationSeconds, FinalizeRaitoPublicMapVote, TimerFlags.STOP_ON_MAPCHANGE);
            return true;
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Could not start public map vote: {ex.Message}");
            raitoPublicMapVote = null;
            return false;
        }
    }

    private void FinalizeRaitoPublicMapVote()
    {
        RaitoPublicMapVote? vote = raitoPublicMapVote;
        if (vote is null || vote.Finalized) return;
        vote.Finalized = true;
        Dictionary<string, int> totals = vote.Candidates.ToDictionary(item => item.MapName, item => vote.Ballots.Values.Count(map => map.Equals(item.MapName, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        int highest = totals.Values.DefaultIfEmpty(0).Max();
        List<string> winners = highest == 0
            ? vote.Candidates.Select(item => item.MapName).ToList()
            : vote.Ballots.Values.GroupBy(map => map, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() == highest).Select(group => group.Key).ToList();
        string winner = winners[Random.Shared.Next(winners.Count)];
        try { raitoDatabase?.CompleteMapVote(vote.Id, raitoConfig.ServerId, winner, totals); }
        catch (Exception ex) { Log($"[BETHECHAMP] Could not finalize map vote in database: {ex.Message}"); }
        Server.PrintToChatAll($" {ChatColors.Green}[BETHECHAMP]{ChatColors.Default} Next map: {ChatColors.Green}{winner}{ChatColors.Default} ({highest} votes).");
        WriteRaitoAdminLog(null, "map_vote.completed", reason: $"Map vote {vote.Id} selected {winner}.", metadata: new { voteId = vote.Id, winner, votes = highest });
        EndSeries(vote.WinnerName, 1, vote.Team1Score, vote.Team2Score);
        ChangeMap(winner, 3.0f);
        raitoPublicMapVote = null;
    }

    private void CastRaitoMapVote(CCSPlayerController player, RaitoMapPoolItem selected)
    {
        RaitoPublicMapVote? vote = raitoPublicMapVote;
        if (!IsPlayerValid(player) || vote is null || vote.Finalized || vote.EndsAt <= DateTime.UtcNow) return;
        vote.Ballots[player.SteamID] = selected.MapName;
        try { raitoDatabase?.UpsertMapVoteBallot(vote.Id, player.SteamID, player.PlayerName, selected.MapName); }
        catch (Exception ex) { Log($"[BETHECHAMP] Could not persist menu ballot: {ex.Message}"); }
        ReplyToUserCommand(player, $"Your vote is now {selected.DisplayName}. You may change it until voting closes.");
    }

    private string GetRaitoMatchState() => isRaitoTestMatch ? "test" : isPractice ? "practice" : isMatchLive ? "live" : isKnifeRound ? "knife" : isWarmup ? "warmup" : "waiting";
    private static bool IsRaitoPrivilegedRole(string role) => role is "VIP" or "ADMIN" or "HEAD_ADMIN" or "MANAGER" or "OWNER";
}
