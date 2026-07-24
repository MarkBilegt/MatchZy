using System.Text.Json;
using MySqlConnector;

namespace MatchZy;

public sealed class RaitoModerationRecord
{
    public required ulong SteamId64 { get; init; }
    public required string PlayerName { get; init; }
    public required string Reason { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }

    public bool IsActive => ExpiresAtUtc is null || ExpiresAtUtc > DateTime.UtcNow;
}

public sealed class RaitoSkinRecord
{
    public required string Side { get; init; }
    public required string ItemType { get; init; }
    public required string WeaponClass { get; init; }
    public required int PaintKitId { get; init; }
    public int? ItemDefIndex { get; init; }
    public required float Wear { get; init; }
    public required int Seed { get; init; }
    public string NameTag { get; init; } = string.Empty;
    public IReadOnlyList<int?> Stickers { get; init; } = [];
    public string DisplayName { get; init; } = string.Empty;
    public bool LegacyModel { get; init; }
    public string Rarity { get; init; } = string.Empty;
    public string RarityColor { get; init; } = string.Empty;

    public static string BuildKey(string side, string itemType, string weaponClass) =>
        $"{side.Trim().ToUpperInvariant()}|{itemType.Trim().ToUpperInvariant()}|{weaponClass.Trim().ToLowerInvariant()}";
}

public enum RaitoXpEventType
{
    Kill,
    Death,
    Assist,
    RoundWin,
    RoundLoss,
    Mvp
}

public sealed record RaitoRankedMatchContext(
    long MatchId,
    string ServerId,
    string Team1Name,
    string Team2Name,
    int MapNumber);

public sealed record RaitoXpEvent(
    string IdempotencyKey,
    RaitoXpEventType Type,
    ulong SteamId64,
    string DisplayName,
    string TeamName,
    int XpDelta = 0,
    int Kills = 0,
    int Deaths = 0,
    int Assists = 0,
    int RoundWins = 0,
    int RoundLosses = 0,
    int Mvps = 0,
    int RoundNumber = 0,
    int? SourceTick = null,
    ulong? CounterpartySteamId64 = null,
    string? CounterpartyName = null,
    object? Metadata = null,
    DateTime? OccurredAtUtc = null);

public sealed record RaitoAppliedXpEvent(
    RaitoXpEvent Event,
    int XpBefore,
    int XpAfter,
    int AppliedDelta);

public sealed record RaitoRankedMatchResult(
    RaitoRankedMatchContext Context,
    int Team1Score,
    int Team2Score,
    string? WinnerName,
    int MapsPlayed,
    bool Aborted = false);

public sealed record RaitoGameCommand(
    string Id,
    string Type,
    ulong TargetSteamId64,
    string TargetName,
    string Reason);

public sealed record RaitoMapPoolItem(string MapName, string DisplayName);
public sealed record RaitoAdminCallUpdate(string Id, ulong CallerSteamId64, string Status);

public sealed class RaitoDatabase : IDisposable
{
    private readonly string _connectionString;
    private bool _disposed;

    public RaitoDatabase(string configPath)
    {
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<DatabaseConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("BETHECHAMP database configuration is empty.");

        if (!string.Equals(config.DatabaseType, "MySQL", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("BETHECHAMP shared database requires DatabaseType=MySQL in database.json.");
        }

        if (string.IsNullOrWhiteSpace(config.MySqlHost) ||
            string.IsNullOrWhiteSpace(config.MySqlDatabase) ||
            string.IsNullOrWhiteSpace(config.MySqlUsername) ||
            config.MySqlPort is null)
        {
            throw new InvalidOperationException("MySQL database.json is missing host, database, username, or port.");
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = config.MySqlHost,
            Port = (uint)config.MySqlPort.Value,
            Database = config.MySqlDatabase,
            UserID = config.MySqlUsername,
            Password = config.MySqlPassword ?? string.Empty,
            Pooling = true,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 5,
            AllowUserVariables = false
        };

        _connectionString = builder.ConnectionString;
        using var connection = OpenConnection();
    }

    public bool TryGetUserRole(ulong steamId64, out string role, out string? userId)
    {
        role = string.Empty;
        userId = null;

        using var connection = OpenConnection();
        using var command = new MySqlCommand(
            """
            SELECT `id`,
                   CASE WHEN `roleExpiresAt` IS NOT NULL AND `roleExpiresAt` <= UTC_TIMESTAMP(3)
                        THEN 'USER' ELSE `role` END AS `activeRole`
            FROM `User` WHERE `steamId64` = @steamId64 LIMIT 1
            """,
            connection);
        command.Parameters.AddWithValue("@steamId64", steamId64.ToString());

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;

        userId = reader.IsDBNull(0) ? null : reader.GetString(0);
        role = reader.IsDBNull(1) ? "USER" : reader.GetString(1).ToUpperInvariant();
        return true;
    }

    public DateTime GetRoleCacheInvalidationTimestamp()
    {
        using var connection = OpenConnection();
        using var command = new MySqlCommand("SELECT `updatedAt` FROM `RoleCacheState` WHERE `id` = 1", connection);
        object? value = command.ExecuteScalar();
        return value is DateTime timestamp ? timestamp : DateTime.MinValue;
    }

    public string CreateAdminCall(ulong steamId64, string callerName, string serverId, string mapName, string matchState, string reason)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var check = new MySqlCommand("""
            SELECT `createdAt` FROM `AdminCall`
            WHERE `callerSteamId64` = @steamId64 AND `serverId` = @serverId
              AND `status` IN ('OPEN','CLAIMED')
            ORDER BY `createdAt` DESC LIMIT 1 FOR UPDATE
            """, connection, transaction))
        {
            check.Parameters.AddWithValue("@steamId64", steamId64.ToString());
            check.Parameters.AddWithValue("@serverId", serverId);
            object? existing = check.ExecuteScalar();
            if (existing is not null) throw new InvalidOperationException("You already have an open admin call on this server.");
        }
        using (var cooldown = new MySqlCommand("""
            SELECT `createdAt` FROM `AdminCall`
            WHERE `callerSteamId64` = @steamId64 AND `serverId` = @serverId
            ORDER BY `createdAt` DESC LIMIT 1
            """, connection, transaction))
        {
            cooldown.Parameters.AddWithValue("@steamId64", steamId64.ToString());
            cooldown.Parameters.AddWithValue("@serverId", serverId);
            object? value = cooldown.ExecuteScalar();
            if (value is DateTime createdAt && createdAt > DateTime.UtcNow.AddMinutes(-5))
                throw new InvalidOperationException("Please wait five minutes before creating another admin call.");
        }

