using System.Threading.Channels;
using System.Security.Cryptography;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace MatchZy;

public partial class MatchZy
{
    private RaitoRankWriter? raitoRankWriter;
    private int raitoCurrentRoundNumber;

    private void InitializeRaitoRanking()
    {
        if (raitoDatabase is null)
        {
            Log("[BETHECHAMP RANK] Ranking is disabled because the shared database is unavailable.");
            return;
        }

        raitoRankWriter = new RaitoRankWriter(raitoDatabase, message => Log($"[BETHECHAMP RANK] {message}"));
        RegisterEventHandler<EventRoundStart>(OnRaitoRankRoundStart, HookMode.Post);
        RegisterEventHandler<EventPlayerDeath>(OnRaitoRankPlayerDeath, HookMode.Post);
        RegisterEventHandler<EventRoundEnd>(OnRaitoRankRoundEnd, HookMode.Post);
        RegisterEventHandler<EventRoundMvp>(OnRaitoRankRoundMvp, HookMode.Post);
        Log("[BETHECHAMP RANK] Transactional XP ledger enabled.");
    }

    private void DisposeRaitoRanking()
    {
        raitoRankWriter?.Dispose();
        raitoRankWriter = null;
    }

    private HookResult OnRaitoRankRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (IsRaitoRankedMatch())
        {
            raitoCurrentRoundNumber = GetRaitoRoundNumber(roundInProgress: true);
        }

