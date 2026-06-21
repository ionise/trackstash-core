using Microsoft.Data.Sqlite;
using TrackStash.Core.Services;

namespace TrackStash.Core.Sqlite;

/// <summary>
/// SQLite local-development adapter for embedding work-item queue storage.
/// </summary>
public sealed class SqliteEmbeddingWorkItemStore : IEmbeddingWorkItemStore
{
    private const string StatusPending = "Pending";
    private const string StatusProcessing = "Processing";
    private const string StatusCompleted = "Completed";
    private const string StatusFailed = "Failed";

    private readonly string _connectionString;

    public SqliteEmbeddingWorkItemStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            // Allow transient writer locks to clear before failing commands.
            DefaultTimeout = 30,
        }.ToString();
    }

    public async ValueTask<EmbeddingEnqueueResult> EnqueueAsync(
        EmbeddingEntityChange change,
        EmbeddingModelIdentity model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.EntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.EntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.ModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.ModelVersion);

        var now = change.OccurredUtc == default ? DateTimeOffset.UtcNow : change.OccurredUtc;
        var workItemId = Guid.NewGuid().ToString("N");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO embedding_work_item (
                    work_item_id,
                    entity_id,
                    entity_type,
                    model_name,
                    model_version,
                    operation,
                    force_regenerate,
                    status,
                    attempt_count,
                    enqueued_utc,
                    next_attempt_utc,
                    leased_until_utc,
                    last_error,
                    updated_utc
                ) VALUES (
                    @workItemId,
                    @entityId,
                    @entityType,
                    @modelName,
                    @modelVersion,
                    @operation,
                    @forceRegenerate,
                    @status,
                    @attemptCount,
                    @enqueuedUtc,
                    NULL,
                    NULL,
                    NULL,
                    @updatedUtc
                )
                """;
            insert.Parameters.AddWithValue("@workItemId", workItemId);
            insert.Parameters.AddWithValue("@entityId", change.EntityId);
            insert.Parameters.AddWithValue("@entityType", change.EntityType);
            insert.Parameters.AddWithValue("@modelName", model.ModelName);
            insert.Parameters.AddWithValue("@modelVersion", model.ModelVersion);
            insert.Parameters.AddWithValue("@operation", change.Operation.ToString());
            insert.Parameters.AddWithValue("@forceRegenerate", change.ForceRegenerate ? 1 : 0);
            insert.Parameters.AddWithValue("@status", StatusPending);
            insert.Parameters.AddWithValue("@attemptCount", 0);
            insert.Parameters.AddWithValue("@enqueuedUtc", now.ToString("O"));
            insert.Parameters.AddWithValue("@updatedUtc", now.ToString("O"));

            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new EmbeddingEnqueueResult(true, false, workItemId);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            var existingId = await FindActiveWorkItemIdAsync(connection, change, model, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existingId))
                return new EmbeddingEnqueueResult(false, true, existingId!, "Active work item already exists for this entity and model.");

            throw;
        }
    }

    public async ValueTask<IReadOnlyList<EmbeddingWorkItem>> LeasePendingAsync(
        int maxItems,
        TimeSpan leaseDuration,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (maxItems <= 0)
            return Array.Empty<EmbeddingWorkItem>();

        var leaseUntil = nowUtc.Add(leaseDuration);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        var items = new List<EmbeddingWorkItem>();

        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT work_item_id, entity_id, entity_type, model_name, model_version, operation, force_regenerate, attempt_count, enqueued_utc, next_attempt_utc, last_error
                FROM embedding_work_item
                WHERE status IN ('Pending', 'Failed')
                  AND (next_attempt_utc IS NULL OR next_attempt_utc <= @nowUtc)
                ORDER BY enqueued_utc
                LIMIT @maxItems
                """;
            select.Parameters.AddWithValue("@nowUtc", nowUtc.ToString("O"));
            select.Parameters.AddWithValue("@maxItems", maxItems);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var item = new EmbeddingWorkItem(
                    WorkItemId: reader.GetString(0),
                    EntityId: reader.GetString(1),
                    EntityType: reader.GetString(2),
                    Model: new EmbeddingModelIdentity(reader.GetString(3), reader.GetString(4)),
                    Operation: ParseOperation(reader.GetString(5)),
                    ForceRegenerate: reader.GetInt64(6) == 1,
                    AttemptCount: reader.GetInt32(7),
                    EnqueuedUtc: DateTimeOffset.Parse(reader.GetString(8)),
                    NextAttemptUtc: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
                    LastError: reader.IsDBNull(10) ? null : reader.GetString(10));
                items.Add(item);
            }
        }

        foreach (var item in items)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE embedding_work_item
                SET status = @status,
                    attempt_count = attempt_count + 1,
                    leased_until_utc = @leasedUntilUtc,
                    updated_utc = @updatedUtc
                WHERE work_item_id = @workItemId
                """;
            update.Parameters.AddWithValue("@status", StatusProcessing);
            update.Parameters.AddWithValue("@leasedUntilUtc", leaseUntil.ToString("O"));
            update.Parameters.AddWithValue("@updatedUtc", nowUtc.ToString("O"));
            update.Parameters.AddWithValue("@workItemId", item.WorkItemId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return items
            .Select(i => i with { AttemptCount = i.AttemptCount + 1 })
            .ToArray();
    }

    public async ValueTask MarkCompletedAsync(
        string workItemId,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE embedding_work_item
            SET status = @status,
                leased_until_utc = NULL,
                next_attempt_utc = NULL,
                last_error = NULL,
                updated_utc = @updatedUtc
            WHERE work_item_id = @workItemId
            """;
        cmd.Parameters.AddWithValue("@status", StatusCompleted);
        cmd.Parameters.AddWithValue("@updatedUtc", completedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@workItemId", workItemId);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask MarkFailedAsync(
        string workItemId,
        string error,
        int nextAttemptInSeconds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        var retryDelaySeconds = Math.Max(1, nextAttemptInSeconds);
        var nextAttemptUtc = nowUtc.AddSeconds(retryDelaySeconds);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE embedding_work_item
            SET status = @status,
                leased_until_utc = NULL,
                next_attempt_utc = @nextAttemptUtc,
                last_error = @lastError,
                updated_utc = @updatedUtc
            WHERE work_item_id = @workItemId
            """;
        cmd.Parameters.AddWithValue("@status", StatusFailed);
        cmd.Parameters.AddWithValue("@nextAttemptUtc", nextAttemptUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@lastError", error);
        cmd.Parameters.AddWithValue("@updatedUtc", nowUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@workItemId", workItemId);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static EmbeddingChangeOperation ParseOperation(string value)
    {
        if (Enum.TryParse<EmbeddingChangeOperation>(value, ignoreCase: true, out var parsed))
            return parsed;

        return EmbeddingChangeOperation.Updated;
    }

    private static async ValueTask<string?> FindActiveWorkItemIdAsync(
        SqliteConnection connection,
        EmbeddingEntityChange change,
        EmbeddingModelIdentity model,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT work_item_id
            FROM embedding_work_item
            WHERE entity_id = @entityId
              AND entity_type = @entityType
              AND model_name = @modelName
              AND model_version = @modelVersion
              AND status IN ('Pending', 'Processing')
            ORDER BY enqueued_utc
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@entityId", change.EntityId);
        cmd.Parameters.AddWithValue("@entityType", change.EntityType);
        cmd.Parameters.AddWithValue("@modelName", model.ModelName);
        cmd.Parameters.AddWithValue("@modelVersion", model.ModelVersion);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result?.ToString();
    }
}