        string id = Guid.NewGuid().ToString("N");
        DateTime now = DateTime.UtcNow;
        using var insert = new MySqlCommand("""
            INSERT INTO `AdminCall`
              (`id`,`serverId`,`callerSteamId64`,`callerName`,`reason`,`mapName`,`matchState`,`status`,`createdAt`,`expiresAt`)
            VALUES
              (@id,@serverId,@steamId64,@callerName,@reason,@mapName,@matchState,'OPEN',@now,@expiresAt)
            """, connection, transaction);
        insert.Parameters.AddWithValue("@id", id);
        insert.Parameters.AddWithValue("@serverId", serverId);
        insert.Parameters.AddWithValue("@steamId64", steamId64.ToString());
        insert.Parameters.AddWithValue("@callerName", callerName);
        insert.Parameters.AddWithValue("@reason", reason);
        insert.Parameters.AddWithValue("@mapName", mapName);
        insert.Parameters.AddWithValue("@matchState", matchState);
        insert.Parameters.AddWithValue("@now", now);
        insert.Parameters.AddWithValue("@expiresAt", now.AddMinutes(30));
        insert.ExecuteNonQuery();
        transaction.Commit();
        return id;
    }

    public void ExpireAdminCallsForMapChange(string serverId, string currentMap)
    {
        using var connection = OpenConnection();
        using var command = new MySqlCommand("""
            UPDATE `AdminCall` SET `status` = 'EXPIRED', `resolvedAt` = UTC_TIMESTAMP(3)
            WHERE `serverId` = @serverId AND `status` IN ('OPEN','CLAIMED')
              AND (`expiresAt` <= UTC_TIMESTAMP(3) OR (`mapName` IS NOT NULL AND `mapName` <> @currentMap))
            """, connection);
        command.Parameters.AddWithValue("@serverId", serverId);
        command.Parameters.AddWithValue("@currentMap", currentMap);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<RaitoAdminCallUpdate> GetRecentAdminCallUpdates(string serverId)
    {
        var result = new List<RaitoAdminCallUpdate>();
        using var connection = OpenConnection();
        using var command = new MySqlCommand("""
            SELECT `id`,`callerSteamId64`,`status` FROM `AdminCall`
            WHERE `serverId` = @serverId AND `createdAt` >= DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 HOUR)
            """, connection);
        command.Parameters.AddWithValue("@serverId", serverId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (ulong.TryParse(reader.GetString(1), out ulong steamId64)) result.Add(new(reader.GetString(0), steamId64, reader.GetString(2)));
        }
        return result;
    }

    public IReadOnlyList<RaitoMapPoolItem> GetEnabledMapPool()
    {
        var result = new List<RaitoMapPoolItem>();
        using var connection = OpenConnection();
        using var command = new MySqlCommand("SELECT `mapName`,`displayName` FROM `MapPoolEntry` WHERE `isEnabled` = 1 ORDER BY `displayOrder`,`mapName`", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public IReadOnlyList<string> GetRecentMaps(string serverId, int count)
    {
        var result = new List<string>();
        using var connection = OpenConnection();
        using var command = new MySqlCommand("SELECT `mapName` FROM `MapRotationHistory` WHERE `serverId` = @serverId ORDER BY `playedAt` DESC LIMIT @count", connection);
        command.Parameters.AddWithValue("@serverId", serverId);
        command.Parameters.AddWithValue("@count", count);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public string CreateMapVote(string serverId, string currentMap, IReadOnlyList<string> candidates, DateTime endsAt)
    {
        string id = Guid.NewGuid().ToString("N");
        using var connection = OpenConnection();
        using var command = new MySqlCommand("""
            INSERT INTO `MapVote` (`id`,`serverId`,`currentMap`,`candidates`,`status`,`startedAt`,`endsAt`)
            VALUES (@id,@serverId,@currentMap,@candidates,'OPEN',UTC_TIMESTAMP(3),@endsAt)
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@serverId", serverId);
        command.Parameters.AddWithValue("@currentMap", currentMap);
        command.Parameters.AddWithValue("@candidates", JsonSerializer.Serialize(candidates));
        command.Parameters.AddWithValue("@endsAt", endsAt);
        command.ExecuteNonQuery();
        return id;
    }

    public void UpsertMapVoteBallot(string voteId, ulong steamId64, string playerName, string mapName)
    {
        using var connection = OpenConnection();
        using var command = new MySqlCommand("""
            INSERT INTO `MapVoteBallot` (`id`,`mapVoteId`,`steamId64`,`playerName`,`mapName`,`createdAt`,`updatedAt`)
            VALUES (@id,@voteId,@steamId64,@playerName,@mapName,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            ON DUPLICATE KEY UPDATE `playerName`=VALUES(`playerName`),`mapName`=VALUES(`mapName`),`updatedAt`=VALUES(`updatedAt`)
            """, connection);
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@voteId", voteId);
        command.Parameters.AddWithValue("@steamId64", steamId64.ToString());
        command.Parameters.AddWithValue("@playerName", playerName);
        command.Parameters.AddWithValue("@mapName", mapName);
        command.ExecuteNonQuery();
    }

    public void CompleteMapVote(string voteId, string serverId, string winnerMap, IReadOnlyDictionary<string, int> totals)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var update = new MySqlCommand("UPDATE `MapVote` SET `status`='COMPLETED',`winnerMap`=@winner,`totals`=@totals,`completedAt`=UTC_TIMESTAMP(3) WHERE `id`=@id AND `status`='OPEN'", connection, transaction))
        {
            update.Parameters.AddWithValue("@winner", winnerMap);
            update.Parameters.AddWithValue("@id", voteId);
            update.Parameters.AddWithValue("@totals", JsonSerializer.Serialize(totals));
            if (update.ExecuteNonQuery() != 1) { transaction.Rollback(); return; }
        }
        using (var history = new MySqlCommand("INSERT INTO `MapRotationHistory` (`id`,`serverId`,`mapName`,`playedAt`) VALUES (@id,@serverId,@mapName,UTC_TIMESTAMP(3))", connection, transaction))
        {
            history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            history.Parameters.AddWithValue("@serverId", serverId);
            using var currentMap = new MySqlCommand("SELECT `currentMap` FROM `MapVote` WHERE `id`=@id", connection, transaction);
            currentMap.Parameters.AddWithValue("@id", voteId);
            history.Parameters.AddWithValue("@mapName", Convert.ToString(currentMap.ExecuteScalar()) ?? winnerMap);
            history.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void StartRankedMatch(RaitoRankedMatchContext context)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            EnsureRankedMatch(connection, transaction, context, DateTime.UtcNow);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public IReadOnlyList<RaitoAppliedXpEvent> ApplyXpEvents(RaitoRankedMatchContext context, IEnumerable<RaitoXpEvent> events)
    {
        List<RaitoXpEvent> pendingEvents = events.ToList();

        if (pendingEvents.Count == 0) return [];

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var appliedEvents = new List<RaitoAppliedXpEvent>(pendingEvents.Count);
        try
        {
            EnsureRankedMatch(connection, transaction, context, DateTime.UtcNow);

            foreach (RaitoXpEvent xpEvent in pendingEvents)
            {
                DateTime occurredAt = xpEvent.OccurredAtUtc ?? DateTime.UtcNow;
                using (var ensurePlayer = new MySqlCommand("""
                    INSERT INTO `PlayerProgress`
                        (`id`, `steamId64`, `displayName`, `xp`, `kills`, `deaths`, `assists`, `roundWins`, `roundLosses`, `mvps`, `createdAt`, `updatedAt`)
                    VALUES
                        (@id, @steamId64, @displayName, 0, 0, 0, 0, 0, 0, 0, @now, @now)
                    ON DUPLICATE KEY UPDATE `displayName` = VALUES(`displayName`)
                    """, connection, transaction))
                {
                    ensurePlayer.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    ensurePlayer.Parameters.AddWithValue("@steamId64", xpEvent.SteamId64.ToString());
                    ensurePlayer.Parameters.AddWithValue("@displayName", xpEvent.DisplayName);
                    ensurePlayer.Parameters.AddWithValue("@now", occurredAt);
                    ensurePlayer.ExecuteNonQuery();
                }

                RaitoProgressSnapshot progress = LockPlayerProgress(connection, transaction, xpEvent.SteamId64);
                int xpAfter = (int)Math.Clamp((long)progress.Xp + xpEvent.XpDelta, 0L, int.MaxValue);
                int appliedDelta = xpAfter - progress.Xp;
                string eventId = Guid.NewGuid().ToString("N");

                using (var insertEvent = new MySqlCommand("""
                    INSERT INTO `XpEvent`
                        (`id`, `idempotencyKey`, `steamId64`, `displayName`, `type`, `requestedDelta`, `appliedDelta`,
                         `xpBefore`, `xpAfter`, `killsDelta`, `deathsDelta`, `assistsDelta`, `roundWinsDelta`,
                         `roundLossesDelta`, `mvpsDelta`, `matchId`, `mapNumber`, `roundNumber`, `sourceTick`, `serverId`,
                         `counterpartySteamId64`, `counterpartyName`, `metadata`, `occurredAt`, `createdAt`)
                    VALUES
                        (@id, @idempotencyKey, @steamId64, @displayName, @type, @requestedDelta, @appliedDelta,
                         @xpBefore, @xpAfter, @killsDelta, @deathsDelta, @assistsDelta, @roundWinsDelta,
                         @roundLossesDelta, @mvpsDelta, @matchId, @mapNumber, @roundNumber, @sourceTick, @serverId,
                         @counterpartySteamId64, @counterpartyName, @metadata, @occurredAt, @createdAt)
                    """, connection, transaction))
                {
                    insertEvent.Parameters.AddWithValue("@id", eventId);
                    insertEvent.Parameters.AddWithValue("@idempotencyKey", xpEvent.IdempotencyKey);
                    insertEvent.Parameters.AddWithValue("@steamId64", xpEvent.SteamId64.ToString());
                    insertEvent.Parameters.AddWithValue("@displayName", xpEvent.DisplayName);
                    insertEvent.Parameters.AddWithValue("@type", ToDatabaseEventType(xpEvent.Type));
                    insertEvent.Parameters.AddWithValue("@requestedDelta", xpEvent.XpDelta);
                    insertEvent.Parameters.AddWithValue("@appliedDelta", appliedDelta);
                    insertEvent.Parameters.AddWithValue("@xpBefore", progress.Xp);
                    insertEvent.Parameters.AddWithValue("@xpAfter", xpAfter);
                    insertEvent.Parameters.AddWithValue("@killsDelta", xpEvent.Kills);
                    insertEvent.Parameters.AddWithValue("@deathsDelta", xpEvent.Deaths);
                    insertEvent.Parameters.AddWithValue("@assistsDelta", xpEvent.Assists);
                    insertEvent.Parameters.AddWithValue("@roundWinsDelta", xpEvent.RoundWins);
                    insertEvent.Parameters.AddWithValue("@roundLossesDelta", xpEvent.RoundLosses);
                    insertEvent.Parameters.AddWithValue("@mvpsDelta", xpEvent.Mvps);
                    insertEvent.Parameters.AddWithValue("@matchId", context.MatchId.ToString());
                    insertEvent.Parameters.AddWithValue("@mapNumber", context.MapNumber);
                    insertEvent.Parameters.AddWithValue("@roundNumber", xpEvent.RoundNumber);
                    insertEvent.Parameters.AddWithValue("@sourceTick", xpEvent.SourceTick ?? (object)DBNull.Value);
                    insertEvent.Parameters.AddWithValue("@serverId", context.ServerId);
                    insertEvent.Parameters.AddWithValue("@counterpartySteamId64", xpEvent.CounterpartySteamId64?.ToString() ?? (object)DBNull.Value);
                    insertEvent.Parameters.AddWithValue("@counterpartyName", xpEvent.CounterpartyName ?? (object)DBNull.Value);
                    insertEvent.Parameters.AddWithValue("@metadata", xpEvent.Metadata is null ? DBNull.Value : JsonSerializer.Serialize(xpEvent.Metadata));
                    insertEvent.Parameters.AddWithValue("@occurredAt", occurredAt);
                    insertEvent.Parameters.AddWithValue("@createdAt", DateTime.UtcNow);

                    try
                    {
                        insertEvent.ExecuteNonQuery();
                    }
                    catch (MySqlException ex) when (ex.Number == 1062)
                    {
                        continue;
                    }
                }

                using (var updateProgress = new MySqlCommand("""
                    UPDATE `PlayerProgress`
                    SET `displayName` = @displayName,
                        `xp` = @xpAfter,
                        `kills` = GREATEST(0, `kills` + @kills),
                        `deaths` = GREATEST(0, `deaths` + @deaths),
                        `assists` = GREATEST(0, `assists` + @assists),
                        `roundWins` = GREATEST(0, `roundWins` + @roundWins),
                        `roundLosses` = GREATEST(0, `roundLosses` + @roundLosses),
                        `mvps` = GREATEST(0, `mvps` + @mvps),
                        `updatedAt` = @now
                    WHERE `steamId64` = @steamId64
                    """, connection, transaction))
                {
                    updateProgress.Parameters.AddWithValue("@displayName", xpEvent.DisplayName);
                    updateProgress.Parameters.AddWithValue("@xpAfter", xpAfter);
                    updateProgress.Parameters.AddWithValue("@kills", xpEvent.Kills);
                    updateProgress.Parameters.AddWithValue("@deaths", xpEvent.Deaths);
                    updateProgress.Parameters.AddWithValue("@assists", xpEvent.Assists);
                    updateProgress.Parameters.AddWithValue("@roundWins", xpEvent.RoundWins);
                    updateProgress.Parameters.AddWithValue("@roundLosses", xpEvent.RoundLosses);
                    updateProgress.Parameters.AddWithValue("@mvps", xpEvent.Mvps);
                    updateProgress.Parameters.AddWithValue("@now", occurredAt);
                    updateProgress.Parameters.AddWithValue("@steamId64", xpEvent.SteamId64.ToString());
                    updateProgress.ExecuteNonQuery();
                }

                using var updateMatchPlayer = new MySqlCommand("""
                    INSERT INTO `RankedMatchPlayer`
                        (`id`, `matchId`, `steamId64`, `displayName`, `teamName`, `xpBefore`, `xpDelta`, `xpAfter`,
                         `kills`, `deaths`, `assists`, `roundWins`, `roundLosses`, `mvps`, `createdAt`, `updatedAt`)
                    VALUES
                        (@id, @matchId, @steamId64, @displayName, @teamName, @xpBefore, @xpDelta, @xpAfter,
                         @kills, @deaths, @assists, @roundWins, @roundLosses, @mvps, @now, @now)
                    ON DUPLICATE KEY UPDATE
                        `displayName` = VALUES(`displayName`),
                        `teamName` = VALUES(`teamName`),
                        `xpDelta` = `xpDelta` + VALUES(`xpDelta`),
                        `xpAfter` = VALUES(`xpAfter`),
                        `kills` = `kills` + VALUES(`kills`),
                        `deaths` = `deaths` + VALUES(`deaths`),
                        `assists` = `assists` + VALUES(`assists`),
                        `roundWins` = `roundWins` + VALUES(`roundWins`),
                        `roundLosses` = `roundLosses` + VALUES(`roundLosses`),
                        `mvps` = `mvps` + VALUES(`mvps`),
                        `updatedAt` = VALUES(`updatedAt`)
                    """, connection, transaction);
                updateMatchPlayer.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                updateMatchPlayer.Parameters.AddWithValue("@matchId", context.MatchId.ToString());
                updateMatchPlayer.Parameters.AddWithValue("@steamId64", xpEvent.SteamId64.ToString());
                updateMatchPlayer.Parameters.AddWithValue("@displayName", xpEvent.DisplayName);
                updateMatchPlayer.Parameters.AddWithValue("@teamName", xpEvent.TeamName);
                updateMatchPlayer.Parameters.AddWithValue("@xpBefore", progress.Xp);
                updateMatchPlayer.Parameters.AddWithValue("@xpDelta", appliedDelta);
                updateMatchPlayer.Parameters.AddWithValue("@xpAfter", xpAfter);
                updateMatchPlayer.Parameters.AddWithValue("@kills", xpEvent.Kills);
                updateMatchPlayer.Parameters.AddWithValue("@deaths", xpEvent.Deaths);
                updateMatchPlayer.Parameters.AddWithValue("@assists", xpEvent.Assists);
                updateMatchPlayer.Parameters.AddWithValue("@roundWins", xpEvent.RoundWins);
                updateMatchPlayer.Parameters.AddWithValue("@roundLosses", xpEvent.RoundLosses);
                updateMatchPlayer.Parameters.AddWithValue("@mvps", xpEvent.Mvps);
                updateMatchPlayer.Parameters.AddWithValue("@now", occurredAt);
                updateMatchPlayer.ExecuteNonQuery();
                appliedEvents.Add(new RaitoAppliedXpEvent(xpEvent, progress.Xp, xpAfter, appliedDelta));
            }

            transaction.Commit();
            return appliedEvents;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void FinalizeRankedMatch(RaitoRankedMatchResult result)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            DateTime now = DateTime.UtcNow;
            EnsureRankedMatch(connection, transaction, result.Context, now);
            using var command = new MySqlCommand("""
                UPDATE `RankedMatch`
                SET `team1Name` = @team1Name,
                    `team2Name` = @team2Name,
                    `team1Score` = @team1Score,
                    `team2Score` = @team2Score,
                    `winnerName` = @winnerName,
                    `mapsPlayed` = @mapsPlayed,
                    `status` = @status,
                    `completedAt` = @completedAt,
                    `updatedAt` = @completedAt
                WHERE `matchId` = @matchId
                  AND (`status` <> 'COMPLETED' OR @status = 'COMPLETED')
                """, connection, transaction);
            command.Parameters.AddWithValue("@team1Name", result.Context.Team1Name);
            command.Parameters.AddWithValue("@team2Name", result.Context.Team2Name);
            command.Parameters.AddWithValue("@team1Score", result.Team1Score);
            command.Parameters.AddWithValue("@team2Score", result.Team2Score);
            command.Parameters.AddWithValue("@winnerName", result.WinnerName ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@mapsPlayed", Math.Max(1, result.MapsPlayed));
            command.Parameters.AddWithValue("@status", result.Aborted ? "ABORTED" : "COMPLETED");
            command.Parameters.AddWithValue("@completedAt", now);
            command.Parameters.AddWithValue("@matchId", result.Context.MatchId.ToString());
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void EnsureRankedMatch(
        MySqlConnection connection,
        MySqlTransaction transaction,
        RaitoRankedMatchContext context,
        DateTime now)
    {
        using var command = new MySqlCommand("""
            INSERT INTO `RankedMatch`
                (`matchId`, `serverId`, `team1Name`, `team2Name`, `mapsPlayed`, `status`, `startedAt`, `createdAt`, `updatedAt`)
            VALUES
                (@matchId, @serverId, @team1Name, @team2Name, @mapsPlayed, 'LIVE', @now, @now, @now)
            ON DUPLICATE KEY UPDATE
                `serverId` = VALUES(`serverId`),
                `team1Name` = VALUES(`team1Name`),
                `team2Name` = VALUES(`team2Name`),
                `mapsPlayed` = GREATEST(`mapsPlayed`, VALUES(`mapsPlayed`)),
                `updatedAt` = VALUES(`updatedAt`)
            """, connection, transaction);
        command.Parameters.AddWithValue("@matchId", context.MatchId.ToString());
        command.Parameters.AddWithValue("@serverId", context.ServerId);
        command.Parameters.AddWithValue("@team1Name", context.Team1Name);
        command.Parameters.AddWithValue("@team2Name", context.Team2Name);
        command.Parameters.AddWithValue("@mapsPlayed", Math.Max(1, context.MapNumber + 1));
        command.Parameters.AddWithValue("@now", now);
        command.ExecuteNonQuery();
    }

    private static RaitoProgressSnapshot LockPlayerProgress(
        MySqlConnection connection,
        MySqlTransaction transaction,
        ulong steamId64)
    {
        using var command = new MySqlCommand(
            "SELECT `xp` FROM `PlayerProgress` WHERE `steamId64` = @steamId64 FOR UPDATE",
            connection,
            transaction);
        command.Parameters.AddWithValue("@steamId64", steamId64.ToString());
        object? value = command.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            throw new InvalidOperationException($"PlayerProgress row was not created for {steamId64}.");
        }

        return new RaitoProgressSnapshot(Convert.ToInt32(value));
    }

    private static string ToDatabaseEventType(RaitoXpEventType type) => type switch
    {
        RaitoXpEventType.Kill => "KILL",
        RaitoXpEventType.Death => "DEATH",
        RaitoXpEventType.Assist => "ASSIST",
        RaitoXpEventType.RoundWin => "ROUND_WIN",
        RaitoXpEventType.RoundLoss => "ROUND_LOSS",
        RaitoXpEventType.Mvp => "MVP",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported XP event type.")
    };

    private readonly record struct RaitoProgressSnapshot(int Xp);

    public Dictionary<string, RaitoSkinRecord> GetPlayerSkins(ulong steamId64)
    {
        var skins = new Dictionary<string, RaitoSkinRecord>(StringComparer.OrdinalIgnoreCase);

        using var connection = OpenConnection();
        using var command = new MySqlCommand("""
            SELECT
                ps.`side`, ps.`itemType`, ps.`weaponClass`, ps.`paintKitId`, ps.`itemDefIndex`, ps.`wear`, ps.`seed`,
                ps.`nameTag`, ps.`sticker0`, ps.`sticker1`, ps.`sticker2`, ps.`sticker3`, ps.`sticker4`,
                catalog.`name`, catalog.`legacyModel`, catalog.`rarity`, catalog.`rarityColor`
            FROM `PlayerSkin` ps
            LEFT JOIN `SkinCatalogItem` catalog ON catalog.`id` = (
                SELECT candidate.`id`
                FROM `SkinCatalogItem` candidate
                WHERE candidate.`category` = 'SKIN'
                  AND candidate.`isEnabled` = 1
                  AND candidate.`itemType` = ps.`itemType`
                  AND candidate.`paintKitId` = ps.`paintKitId`
                  AND (
                      (ps.`itemType` = 'WEAPON' AND candidate.`weaponClass` = ps.`weaponClass`)
                      OR
                      (ps.`itemType` <> 'WEAPON' AND candidate.`itemDefIndex` = ps.`itemDefIndex`)
                  )
                LIMIT 1
            )
            WHERE ps.`userId` = (SELECT `id` FROM `User` WHERE `steamId64` = @steamId64 LIMIT 1)
            """, connection);
        command.Parameters.AddWithValue("@steamId64", steamId64.ToString());

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string side = reader.IsDBNull(0) ? "ALL" : reader.GetString(0).Trim().ToUpperInvariant();
            string itemType = reader.IsDBNull(1) ? "WEAPON" : reader.GetString(1).Trim().ToUpperInvariant();
            string weaponClass = reader.GetString(2).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(weaponClass)) continue;

            skins[RaitoSkinRecord.BuildKey(side, itemType, weaponClass)] = new RaitoSkinRecord
            {
                Side = side,
                ItemType = itemType,
                WeaponClass = weaponClass,
                PaintKitId = reader.GetInt32(3),
                ItemDefIndex = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Wear = Math.Clamp(reader.GetFloat(5), 0.0001f, 1f),
                Seed = Math.Clamp(reader.GetInt32(6), 0, 1000),
                NameTag = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                Stickers = Enumerable.Range(8, 5).Select(index => reader.IsDBNull(index) ? (int?)null : reader.GetInt32(index)).ToArray(),
                DisplayName = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                LegacyModel = !reader.IsDBNull(14) && reader.GetBoolean(14),
                Rarity = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                RarityColor = reader.IsDBNull(16) ? string.Empty : reader.GetString(16)
            };
        }

        return skins;
    }

    public RaitoModerationRecord? GetActiveModeration(string tableName, ulong steamId64)
    {
        EnsureTableName(tableName);
        using var connection = OpenConnection();
        using var command = new MySqlCommand($"SELECT `steamId64`, `playerName`, `reason`, `expiresAt` FROM `{tableName}` WHERE `steamId64` = @steamId64 LIMIT 1", connection);
        command.Parameters.AddWithValue("@steamId64", steamId64.ToString());

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        DateTime? expiresAt = reader.IsDBNull(3) ? null : DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
        var record = new RaitoModerationRecord
        {
            SteamId64 = ulong.Parse(reader.GetString(0)),
            PlayerName = reader.GetString(1),
            Reason = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            ExpiresAtUtc = expiresAt
        };

        if (!record.IsActive)
        {
            reader.Close();
            ExpireModeration(tableName, steamId64);
            return null;
        }

        return record;
    }

    public void UpsertModeration(
        string tableName,
        string actorColumn,
        ulong steamId64,
        string playerName,
        string reason,
        string actorName,
        ulong? actorSteamId64,
        TimeSpan? duration,
        string? serverId = null)
    {
        EnsureModerationColumns(tableName, actorColumn);
        string actorUserId = GetUserId(actorSteamId64);
        DateTime now = DateTime.UtcNow;
        DateTime? expiresAt = duration is null ? null : now.Add(duration.Value);
        string kind = ModerationKindForTable(tableName);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        CloseActiveModerationCases(connection, transaction, kind, steamId64, "REVOKED", "SUPERSEDED", actorName, actorUserId, actorSteamId64, "Replaced by a newer active punishment.", now);
        string sql = $"""
            INSERT INTO `{tableName}`
                (`id`, `steamId64`, `playerName`, `reason`, `{actorColumn}`, `{actorColumn}UserId`, `{ColumnForDate(actorColumn)}`, `expiresAt`)
            VALUES
                (@id, @steamId64, @playerName, @reason, @actorName, @actorUserId, @createdAt, @expiresAt)
            ON DUPLICATE KEY UPDATE
                `playerName` = VALUES(`playerName`),
                `reason` = VALUES(`reason`),
                `{actorColumn}` = VALUES(`{actorColumn}`),
                `{actorColumn}UserId` = VALUES(`{actorColumn}UserId`),
                `{ColumnForDate(actorColumn)}` = VALUES(`{ColumnForDate(actorColumn)}`),
                `expiresAt` = VALUES(`expiresAt`)
            """;

        using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@steamId64", steamId64.ToString());
        command.Parameters.AddWithValue("@playerName", playerName);
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@actorName", actorName);
        command.Parameters.AddWithValue("@actorUserId", string.IsNullOrWhiteSpace(actorUserId) ? DBNull.Value : actorUserId);
        command.Parameters.AddWithValue("@createdAt", now);
        command.Parameters.AddWithValue("@expiresAt", expiresAt is null ? DBNull.Value : expiresAt.Value);
        command.ExecuteNonQuery();

        string caseId = CreateModerationCase(
            connection,
            transaction,
            kind,
            "ACTIVE",
            steamId64,
            playerName,
            actorName,
            actorUserId,
            actorSteamId64,
            reason,
            duration,
            expiresAt,
            serverId,
            now);
        InsertGameCommand(connection, transaction, CommandTypeForKind(kind), steamId64, playerName, reason, caseId, actorUserId, now);
        transaction.Commit();
    }

    public void DeleteModeration(string tableName, ulong steamId64)
    {
        EnsureTableName(tableName);
        using var connection = OpenConnection();
        using var command = new MySqlCommand($"DELETE FROM `{tableName}` WHERE `steamId64` = @steamId64", connection);
        command.Parameters.AddWithValue("@steamId64", steamId64.ToString());
        command.ExecuteNonQuery();
    }

    public void ResolveModeration(
        string tableName,
        ulong steamId64,
        string actorName,
        ulong? actorSteamId64,
        string reason)
    {
        EnsureTableName(tableName);
        string actorUserId = GetUserId(actorSteamId64);
        DateTime now = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        DeleteModeration(connection, transaction, tableName, steamId64);
        string kind = ModerationKindForTable(tableName);
        IReadOnlyList<string> caseIds = CloseActiveModerationCases(connection, transaction, kind, steamId64, "REVOKED", "REVOKED", actorName, actorUserId, actorSteamId64, reason, now);
        InsertGameCommand(connection, transaction, InverseCommandTypeForKind(kind), steamId64, steamId64.ToString(), reason, caseIds.FirstOrDefault(), actorUserId, now);
        transaction.Commit();
    }

    public void CreateCompletedModerationCase(
        string kind,
        ulong steamId64,
        string playerName,
        string actorName,
        ulong? actorSteamId64,
        string reason,
        string? serverId = null)
    {
        if (kind != "GAME_KICK") throw new ArgumentException("Unsupported completed moderation kind.", nameof(kind));
        string actorUserId = GetUserId(actorSteamId64);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        CreateModerationCase(connection, transaction, kind, "COMPLETED", steamId64, playerName, actorName, actorUserId, actorSteamId64, reason, null, null, serverId, DateTime.UtcNow);
        transaction.Commit();
    }

    public IReadOnlyList<RaitoGameCommand> GetPendingGameCommands(string serverId, int limit = 25)
    {
        var commands = new List<RaitoGameCommand>();
        using var connection = OpenConnection();
        using var command = new MySqlCommand("""
            SELECT command.`id`, command.`type`, command.`targetSteamId64`, command.`targetName`, command.`reason`
            FROM `GameCommand` command
            LEFT JOIN `GameCommandReceipt` receipt
                ON receipt.`commandId` = command.`id` AND receipt.`serverId` = @serverId
            WHERE command.`deliverUntil` > UTC_TIMESTAMP(3)
              AND (command.`serverId` IS NULL OR command.`serverId` = @serverId)
              AND receipt.`id` IS NULL
            ORDER BY command.`createdAt` ASC
            LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("@serverId", serverId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!ulong.TryParse(reader.GetString(2), out ulong steamId64)) continue;
            commands.Add(new RaitoGameCommand(reader.GetString(0), reader.GetString(1), steamId64, reader.GetString(3), reader.GetString(4)));
        }
        return commands;
    }

    public void AddGameCommandReceipt(string commandId, string serverId, string status, string? detail)
    {
        if (status is not ("APPLIED" or "SKIPPED" or "FAILED")) throw new ArgumentException("Unsupported command receipt status.", nameof(status));
        using var connection = OpenConnection();
        using var command = new MySqlCommand("""
            INSERT INTO `GameCommandReceipt` (`id`, `commandId`, `serverId`, `status`, `detail`, `executedAt`)
            VALUES (@id, @commandId, @serverId, @status, @detail, @executedAt)
            ON DUPLICATE KEY UPDATE `status` = VALUES(`status`), `detail` = VALUES(`detail`), `executedAt` = VALUES(`executedAt`)
            """, connection);
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@commandId", commandId);
        command.Parameters.AddWithValue("@serverId", serverId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@detail", detail ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@executedAt", DateTime.UtcNow);
        command.ExecuteNonQuery();
    }

    public void AddAdminLog(
        ulong? actorSteamId64,
        string actorName,
        string action,
        ulong? targetSteamId64 = null,
        string? targetName = null,
        string? reason = null,
        object? metadata = null)
    {
        string actorUserId = GetUserId(actorSteamId64);
        using var connection = OpenConnection();
        using var command = new MySqlCommand("""
            INSERT INTO `AdminLog`
                (`id`, `actorSteamId64`, `actorName`, `actorUserId`, `action`, `targetSteamId64`, `targetName`, `reason`, `metadata`, `createdAt`)
            VALUES
                (@id, @actorSteamId64, @actorName, @actorUserId, @action, @targetSteamId64, @targetName, @reason, @metadata, @createdAt)
            """, connection);

        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@actorSteamId64", actorSteamId64?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@actorName", actorName);
        command.Parameters.AddWithValue("@actorUserId", string.IsNullOrWhiteSpace(actorUserId) ? DBNull.Value : actorUserId);
        command.Parameters.AddWithValue("@action", action);
        command.Parameters.AddWithValue("@targetSteamId64", targetSteamId64?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@targetName", targetName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@reason", reason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@metadata", metadata is null ? DBNull.Value : JsonSerializer.Serialize(metadata));
        command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow);
        command.ExecuteNonQuery();
    }

    public void UpdateServer(
        RaitoServerConfig config,
        int currentPlayers,
        string matchState,
        string currentMap)
    {
        using var connection = OpenConnection();
        using var command = new MySqlCommand("""
            INSERT INTO `GameServer`
                (`id`, `name`, `ipAddress`, `port`, `region`, `isActive`, `currentPlayers`, `maxPlayers`, `matchState`, `currentMap`, `lastHeartbeatAt`)
            VALUES
                (@id, @name, @ipAddress, @port, @region, 1, @currentPlayers, @maxPlayers, @matchState, @currentMap, @lastHeartbeatAt)
            ON DUPLICATE KEY UPDATE
                `name` = VALUES(`name`),
                `ipAddress` = VALUES(`ipAddress`),
                `port` = VALUES(`port`),
                `region` = VALUES(`region`),
                `isActive` = 1,
                `currentPlayers` = VALUES(`currentPlayers`),
                `maxPlayers` = VALUES(`maxPlayers`),
                `matchState` = VALUES(`matchState`),
                `currentMap` = VALUES(`currentMap`),
                `lastHeartbeatAt` = VALUES(`lastHeartbeatAt`)
            """, connection);

        command.Parameters.AddWithValue("@id", config.ServerId);
        command.Parameters.AddWithValue("@name", config.ServerName);
        command.Parameters.AddWithValue("@ipAddress", config.ServerIpAddress);
        command.Parameters.AddWithValue("@port", config.ServerPort);
        command.Parameters.AddWithValue("@region", config.Region);
        command.Parameters.AddWithValue("@currentPlayers", currentPlayers);
        command.Parameters.AddWithValue("@maxPlayers", config.MaxPlayers);
        command.Parameters.AddWithValue("@matchState", matchState);
        command.Parameters.AddWithValue("@currentMap", currentMap);
        command.Parameters.AddWithValue("@lastHeartbeatAt", DateTime.UtcNow);
        command.ExecuteNonQuery();
    }

    private void ExpireModeration(string tableName, ulong steamId64)
    {
        DateTime now = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        DeleteModeration(connection, transaction, tableName, steamId64);
        IReadOnlyList<string> caseIds = CloseActiveModerationCases(
            connection,
            transaction,
            ModerationKindForTable(tableName),
            steamId64,
            "EXPIRED",
            "EXPIRED",
            "BETHECHAMP Bot",
            string.Empty,
            null,
            "Punishment duration elapsed.",
            now);
        string kind = ModerationKindForTable(tableName);
        InsertGameCommand(connection, transaction, InverseCommandTypeForKind(kind), steamId64, steamId64.ToString(), "Punishment duration elapsed.", caseIds.FirstOrDefault(), string.Empty, now);
        transaction.Commit();
    }

    private static void DeleteModeration(MySqlConnection connection, MySqlTransaction transaction, string tableName, ulong steamId64)
    {
        EnsureTableName(tableName);
        using var command = new MySqlCommand($"DELETE FROM `{tableName}` WHERE `steamId64` = @steamId64", connection, transaction);
        command.Parameters.AddWithValue("@steamId64", steamId64.ToString());
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> CloseActiveModerationCases(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string kind,
        ulong steamId64,
        string status,
        string eventType,
        string actorName,
        string actorUserId,
        ulong? actorSteamId64,
        string reason,
        DateTime now)
    {
        var caseIds = new List<string>();
        using (var select = new MySqlCommand("""
            SELECT `id`
            FROM `ModerationCase`
            WHERE `kind` = @kind AND `targetSteamId64` = @steamId64 AND `status` = 'ACTIVE'
            ORDER BY `createdAt` DESC
            FOR UPDATE
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("@kind", kind);
            select.Parameters.AddWithValue("@steamId64", steamId64.ToString());
            using var reader = select.ExecuteReader();
            while (reader.Read()) caseIds.Add(reader.GetString(0));
        }

        foreach (string caseId in caseIds)
        {
            using var update = new MySqlCommand("""
                UPDATE `ModerationCase`
                SET `status` = @status,
                    `revokedAt` = CASE WHEN @status = 'REVOKED' THEN @now ELSE `revokedAt` END,
                    `revokedByUserId` = CASE WHEN @status = 'REVOKED' THEN @actorUserId ELSE `revokedByUserId` END,
                    `updatedAt` = @now
                WHERE `id` = @caseId AND `status` = 'ACTIVE'
                """, connection, transaction);
            update.Parameters.AddWithValue("@status", status);
            update.Parameters.AddWithValue("@now", now);
            update.Parameters.AddWithValue("@actorUserId", string.IsNullOrWhiteSpace(actorUserId) ? DBNull.Value : actorUserId);
            update.Parameters.AddWithValue("@caseId", caseId);
            if (update.ExecuteNonQuery() == 0) continue;

            using var insertEvent = new MySqlCommand("""
                INSERT INTO `ModerationCaseEvent`
                    (`id`, `caseId`, `type`, `actorUserId`, `actorSteamId64`, `actorName`, `reason`, `createdAt`)
                VALUES
                    (@id, @caseId, @type, @actorUserId, @actorSteamId64, @actorName, @reason, @createdAt)
                """, connection, transaction);
            insertEvent.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            insertEvent.Parameters.AddWithValue("@caseId", caseId);
            insertEvent.Parameters.AddWithValue("@type", eventType);
            insertEvent.Parameters.AddWithValue("@actorUserId", string.IsNullOrWhiteSpace(actorUserId) ? DBNull.Value : actorUserId);
            insertEvent.Parameters.AddWithValue("@actorSteamId64", actorSteamId64?.ToString() ?? (object)DBNull.Value);
            insertEvent.Parameters.AddWithValue("@actorName", actorName);
            insertEvent.Parameters.AddWithValue("@reason", reason);
            insertEvent.Parameters.AddWithValue("@createdAt", now);
            insertEvent.ExecuteNonQuery();
        }

        return caseIds;
    }

    private static string CreateModerationCase(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string kind,
        string status,
        ulong steamId64,
        string playerName,
        string actorName,
        string actorUserId,
        ulong? actorSteamId64,
        string reason,
        TimeSpan? duration,
        DateTime? expiresAt,
        string? serverId,
        DateTime now)
    {
        string caseId = Guid.NewGuid().ToString("N");
        int? durationSeconds = duration is null ? null : (int)Math.Min(int.MaxValue, Math.Ceiling(duration.Value.TotalSeconds));
        using var command = new MySqlCommand("""
            INSERT INTO `ModerationCase`
                (`id`, `kind`, `status`, `origin`, `targetSteamId64`, `targetName`, `actorUserId`, `actorSteamId64`, `actorName`, `serverId`, `reason`, `durationSeconds`, `startsAt`, `expiresAt`, `createdAt`, `updatedAt`)
            VALUES
                (@id, @kind, @status, 'GAME', @steamId64, @playerName, @actorUserId, @actorSteamId64, @actorName, @serverId, @reason, @durationSeconds, @startsAt, @expiresAt, @createdAt, @updatedAt)
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", caseId);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@steamId64", steamId64.ToString());
        command.Parameters.AddWithValue("@playerName", playerName);
        command.Parameters.AddWithValue("@actorUserId", string.IsNullOrWhiteSpace(actorUserId) ? DBNull.Value : actorUserId);
        command.Parameters.AddWithValue("@actorSteamId64", actorSteamId64?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@actorName", actorName);
        command.Parameters.AddWithValue("@serverId", serverId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@durationSeconds", durationSeconds ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@startsAt", now);
        command.Parameters.AddWithValue("@expiresAt", expiresAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", now);
        command.Parameters.AddWithValue("@updatedAt", now);
        command.ExecuteNonQuery();

        using var insertEvent = new MySqlCommand("""
            INSERT INTO `ModerationCaseEvent`
                (`id`, `caseId`, `type`, `actorUserId`, `actorSteamId64`, `actorName`, `reason`, `createdAt`)
            VALUES
                (@id, @caseId, 'CREATED', @actorUserId, @actorSteamId64, @actorName, @reason, @createdAt)
            """, connection, transaction);
        insertEvent.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        insertEvent.Parameters.AddWithValue("@caseId", caseId);
        insertEvent.Parameters.AddWithValue("@actorUserId", string.IsNullOrWhiteSpace(actorUserId) ? DBNull.Value : actorUserId);
        insertEvent.Parameters.AddWithValue("@actorSteamId64", actorSteamId64?.ToString() ?? (object)DBNull.Value);
        insertEvent.Parameters.AddWithValue("@actorName", actorName);
        insertEvent.Parameters.AddWithValue("@reason", reason);
        insertEvent.Parameters.AddWithValue("@createdAt", now);
        insertEvent.ExecuteNonQuery();
        return caseId;
    }

    private static void InsertGameCommand(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string commandType,
        ulong steamId64,
        string playerName,
        string reason,
        string? caseId,
        string actorUserId,
        DateTime now)
    {
        string id = Guid.NewGuid().ToString("N");
        using var command = new MySqlCommand("""
            INSERT INTO `GameCommand`
                (`id`, `idempotencyKey`, `type`, `targetSteamId64`, `targetName`, `reason`, `caseId`, `createdByUserId`, `deliverUntil`, `createdAt`)
            VALUES
                (@id, @idempotencyKey, @type, @steamId64, @playerName, @reason, @caseId, @createdByUserId, @deliverUntil, @createdAt)
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@idempotencyKey", $"game:{caseId ?? id}:{commandType.ToLowerInvariant()}");
        command.Parameters.AddWithValue("@type", commandType);
        command.Parameters.AddWithValue("@steamId64", steamId64.ToString());
        command.Parameters.AddWithValue("@playerName", playerName);
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@caseId", caseId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@createdByUserId", string.IsNullOrWhiteSpace(actorUserId) ? DBNull.Value : actorUserId);
        command.Parameters.AddWithValue("@deliverUntil", now.AddMinutes(5));
        command.Parameters.AddWithValue("@createdAt", now);
        command.ExecuteNonQuery();
    }

    private string GetUserId(ulong? steamId64)
    {
        if (steamId64 is null) return string.Empty;

        using var connection = OpenConnection();
        using var command = new MySqlCommand("SELECT `id` FROM `User` WHERE `steamId64` = @steamId64 LIMIT 1", connection);
        command.Parameters.AddWithValue("@steamId64", steamId64.Value.ToString());
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private MySqlConnection OpenConnection()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RaitoDatabase));
        var connection = new MySqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void EnsureTableName(string tableName)
    {
        if (tableName is not ("Ban" or "Mute" or "Gag")) throw new ArgumentException("Unsupported moderation table.", nameof(tableName));
    }

    private static string ModerationKindForTable(string tableName) => tableName switch
    {
        "Ban" => "GAME_BAN",
        "Mute" => "GAME_MUTE",
        "Gag" => "GAME_GAG",
        _ => throw new ArgumentException("Unsupported moderation table.", nameof(tableName))
    };

    private static string CommandTypeForKind(string kind) => kind switch
    {
        "GAME_BAN" => "BAN",
        "GAME_MUTE" => "MUTE",
        "GAME_GAG" => "GAG",
        _ => throw new ArgumentException("Unsupported game moderation kind.", nameof(kind))
    };

    private static string InverseCommandTypeForKind(string kind) => kind switch
    {
        "GAME_BAN" => "UNBAN",
        "GAME_MUTE" => "UNMUTE",
        "GAME_GAG" => "UNGAG",
        _ => throw new ArgumentException("Unsupported game moderation kind.", nameof(kind))
    };

    private static void EnsureModerationColumns(string tableName, string actorColumn)
    {
        EnsureTableName(tableName);
        string expected = tableName switch
        {
            "Ban" => "bannedBy",
            "Mute" => "mutedBy",
            "Gag" => "gaggedBy",
            _ => string.Empty
        };
        if (!string.Equals(expected, actorColumn, StringComparison.Ordinal)) throw new ArgumentException("Unsupported moderation actor column.", nameof(actorColumn));
    }

    private static string ColumnForDate(string actorColumn) => actorColumn switch
    {
        "bannedBy" => "bannedAt",
        "mutedBy" => "mutedAt",
        "gaggedBy" => "gaggedAt",
        _ => throw new ArgumentException("Unsupported moderation actor column.", nameof(actorColumn))
    };

    public void Dispose()
    {
        _disposed = true;
    }
}