        return HookResult.Continue;
    }

    private HookResult OnRaitoRankPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!IsRaitoRankedMatch()) return HookResult.Continue;

        CCSPlayerController? victim = @event.Userid;
        if (!IsRaitoRankPlayer(victim)) return HookResult.Continue;
        CCSPlayerController rankedVictim = victim!;

        int roundNumber = GetRaitoTrackedRoundNumber();
        int sourceTick = Server.TickCount;
        object eventMetadata = new
        {
            weapon = @event.Weapon,
            headshot = @event.Headshot,
            assistedFlash = @event.Assistedflash,
            distance = @event.Distance
        };
        var events = new List<RaitoXpEvent>
        {
            CreateRaitoXpEvent(
                RaitoXpEventType.Death,
                rankedVictim,
                roundNumber,
                sourceTick,
                xpDelta: -5,
                deaths: 1,
                counterparty: IsRaitoRankPlayer(@event.Attacker) ? @event.Attacker : null,
                metadata: eventMetadata)
        };

        CCSPlayerController? attacker = @event.Attacker;
        bool enemyKill = IsRaitoRankPlayer(attacker)
            && attacker!.SteamID != rankedVictim.SteamID
            && attacker.TeamNum != rankedVictim.TeamNum;
        if (enemyKill)
        {
            events.Add(CreateRaitoXpEvent(
                RaitoXpEventType.Kill,
                attacker!,
                roundNumber,
                sourceTick,
                xpDelta: 5,
                kills: 1,
                counterparty: rankedVictim,
                metadata: eventMetadata));
        }

        CCSPlayerController? assister = @event.Assister;
        bool enemyAssist = IsRaitoRankPlayer(assister)
            && assister!.SteamID != rankedVictim.SteamID
            && assister.TeamNum != rankedVictim.TeamNum
            && (!enemyKill || assister.SteamID != attacker!.SteamID);
        if (enemyAssist)
        {
            events.Add(CreateRaitoXpEvent(
                RaitoXpEventType.Assist,
                assister!,
                roundNumber,
                sourceTick,
                xpDelta: 1,
                assists: 1,
                counterparty: rankedVictim,
                metadata: eventMetadata));
        }

        QueueRaitoXpEvents(events);
        return HookResult.Continue;
    }

    private HookResult OnRaitoRankRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (!IsRaitoRankedMatch() || @event.Winner is not ((int)CsTeam.Terrorist) and not ((int)CsTeam.CounterTerrorist))
        {
            return HookResult.Continue;
        }

        int roundNumber = GetRaitoTrackedRoundNumber();
        int sourceTick = Server.TickCount;
        List<RaitoXpEvent> events = Utilities.GetPlayers()
            .Where(IsRaitoRankPlayer)
            .Select(player => player.TeamNum == @event.Winner
                ? CreateRaitoXpEvent(
                    RaitoXpEventType.RoundWin,
                    player,
                    roundNumber,
                    sourceTick,
                    xpDelta: 2,
                    roundWins: 1,
                    metadata: new { winner = @event.Winner, reason = @event.Reason })
                : CreateRaitoXpEvent(
                    RaitoXpEventType.RoundLoss,
                    player,
                    roundNumber,
                    sourceTick,
                    xpDelta: -2,
                    roundLosses: 1,
                    metadata: new { winner = @event.Winner, reason = @event.Reason }))
            .ToList();

        QueueRaitoXpEvents(events);
        return HookResult.Continue;
    }

    private HookResult OnRaitoRankRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        if (!IsRaitoRankedMatch() || !IsRaitoRankPlayer(@event.Userid)) return HookResult.Continue;

        CCSPlayerController player = @event.Userid!;
        QueueRaitoXpEvents([
            CreateRaitoXpEvent(
                RaitoXpEventType.Mvp,
                player,
                GetRaitoTrackedRoundNumber(),
                Server.TickCount,
                xpDelta: 3,
                mvps: 1)
        ]);
        return HookResult.Continue;
    }

    private RaitoXpEvent CreateRaitoXpEvent(
        RaitoXpEventType type,
        CCSPlayerController player,
        int roundNumber,
        int sourceTick,
        int xpDelta,
        int kills = 0,
        int deaths = 0,
        int assists = 0,
        int roundWins = 0,
        int roundLosses = 0,
        int mvps = 0,
        CCSPlayerController? counterparty = null,
        object? metadata = null)
    {
        string eventType = type.ToString().ToLowerInvariant();
        string sourceIdentity = type is RaitoXpEventType.Kill or RaitoXpEventType.Death or RaitoXpEventType.Assist
            ? $"{sourceTick}:{counterparty?.SteamID ?? 0}"
            : "round";
        string eventIdentity = $"{raitoConfig.ServerId}:{liveMatchId}:{matchConfig.CurrentMapNumber}:{roundNumber}:{eventType}:{sourceIdentity}:{player.SteamID}";
        string idempotencyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventIdentity))).ToLowerInvariant();
        return new RaitoXpEvent(
            idempotencyKey,
            type,
            player.SteamID,
            player.PlayerName,
            GetRaitoLogicalTeamName(player),
            xpDelta,
            kills,
            deaths,
            assists,
            roundWins,
            roundLosses,
            mvps,
            roundNumber,
            sourceTick,
            counterparty?.SteamID,
            counterparty?.PlayerName,
            metadata,
            DateTime.UtcNow);
    }

    private bool IsRaitoRankedMatch() =>
        isMatchLive && IsRaitoRankedSeries();

    private bool IsRaitoRankedSeries() =>
        liveMatchId > 0 && !isRaitoTestMatch && !isPractice && !isDryRun;

    private bool IsRaitoRankPlayer(CCSPlayerController? player) =>
        IsPlayerValid(player)
        && !player!.IsBot
        && !player.IsHLTV
        && player.SteamID > 0
        && player.TeamNum is (int)CsTeam.Terrorist or (int)CsTeam.CounterTerrorist
        && !matchzyTeam1.coach.Contains(player)
        && !matchzyTeam2.coach.Contains(player);

    private int GetRaitoTrackedRoundNumber()
    {
        if (raitoCurrentRoundNumber > 0) return raitoCurrentRoundNumber;

        raitoCurrentRoundNumber = GetRaitoRoundNumber(roundInProgress: true);
        return raitoCurrentRoundNumber;
    }

    private int GetRaitoRoundNumber(bool roundInProgress)
    {
        try
        {
            int completedRounds = GetRoundNumer();
            return Math.Max(1, completedRounds + (roundInProgress ? 1 : 0));
        }
        catch
        {
            return Math.Max(1, raitoCurrentRoundNumber);
        }
    }

    private string GetRaitoLogicalTeamName(CCSPlayerController player)
    {
        if (player.TeamNum == (int)CsTeam.CounterTerrorist && reverseTeamSides.TryGetValue("CT", out Team? ctTeam))
        {
            return ctTeam.teamName;
        }

        if (player.TeamNum == (int)CsTeam.Terrorist && reverseTeamSides.TryGetValue("TERRORIST", out Team? tTeam))
        {
            return tTeam.teamName;
        }

        return string.Empty;
    }

    private RaitoRankedMatchContext GetRaitoRankedMatchContext() => new(
        liveMatchId,
        raitoConfig.ServerId,
        matchzyTeam1.teamName,
        matchzyTeam2.teamName,
        matchConfig.CurrentMapNumber);

    private void BeginRaitoRankedMatchTracking()
    {
        if (!IsRaitoRankedSeries() || raitoRankWriter is null) return;

        raitoCurrentRoundNumber = 0;
        if (!raitoRankWriter.TryStartMatch(GetRaitoRankedMatchContext()))
        {
            Log("[BETHECHAMP RANK] Failed to queue ranked match start.");
        }
    }

    private void CompleteRaitoRankedMatchTracking(string? winnerName, int team1Score, int team2Score, int mapsPlayed)
    {
        if (!IsRaitoRankedSeries() || raitoRankWriter is null) return;

        var result = new RaitoRankedMatchResult(
            GetRaitoRankedMatchContext(),
            team1Score,
            team2Score,
            winnerName,
            mapsPlayed);
        if (!raitoRankWriter.TryFinalizeMatch(result))
        {
            Log("[BETHECHAMP RANK] Failed to queue ranked match finalization.");
        }
    }

    private void AbortRaitoRankedMatchTracking()
    {
        if (!IsRaitoRankedMatch() || raitoRankWriter is null) return;

        var result = new RaitoRankedMatchResult(
            GetRaitoRankedMatchContext(),
            matchzyTeam1.seriesScore,
            matchzyTeam2.seriesScore,
            null,
            matchConfig.CurrentMapNumber + 1,
            Aborted: true);
        if (!raitoRankWriter.TryFinalizeMatch(result))
        {
            Log("[BETHECHAMP RANK] Failed to queue ranked match abort.");
        }
    }

    private void QueueRaitoXpEvents(IReadOnlyCollection<RaitoXpEvent> events)
    {
        if (events.Count == 0 || raitoRankWriter is null) return;

        if (!raitoRankWriter.TryWriteEvents(GetRaitoRankedMatchContext(), events))
        {
            Log($"[BETHECHAMP RANK] Failed to queue {events.Count} XP event(s).");
        }
    }
}

internal abstract record RaitoRankWriteOperation;
internal sealed record RaitoRankStartOperation(RaitoRankedMatchContext Context) : RaitoRankWriteOperation;
internal sealed record RaitoXpEventsOperation(RaitoRankedMatchContext Context, IReadOnlyCollection<RaitoXpEvent> Events) : RaitoRankWriteOperation;
internal sealed record RaitoRankFinalizeOperation(RaitoRankedMatchResult Result) : RaitoRankWriteOperation;

internal sealed class RaitoRankWriter : IDisposable
{
    private readonly RaitoDatabase database;
    private readonly Action<string> log;
    private readonly Channel<RaitoRankWriteOperation> queue;
    private readonly Task worker;
    private int disposed;

    public RaitoRankWriter(RaitoDatabase database, Action<string> log)
    {
        this.database = database;
        this.log = log;
        queue = Channel.CreateUnbounded<RaitoRankWriteOperation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        worker = Task.Run(ProcessQueue);
    }

    public bool TryStartMatch(RaitoRankedMatchContext context) =>
        TryWrite(new RaitoRankStartOperation(context));

    public bool TryWriteEvents(RaitoRankedMatchContext context, IReadOnlyCollection<RaitoXpEvent> events) =>
        TryWrite(new RaitoXpEventsOperation(context, events.ToArray()));

    public bool TryFinalizeMatch(RaitoRankedMatchResult result) =>
        TryWrite(new RaitoRankFinalizeOperation(result));

    private bool TryWrite(RaitoRankWriteOperation operation) =>
        Volatile.Read(ref disposed) == 0 && queue.Writer.TryWrite(operation);

    private async Task ProcessQueue()
    {
        await foreach (RaitoRankWriteOperation operation in queue.Reader.ReadAllAsync())
        {
            try
            {
                await ApplyWithRetry(operation);
            }
            catch (Exception ex)
            {
                log($"Database operation failed after three attempts ({Describe(operation)}): {ex.Message}");
            }
        }
    }

    private async Task ApplyWithRetry(RaitoRankWriteOperation operation)
    {
        int[] retryDelaysMs = [250, 1000];
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Apply(operation);
                return;
            }
            catch when (attempt < retryDelaysMs.Length)
            {
                await Task.Delay(retryDelaysMs[attempt]);
            }
        }
    }

    private void Apply(RaitoRankWriteOperation operation)
    {
        switch (operation)
        {
            case RaitoRankStartOperation start:
                database.StartRankedMatch(start.Context);
                break;
            case RaitoXpEventsOperation events:
                database.ApplyXpEvents(events.Context, events.Events);
                break;
            case RaitoRankFinalizeOperation finalize:
                database.FinalizeRankedMatch(finalize.Result);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported rank write operation.");
        }
    }

    private static string Describe(RaitoRankWriteOperation operation) => operation switch
    {
        RaitoRankStartOperation => "match start",
        RaitoXpEventsOperation events => $"{events.Events.Count} XP event(s)",
        RaitoRankFinalizeOperation finalize => finalize.Result.Aborted ? "match abort" : "match finalization",
        _ => "unknown"
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        queue.Writer.TryComplete();
        try
        {
            worker.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log($"Writer shutdown failed: {ex.Message}");
        }
    }
}
