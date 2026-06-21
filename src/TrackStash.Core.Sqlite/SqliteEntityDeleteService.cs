using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteEntityDeleteService : IEntityDeleteService
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private const int MaxSampleValues = 5;

    public SqliteEntityDeleteService(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async ValueTask<DeleteAnalysisResult> AnalyzeDependenciesAsync(
        string entityType,
        string entityId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        var blockers = new List<DeleteBlocker>();
        var ownedCleanupTables = new List<OwnedCleanupTable>();

        // Analyze dependencies based on entity type
        await AnalyzeEntityDependenciesAsync(entityType, entityId, blockers, ownedCleanupTables, cancellationToken);

        return new DeleteAnalysisResult
        {
            EntityType = entityType,
            EntityId = entityId,
            Blockers = blockers,
            OwnedCleanupRows = ownedCleanupTables,
        };
    }

    public async ValueTask<DeleteExecutionResult> DeleteEntityAsync(
        string entityType,
        string entityId,
        string? deletedBy = null,
        string? deleteReason = null,
        DateTimeOffset? purgeAfterUtc = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // First, analyze dependencies
            var analysis = await AnalyzeDependenciesAsync(entityType, entityId, null!, cancellationToken);

            if (!analysis.IsSafeToDelete)
            {
                var blockerNames = string.Join(", ", analysis.Blockers.Select(b => $"{b.TableName}.{b.ColumnName}"));
                return new DeleteExecutionResult
                {
                    EntityType = entityType,
                    EntityId = entityId,
                    Success = false,
                    ErrorMessage = $"Cannot delete {entityType} '{entityId}': blocked by {blockerNames}",
                    ExecutedUtc = DateTimeOffset.UtcNow,
                };
            }

            // Get the entity to create a tombstone
            var entityJson = await GetEntityJsonAsync(entityType, entityId, cancellationToken);
            if (entityJson == null)
            {
                return new DeleteExecutionResult
                {
                    EntityType = entityType,
                    EntityId = entityId,
                    Success = false,
                    ErrorMessage = $"{entityType} '{entityId}' not found",
                    ExecutedUtc = DateTimeOffset.UtcNow,
                };
            }

            var now = DateTimeOffset.UtcNow;
            var tombstone = new EntityTombstone
            {
                EntityType = entityType,
                EntityId = entityId,
                EntityDataJson = entityJson,
                DeletedBy = deletedBy,
                DeleteReason = deleteReason,
                DeletedUtc = now,
                PurgeAfterUtc = purgeAfterUtc,
                IsPurged = false,
            };

            // Delete owned cleanup rows and the entity in a transaction
            var cleanupCount = await DeleteEntityWithCleanupAsync(entityType, entityId, analysis.OwnedCleanupRows, tombstone, cancellationToken);

            return new DeleteExecutionResult
            {
                EntityType = entityType,
                EntityId = entityId,
                Success = true,
                CleanupRowsDeleted = cleanupCount,
                Tombstone = tombstone,
                ExecutedUtc = now,
            };
        }
        catch (Exception ex)
        {
            return new DeleteExecutionResult
            {
                EntityType = entityType,
                EntityId = entityId,
                Success = false,
                ErrorMessage = ex.Message,
                ExecutedUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    public async ValueTask<int> DeleteOwnedRowsAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        var ownedCleanupTables = new List<OwnedCleanupTable>();
        await AnalyzeEntityDependenciesAsync(
            entityType,
            entityId,
            blockers: [],
            ownedCleanupTables,
            cancellationToken).ConfigureAwait(false);

        var deleted = 0;
        foreach (var table in ownedCleanupTables)
        {
            var deleteQuery = $"DELETE FROM {table.TableName} WHERE {table.ReferencingColumn} = @entityId";
            using var deleteCommand = new SqliteCommand(deleteQuery, _connection) { Transaction = _transaction };
            deleteCommand.Parameters.AddWithValue("@entityId", entityId);
            deleted += await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    public async ValueTask<EntityTombstone?> GetTombstoneAsync(
        string entityType,
        string entityId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT entity_type, entity_id, entity_data_json, deleted_by, delete_reason,
                   deleted_utc, purge_after_utc, is_purged
            FROM entity_tombstone
            WHERE entity_type = @entityType AND entity_id = @entityId
            """;

        using var command = new SqliteCommand(query, _connection) { Transaction = _transaction };
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@entityId", entityId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new EntityTombstone
            {
                EntityType = reader.GetString(0),
                EntityId = reader.GetString(1),
                EntityDataJson = reader.GetString(2),
                DeletedBy = reader.IsDBNull(3) ? null : reader.GetString(3),
                DeleteReason = reader.IsDBNull(4) ? null : reader.GetString(4),
                DeletedUtc = DateTimeOffset.Parse(reader.GetString(5)),
                PurgeAfterUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                IsPurged = reader.GetInt32(7) != 0,
            };
        }

        return null;
    }

    public async ValueTask<IReadOnlyList<EntityTombstone>> ListTombstonesAsync(
        string entityType,
        int pageSize = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT entity_type, entity_id, entity_data_json, deleted_by, delete_reason,
                   deleted_utc, purge_after_utc, is_purged
            FROM entity_tombstone
            WHERE entity_type = @entityType AND is_purged = 0
            ORDER BY deleted_utc DESC
            LIMIT @limit OFFSET @offset
            """;

        var tombstones = new List<EntityTombstone>();
        using var command = new SqliteCommand(query, _connection) { Transaction = _transaction };
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@limit", pageSize);
        command.Parameters.AddWithValue("@offset", offset);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tombstones.Add(new EntityTombstone
            {
                EntityType = reader.GetString(0),
                EntityId = reader.GetString(1),
                EntityDataJson = reader.GetString(2),
                DeletedBy = reader.IsDBNull(3) ? null : reader.GetString(3),
                DeleteReason = reader.IsDBNull(4) ? null : reader.GetString(4),
                DeletedUtc = DateTimeOffset.Parse(reader.GetString(5)),
                PurgeAfterUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                IsPurged = reader.GetInt32(7) != 0,
            });
        }

        return tombstones;
    }

    public async ValueTask<RestoreAnalysisResult> AnalyzeRestoreAsync(
        EntityTombstone tombstone,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        var conflicts = new List<string>();
        var prerequisites = new List<string>();
        var tablesToRestore = new List<string>();
        var derivedDataToRebuild = new List<string>();

        // Check if entity already exists
        var existingEntity = await GetEntityJsonAsync(tombstone.EntityType, tombstone.EntityId, cancellationToken);
        if (existingEntity != null)
        {
            conflicts.Add($"{tombstone.EntityType} with ID '{tombstone.EntityId}' already exists in the database");
        }

        // Check prerequisites based on entity type
        var prereqs = await CheckRestorePrerequisitesAsync(tombstone, cancellationToken);
        prerequisites.AddRange(prereqs);

        // Set tables to restore
        tablesToRestore.Add(tombstone.EntityType);
        switch (tombstone.EntityType.ToLowerInvariant())
        {
            case "label":
                tablesToRestore.AddRange(["label_external_ref", "label_alias"]);
                break;
            case "artist":
                tablesToRestore.AddRange(["artist_external_ref", "artist_alias"]);
                break;
            case "release":
                tablesToRestore.AddRange(["release_external_ref", "release_artist_credit", "release_label_link", "release_recording"]);
                break;
            case "recording":
                tablesToRestore.AddRange(["recording_external_ref", "recording_artist_credit", "recording_relationship"]);
                break;
        }

        // Derived data that should be rebuilt
        derivedDataToRebuild.AddRange(["media_file_recording_match", "media_file_recording_candidate", "embedding_document"]);

        return new RestoreAnalysisResult
        {
            Tombstone = tombstone,
            IsSafeToRestore = conflicts.Count == 0 && prerequisites.Count == 0,
            Conflicts = conflicts,
            Prerequisites = prerequisites,
            TablesToRestore = tablesToRestore,
            DerivedDataToRebuild = derivedDataToRebuild,
        };
    }

    public async ValueTask<RestoreExecutionResult> RestoreEntityAsync(
        EntityTombstone tombstone,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Analyze restore prerequisites first
            var analysis = await AnalyzeRestoreAsync(tombstone, unitOfWork, cancellationToken);

            if (!analysis.IsSafeToRestore)
            {
                var issues = string.Join("; ", analysis.Conflicts.Concat(analysis.Prerequisites));
                return new RestoreExecutionResult
                {
                    RestoredTombstone = tombstone,
                    Success = false,
                    ErrorMessage = $"Cannot restore: {issues}",
                    RestoredUtc = DateTimeOffset.UtcNow,
                };
            }

            // Parse the entity data and restore it
            var cleanupCount = await RestoreEntityWithCleanupAsync(tombstone, cancellationToken);

            var rebuilds = string.Join(", ", analysis.DerivedDataToRebuild);

            return new RestoreExecutionResult
            {
                RestoredTombstone = tombstone,
                Success = true,
                CleanupRowsRestored = cleanupCount,
                RestoredUtc = DateTimeOffset.UtcNow,
                RebuildReminders = new[] { $"Rebuild derived data: {rebuilds}" },
            };
        }
        catch (Exception ex)
        {
            return new RestoreExecutionResult
            {
                RestoredTombstone = tombstone,
                Success = false,
                ErrorMessage = ex.Message,
                RestoredUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    public async ValueTask<bool> PurgeTombstoneAsync(
        string entityType,
        string entityId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            UPDATE entity_tombstone
            SET is_purged = 1, updated_utc = @now
            WHERE entity_type = @entityType AND entity_id = @entityId
            """;

        using var command = new SqliteCommand(query, _connection) { Transaction = _transaction };
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@entityId", entityId);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    // Private helper methods

    private async ValueTask AnalyzeEntityDependenciesAsync(
        string entityType,
        string entityId,
        List<DeleteBlocker> blockers,
        List<OwnedCleanupTable> ownedCleanupTables,
        CancellationToken cancellationToken = default)
    {
        switch (entityType.ToLowerInvariant())
        {
            case "label":
                await AnalyzeLabelDependenciesAsync(entityId, blockers, ownedCleanupTables, cancellationToken);
                break;
            case "artist":
                await AnalyzeArtistDependenciesAsync(entityId, blockers, ownedCleanupTables, cancellationToken);
                break;
            case "release":
                await AnalyzeReleaseDependenciesAsync(entityId, blockers, ownedCleanupTables, cancellationToken);
                break;
            case "recording":
                await AnalyzeRecordingDependenciesAsync(entityId, blockers, ownedCleanupTables, cancellationToken);
                break;
        }
    }

    private async ValueTask AnalyzeLabelDependenciesAsync(
        string labelId,
        List<DeleteBlocker> blockers,
        List<OwnedCleanupTable> ownedCleanupTables,
        CancellationToken cancellationToken = default)
    {
        // Blockers: release_label_link references this label
        await CountAndAddBlockerAsync(
            "release_label_link",
            "label_id",
            labelId,
            "Releases link to this label",
            blockers,
            cancellationToken);

        // Owned cleanup: label_external_ref, label_alias
        await CountAndAddOwnedCleanupAsync("label_external_ref", "label_id", labelId, ownedCleanupTables, cancellationToken);
        await CountAndAddOwnedCleanupAsync("label_alias", "label_id", labelId, ownedCleanupTables, cancellationToken);
    }

    private async ValueTask AnalyzeArtistDependenciesAsync(
        string artistId,
        List<DeleteBlocker> blockers,
        List<OwnedCleanupTable> ownedCleanupTables,
        CancellationToken cancellationToken = default)
    {
        // Blockers: release_artist_credit references this artist
        await CountAndAddBlockerAsync(
            "release_artist_credit",
            "artist_id",
            artistId,
            "Release artist credits reference this artist",
            blockers,
            cancellationToken);

        // Blockers: recording_artist_credit references this artist
        await CountAndAddBlockerAsync(
            "recording_artist_credit",
            "artist_id",
            artistId,
            "Recording artist credits reference this artist",
            blockers,
            cancellationToken);

        // Blockers: recording_relationship.from_recording_id or to_recording_id (artists are handled by recordings)
        // Blockers: artist_relationship.from_artist_id or to_artist_id
        await CountAndAddBlockerAsync(
            "recording_relationship",
            "from_recording_id",
            artistId,
            "Artist relationships reference this artist",
            blockers,
            cancellationToken);

        // Owned cleanup: artist_external_ref, artist_alias
        await CountAndAddOwnedCleanupAsync("artist_external_ref", "artist_id", artistId, ownedCleanupTables, cancellationToken);
        await CountAndAddOwnedCleanupAsync("artist_alias", "artist_id", artistId, ownedCleanupTables, cancellationToken);
    }

    private async ValueTask AnalyzeReleaseDependenciesAsync(
        string releaseId,
        List<DeleteBlocker> blockers,
        List<OwnedCleanupTable> ownedCleanupTables,
        CancellationToken cancellationToken = default)
    {
        // Blockers: media_file_recording_match references a recording in a release (complex check)
        // For now, we'll skip this as it's a derived relationship

        // Owned cleanup: release_external_ref, release_artist_credit, release_label_link, release_recording
        // Note: release_alias table doesn't exist in schema, so we skip it
        await CountAndAddOwnedCleanupAsync("release_external_ref", "release_id", releaseId, ownedCleanupTables, cancellationToken);
        await CountAndAddOwnedCleanupAsync("release_artist_credit", "release_id", releaseId, ownedCleanupTables, cancellationToken);
        await CountAndAddOwnedCleanupAsync("release_label_link", "release_id", releaseId, ownedCleanupTables, cancellationToken);
        await CountAndAddOwnedCleanupAsync("release_recording", "release_id", releaseId, ownedCleanupTables, cancellationToken);
    }

    private async ValueTask AnalyzeRecordingDependenciesAsync(
        string recordingId,
        List<DeleteBlocker> blockers,
        List<OwnedCleanupTable> ownedCleanupTables,
        CancellationToken cancellationToken = default)
    {
        // Blockers: release_recording references this recording
        await CountAndAddBlockerAsync(
            "release_recording",
            "recording_id",
            recordingId,
            "Releases contain this recording",
            blockers,
            cancellationToken);

        // Blockers: media_file_recording_match references this recording
        await CountAndAddBlockerAsync(
            "media_file_recording_match",
            "recording_id",
            recordingId,
            "Media files are matched to this recording",
            blockers,
            cancellationToken);

        // Blockers: media_file_recording_candidate references this recording
        await CountAndAddBlockerAsync(
            "media_file_recording_candidate",
            "recording_id",
            recordingId,
            "Media files have this recording as a candidate",
            blockers,
            cancellationToken);

        // Blockers: recording_relationship references this recording
        await CountAndAddBlockerAsync(
            "recording_relationship",
            "from_recording_id",
            recordingId,
            "Other recordings have relationships to this recording",
            blockers,
            cancellationToken);

        // Owned cleanup: recording_external_ref, recording_artist_credit, recording_relationship (outgoing)
        // Note: recording_alias table doesn't exist in schema, so we skip it
        await CountAndAddOwnedCleanupAsync("recording_external_ref", "recording_id", recordingId, ownedCleanupTables, cancellationToken);
        await CountAndAddOwnedCleanupAsync("recording_artist_credit", "recording_id", recordingId, ownedCleanupTables, cancellationToken);
        await CountAndAddOwnedCleanupAsync("recording_relationship", "from_recording_id", recordingId, ownedCleanupTables, cancellationToken);
    }

    private async ValueTask CountAndAddBlockerAsync(
        string tableName,
        string columnName,
        string entityId,
        string reason,
        List<DeleteBlocker> blockers,
        CancellationToken cancellationToken = default)
    {
        var (count, samples) = await GetBlockingRowsAsync(tableName, columnName, entityId, MaxSampleValues, cancellationToken);
        if (count > 0)
        {
            blockers.Add(new DeleteBlocker
            {
                TableName = tableName,
                ColumnName = columnName,
                RowCount = count,
                SampleValues = samples,
                Reason = reason,
            });
        }
    }

    private async ValueTask CountAndAddOwnedCleanupAsync(
        string tableName,
        string columnName,
        string entityId,
        List<OwnedCleanupTable> ownedCleanupTables,
        CancellationToken cancellationToken = default)
    {
        var count = await CountRowsAsync(tableName, columnName, entityId, cancellationToken);
        if (count > 0)
        {
            ownedCleanupTables.Add(new OwnedCleanupTable
            {
                TableName = tableName,
                RowCount = count,
                ReferencingColumn = columnName,
            });
        }
    }

    private async ValueTask<(int count, IReadOnlyList<string> samples)> GetBlockingRowsAsync(
        string tableName,
        string columnName,
        string entityId,
        int maxSamples,
        CancellationToken cancellationToken = default)
    {
        var countQuery = $"SELECT COUNT(*) FROM {tableName} WHERE {columnName} = @entityId";
        using var countCommand = new SqliteCommand(countQuery, _connection) { Transaction = _transaction };
        countCommand.Parameters.AddWithValue("@entityId", entityId);
        var count = (long?)await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0;

        var samples = new List<string>();
        if (count > 0 && maxSamples > 0)
        {
            var sampleQuery = $"SELECT {columnName} FROM {tableName} WHERE {columnName} = @entityId LIMIT {maxSamples}";
            using var sampleCommand = new SqliteCommand(sampleQuery, _connection) { Transaction = _transaction };
            sampleCommand.Parameters.AddWithValue("@entityId", entityId);
            using var reader = await sampleCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                samples.Add(reader.GetString(0));
            }
        }

        return ((int)count, samples);
    }

    private async ValueTask<int> CountRowsAsync(
        string tableName,
        string columnName,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        var query = $"SELECT COUNT(*) FROM {tableName} WHERE {columnName} = @entityId";
        using var command = new SqliteCommand(query, _connection) { Transaction = _transaction };
        command.Parameters.AddWithValue("@entityId", entityId);
        var count = (long?)await command.ExecuteScalarAsync(cancellationToken) ?? 0;
        return (int)count;
    }

    private async ValueTask<string?> GetEntityJsonAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        var query = $"SELECT COALESCE(source_payload_json, '{{}}') FROM {entityType} WHERE {entityType}_id = @entityId";
        using var command = new SqliteCommand(query, _connection) { Transaction = _transaction };
        command.Parameters.AddWithValue("@entityId", entityId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async ValueTask<int> DeleteEntityWithCleanupAsync(
        string entityType,
        string entityId,
        IReadOnlyList<OwnedCleanupTable> ownedCleanupTables,
        EntityTombstone tombstone,
        CancellationToken cancellationToken = default)
    {
        var cleanupCount = 0;

        // Delete owned cleanup rows
        foreach (var table in ownedCleanupTables)
        {
            var deleteQuery = $"DELETE FROM {table.TableName} WHERE {table.ReferencingColumn} = @entityId";
            using var deleteCommand = new SqliteCommand(deleteQuery, _connection) { Transaction = _transaction };
            deleteCommand.Parameters.AddWithValue("@entityId", entityId);
            cleanupCount += await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Delete the entity itself
        var entityDeleteQuery = $"DELETE FROM {entityType} WHERE {entityType}_id = @entityId";
        using var entityDeleteCommand = new SqliteCommand(entityDeleteQuery, _connection) { Transaction = _transaction };
        entityDeleteCommand.Parameters.AddWithValue("@entityId", entityId);
        await entityDeleteCommand.ExecuteNonQueryAsync(cancellationToken);

        // Insert tombstone
        const string tombstoneQuery = """
            INSERT INTO entity_tombstone
            (entity_type, entity_id, entity_data_json, deleted_by, delete_reason, deleted_utc, purge_after_utc, is_purged, created_utc, updated_utc)
            VALUES (@entityType, @entityId, @entityDataJson, @deletedBy, @deleteReason, @deletedUtc, @purgeAfterUtc, 0, @now, @now)
            """;
        using var tombstoneCommand = new SqliteCommand(tombstoneQuery, _connection) { Transaction = _transaction };
        tombstoneCommand.Parameters.AddWithValue("@entityType", tombstone.EntityType);
        tombstoneCommand.Parameters.AddWithValue("@entityId", tombstone.EntityId);
        tombstoneCommand.Parameters.AddWithValue("@entityDataJson", tombstone.EntityDataJson);
        tombstoneCommand.Parameters.AddWithValue("@deletedBy", (object?)tombstone.DeletedBy ?? DBNull.Value);
        tombstoneCommand.Parameters.AddWithValue("@deleteReason", (object?)tombstone.DeleteReason ?? DBNull.Value);
        tombstoneCommand.Parameters.AddWithValue("@deletedUtc", tombstone.DeletedUtc.ToString("O"));
        tombstoneCommand.Parameters.AddWithValue("@purgeAfterUtc", (object?)(tombstone.PurgeAfterUtc?.ToString("O")) ?? DBNull.Value);
        tombstoneCommand.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await tombstoneCommand.ExecuteNonQueryAsync(cancellationToken);

        return cleanupCount;
    }

    private async ValueTask<List<string>> CheckRestorePrerequisitesAsync(
        EntityTombstone tombstone,
        CancellationToken cancellationToken = default)
    {
        var prerequisites = new List<string>();

        // Parse the entity JSON to extract references
        try
        {
            using var doc = JsonDocument.Parse(tombstone.EntityDataJson);
            var root = doc.RootElement;

            switch (tombstone.EntityType.ToLowerInvariant())
            {
                case "release":
                    // Check if label entities exist for release
                    if (root.TryGetProperty("labelLinks", out var labelLinks))
                    {
                        foreach (var link in labelLinks.EnumerateArray())
                        {
                            if (link.TryGetProperty("labelId", out var labelId))
                            {
                                var labelExists = await CheckEntityExistsAsync("label", labelId.GetString()!, cancellationToken);
                                if (!labelExists)
                                {
                                    prerequisites.Add($"Label '{labelId.GetString()}' must be restored first");
                                }
                            }
                        }
                    }

                    // Check if artist entities exist for release credits
                    if (root.TryGetProperty("artistCredits", out var artistCredits))
                    {
                        foreach (var credit in artistCredits.EnumerateArray())
                        {
                            if (credit.TryGetProperty("artistId", out var artistId))
                            {
                                var artistExists = await CheckEntityExistsAsync("artist", artistId.GetString()!, cancellationToken);
                                if (!artistExists)
                                {
                                    prerequisites.Add($"Artist '{artistId.GetString()}' must be restored first");
                                }
                            }
                        }
                    }

                    break;

                case "recording":
                    // Check if artist entities exist for recording credits
                    if (root.TryGetProperty("artistCredits", out var recArtistCredits))
                    {
                        foreach (var credit in recArtistCredits.EnumerateArray())
                        {
                            if (credit.TryGetProperty("artistId", out var artistId))
                            {
                                var artistExists = await CheckEntityExistsAsync("artist", artistId.GetString()!, cancellationToken);
                                if (!artistExists)
                                {
                                    prerequisites.Add($"Artist '{artistId.GetString()}' must be restored first");
                                }
                            }
                        }
                    }

                    break;
            }
        }
        catch
        {
            // If we can't parse the JSON, continue with no prerequisites
        }

        return prerequisites;
    }

    private async ValueTask<bool> CheckEntityExistsAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        var query = $"SELECT 1 FROM {entityType} WHERE {entityType}_id = @entityId LIMIT 1";
        using var command = new SqliteCommand(query, _connection) { Transaction = _transaction };
        command.Parameters.AddWithValue("@entityId", entityId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    private async ValueTask<int> RestoreEntityWithCleanupAsync(
        EntityTombstone tombstone,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Deserialize entity from JSON
            using var doc = JsonDocument.Parse(tombstone.EntityDataJson);
            var root = doc.RootElement;

            var cleanupCount = 0;
            var now = DateTimeOffset.UtcNow.ToString("O");

            // Get entity ID and type
            var entityId = tombstone.EntityId;
            var entityType = tombstone.EntityType.ToLowerInvariant();

            // Re-insert the entity based on type
            switch (entityType)
            {
                case "label":
                    cleanupCount += await RestoreLabelAsync(root, entityId, now, cancellationToken);
                    break;
                case "artist":
                    cleanupCount += await RestoreArtistAsync(root, entityId, now, cancellationToken);
                    break;
                case "release":
                    cleanupCount += await RestoreReleaseAsync(root, entityId, now, cancellationToken);
                    break;
                case "recording":
                    cleanupCount += await RestoreRecordingAsync(root, entityId, now, cancellationToken);
                    break;
            }

            return cleanupCount;
        }
        catch (Exception)
        {
            // If deserialization or restoration fails, return 0
            return 0;
        }
    }

    private async ValueTask<int> RestoreLabelAsync(
        JsonElement root,
        string labelId,
        string now,
        CancellationToken cancellationToken = default)
    {
        var cleanupCount = 0;

        // Re-insert label
        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var normalizedName = root.TryGetProperty("normalizedName", out var normNameProp) ? normNameProp.GetString() : null;
        var sortName = root.TryGetProperty("sortName", out var sortNameProp) ? sortNameProp.GetString() : null;
        var sourcePayloadJson = root.TryGetProperty("sourcePayloadJson", out var sourceProp) ? sourceProp.GetString() : null;

        const string labelInsert = """
            INSERT INTO label (label_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc)
            VALUES (@labelId, @name, @normalizedName, @sortName, @sourcePayloadJson, @now, @now)
            """;

        using var labelCmd = new SqliteCommand(labelInsert, _connection) { Transaction = _transaction };
        labelCmd.Parameters.AddWithValue("@labelId", labelId);
        labelCmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        labelCmd.Parameters.AddWithValue("@normalizedName", (object?)normalizedName ?? DBNull.Value);
        labelCmd.Parameters.AddWithValue("@sortName", (object?)sortName ?? DBNull.Value);
        labelCmd.Parameters.AddWithValue("@sourcePayloadJson", (object?)sourcePayloadJson ?? DBNull.Value);
        labelCmd.Parameters.AddWithValue("@now", now);
        await labelCmd.ExecuteNonQueryAsync(CancellationToken.None);

        // Restore external refs
        if (root.TryGetProperty("externalReferences", out var externalRefsElem))
        {
            cleanupCount += await RestoreExternalRefsAsync("label_external_ref", "label_id", labelId, externalRefsElem, cancellationToken);
        }

        // Restore aliases
        if (root.TryGetProperty("aliases", out var aliasesElem))
        {
            cleanupCount += await RestoreAliasesAsync("label_alias", "label_id", labelId, aliasesElem, cancellationToken);
        }

        return cleanupCount;
    }

    private async ValueTask<int> RestoreArtistAsync(
        JsonElement root,
        string artistId,
        string now,
        CancellationToken cancellationToken = default)
    {
        var cleanupCount = 0;

        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var normalizedName = root.TryGetProperty("normalizedName", out var normNameProp) ? normNameProp.GetString() : null;
        var sortName = root.TryGetProperty("sortName", out var sortNameProp) ? sortNameProp.GetString() : null;
        var sourcePayloadJson = root.TryGetProperty("sourcePayloadJson", out var sourceProp) ? sourceProp.GetString() : null;

        const string artistInsert = """
            INSERT INTO artist (artist_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc)
            VALUES (@artistId, @name, @normalizedName, @sortName, @sourcePayloadJson, @now, @now)
            """;

        using var artistCmd = new SqliteCommand(artistInsert, _connection) { Transaction = _transaction };
        artistCmd.Parameters.AddWithValue("@artistId", artistId);
        artistCmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        artistCmd.Parameters.AddWithValue("@normalizedName", (object?)normalizedName ?? DBNull.Value);
        artistCmd.Parameters.AddWithValue("@sortName", (object?)sortName ?? DBNull.Value);
        artistCmd.Parameters.AddWithValue("@sourcePayloadJson", (object?)sourcePayloadJson ?? DBNull.Value);
        artistCmd.Parameters.AddWithValue("@now", now);
        await artistCmd.ExecuteNonQueryAsync(CancellationToken.None);

        if (root.TryGetProperty("externalReferences", out var externalRefsElem))
        {
            cleanupCount += await RestoreExternalRefsAsync("artist_external_ref", "artist_id", artistId, externalRefsElem, cancellationToken);
        }

        if (root.TryGetProperty("aliases", out var aliasesElem))
        {
            cleanupCount += await RestoreAliasesAsync("artist_alias", "artist_id", artistId, aliasesElem, cancellationToken);
        }

        return cleanupCount;
    }

    private async ValueTask<int> RestoreReleaseAsync(
        JsonElement root,
        string releaseId,
        string now,
        CancellationToken cancellationToken = default)
    {
        var cleanupCount = 0;

        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var normalizedName = root.TryGetProperty("normalizedName", out var normNameProp) ? normNameProp.GetString() : null;
        var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
        var sourcePayloadJson = root.TryGetProperty("sourcePayloadJson", out var sourceProp) ? sourceProp.GetString() : null;

        const string releaseInsert = """
            INSERT INTO release (release_id, name, normalized_name, title, source_payload_json, created_utc, updated_utc)
            VALUES (@releaseId, @name, @normalizedName, @title, @sourcePayloadJson, @now, @now)
            """;

        using var releaseCmd = new SqliteCommand(releaseInsert, _connection) { Transaction = _transaction };
        releaseCmd.Parameters.AddWithValue("@releaseId", releaseId);
        releaseCmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        releaseCmd.Parameters.AddWithValue("@normalizedName", (object?)normalizedName ?? DBNull.Value);
        releaseCmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        releaseCmd.Parameters.AddWithValue("@sourcePayloadJson", (object?)sourcePayloadJson ?? DBNull.Value);
        releaseCmd.Parameters.AddWithValue("@now", now);
        await releaseCmd.ExecuteNonQueryAsync(CancellationToken.None);

        if (root.TryGetProperty("externalReferences", out var externalRefsElem))
        {
            cleanupCount += await RestoreExternalRefsAsync("release_external_ref", "release_id", releaseId, externalRefsElem, cancellationToken);
        }

        // Note: release_alias table doesn't exist in schema, so we don't restore it
        // if (root.TryGetProperty("aliases", out var aliasesElem))
        // {
        //     cleanupCount += await RestoreAliasesAsync("release_alias", "release_id", releaseId, aliasesElem, cancellationToken);
        // }

        if (root.TryGetProperty("artistCredits", out var artistCreditsElem))
        {
            cleanupCount += await RestoreReleaseArtistCreditsAsync(releaseId, artistCreditsElem, cancellationToken);
        }

        if (root.TryGetProperty("labelLinks", out var labelLinksElem))
        {
            cleanupCount += await RestoreReleaseLabelLinksAsync(releaseId, labelLinksElem, cancellationToken);
        }

        if (root.TryGetProperty("releaseRecordings", out var recordingsElem))
        {
            cleanupCount += await RestoreReleaseRecordingsAsync(releaseId, recordingsElem, cancellationToken);
        }

        return cleanupCount;
    }

    private async ValueTask<int> RestoreRecordingAsync(
        JsonElement root,
        string recordingId,
        string now,
        CancellationToken cancellationToken = default)
    {
        var cleanupCount = 0;

        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var normalizedName = root.TryGetProperty("normalizedName", out var normNameProp) ? normNameProp.GetString() : null;
        var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
        var mixName = root.TryGetProperty("mixName", out var mixNameProp) ? mixNameProp.GetString() : null;
        var isrc = root.TryGetProperty("isrc", out var isrcProp) ? isrcProp.GetString() : null;
        var sourcePayloadJson = root.TryGetProperty("sourcePayloadJson", out var sourceProp) ? sourceProp.GetString() : null;

        const string recordingInsert = """
            INSERT INTO recording (recording_id, name, normalized_name, title, mix_name, isrc, source_payload_json, created_utc, updated_utc)
            VALUES (@recordingId, @name, @normalizedName, @title, @mixName, @isrc, @sourcePayloadJson, @now, @now)
            """;

        using var recordingCmd = new SqliteCommand(recordingInsert, _connection) { Transaction = _transaction };
        recordingCmd.Parameters.AddWithValue("@recordingId", recordingId);
        recordingCmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        recordingCmd.Parameters.AddWithValue("@normalizedName", (object?)normalizedName ?? DBNull.Value);
        recordingCmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        recordingCmd.Parameters.AddWithValue("@mixName", (object?)mixName ?? DBNull.Value);
        recordingCmd.Parameters.AddWithValue("@isrc", (object?)isrc ?? DBNull.Value);
        recordingCmd.Parameters.AddWithValue("@sourcePayloadJson", (object?)sourcePayloadJson ?? DBNull.Value);
        recordingCmd.Parameters.AddWithValue("@now", now);
        await recordingCmd.ExecuteNonQueryAsync(CancellationToken.None);

        if (root.TryGetProperty("externalReferences", out var externalRefsElem))
        {
            cleanupCount += await RestoreExternalRefsAsync("recording_external_ref", "recording_id", recordingId, externalRefsElem, cancellationToken);
        }

        // Note: recording_alias table doesn't exist in schema, so we don't restore it
        // if (root.TryGetProperty("aliases", out var aliasesElem))
        // {
        //     cleanupCount += await RestoreAliasesAsync("recording_alias", "recording_id", recordingId, aliasesElem, cancellationToken);
        // }

        if (root.TryGetProperty("artistCredits", out var artistCreditsElem))
        {
            cleanupCount += await RestoreRecordingArtistCreditsAsync(recordingId, artistCreditsElem, cancellationToken);
        }

        if (root.TryGetProperty("releaseLinks", out var releaseLinksElem))
        {
            cleanupCount += await RestoreRecordingReleaseLinksAsync(recordingId, releaseLinksElem, cancellationToken);
        }

        if (root.TryGetProperty("relationships", out var relationshipsElem))
        {
            cleanupCount += await RestoreRecordingRelationshipsAsync(recordingId, relationshipsElem, cancellationToken);
        }

        return cleanupCount;
    }

    private async ValueTask<int> RestoreExternalRefsAsync(
        string tableName,
        string idColumn,
        string entityId,
        JsonElement externalRefsElem,
        CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (var refElem in externalRefsElem.EnumerateArray())
        {
            var source = refElem.TryGetProperty("source", out var sourceProp) ? sourceProp.GetString() : "";
            var externalId = refElem.TryGetProperty("externalId", out var extIdProp) ? extIdProp.GetString() : "";
            var isPrimary = refElem.TryGetProperty("isPrimary", out var isPrimaryProp) ? (isPrimaryProp.GetBoolean() ? 1 : 0) : 0;
            var lastSeenUtc = refElem.TryGetProperty("lastSeenUtc", out var lastSeenProp) ? lastSeenProp.GetString() : null;
            var payloadJson = refElem.TryGetProperty("payloadJson", out var payloadProp) ? payloadProp.GetString() : null;

            var query = $"INSERT INTO {tableName} ({idColumn}, source, external_id, is_primary, last_seen_utc, payload_json) VALUES (@id, @source, @externalId, @isPrimary, @lastSeenUtc, @payloadJson)";
            using var cmd = new SqliteCommand(query, _connection) { Transaction = _transaction };
            cmd.Parameters.AddWithValue("@id", entityId);
            cmd.Parameters.AddWithValue("@source", source ?? "");
            cmd.Parameters.AddWithValue("@externalId", externalId ?? "");
            cmd.Parameters.AddWithValue("@isPrimary", isPrimary);
            cmd.Parameters.AddWithValue("@lastSeenUtc", (object?)lastSeenUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@payloadJson", (object?)payloadJson ?? DBNull.Value);
            count += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return count;
    }

    private async ValueTask<int> RestoreAliasesAsync(
        string tableName,
        string idColumn,
        string entityId,
        JsonElement aliasesElem,
        CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (var aliasElem in aliasesElem.EnumerateArray())
        {
            var value = aliasElem.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : "";
            var normalizedValue = aliasElem.TryGetProperty("normalizedValue", out var normValueProp) ? normValueProp.GetString() : null;
            var isPrimary = aliasElem.TryGetProperty("isPrimary", out var isPrimaryProp) ? (isPrimaryProp.GetBoolean() ? 1 : 0) : 0;

            var query = $"INSERT INTO {tableName} ({idColumn}, value, normalized_value, is_primary) VALUES (@id, @value, @normalizedValue, @isPrimary)";
            using var cmd = new SqliteCommand(query, _connection) { Transaction = _transaction };
            cmd.Parameters.AddWithValue("@id", entityId);
            cmd.Parameters.AddWithValue("@value", value ?? "");
            cmd.Parameters.AddWithValue("@normalizedValue", (object?)normalizedValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@isPrimary", isPrimary);
            count += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return count;
    }

    private async ValueTask<int> RestoreReleaseArtistCreditsAsync(
        string releaseId,
        JsonElement artistCreditsElem,
        CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (var creditElem in artistCreditsElem.EnumerateArray())
        {
            var artistId = creditElem.TryGetProperty("artistId", out var artistIdProp) ? artistIdProp.GetString() : "";
            var creditName = creditElem.TryGetProperty("creditName", out var creditNameProp) ? creditNameProp.GetString() : null;
            var position = creditElem.TryGetProperty("position", out var posProp) ? posProp.GetInt32() : (int?)null;

            const string query = """
                INSERT INTO release_artist_credit (release_id, artist_id, credit_name, position)
                VALUES (@releaseId, @artistId, @creditName, @position)
                """;
            using var cmd = new SqliteCommand(query, _connection) { Transaction = _transaction };
            cmd.Parameters.AddWithValue("@releaseId", releaseId);
            cmd.Parameters.AddWithValue("@artistId", artistId ?? "");
            cmd.Parameters.AddWithValue("@creditName", (object?)creditName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@position", (object?)position ?? DBNull.Value);
            count += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return count;
    }

    private async ValueTask<int> RestoreReleaseLabelLinksAsync(
        string releaseId,
        JsonElement labelLinksElem,
        CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (var linkElem in labelLinksElem.EnumerateArray())
        {
            var labelId = linkElem.TryGetProperty("labelId", out var labelIdProp) ? labelIdProp.GetString() : "";
            var isPrimary = linkElem.TryGetProperty("isPrimary", out var isPrimaryProp) ? (isPrimaryProp.GetBoolean() ? 1 : 0) : 0;
            var role = linkElem.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : null;

            const string query = """
                INSERT INTO release_label_link (release_id, label_id, is_primary, role)
                VALUES (@releaseId, @labelId, @isPrimary, @role)
                """;
            using var cmd = new SqliteCommand(query, _connection) { Transaction = _transaction };
            cmd.Parameters.AddWithValue("@releaseId", releaseId);
            cmd.Parameters.AddWithValue("@labelId", labelId ?? "");
            cmd.Parameters.AddWithValue("@isPrimary", isPrimary);
            cmd.Parameters.AddWithValue("@role", (object?)role ?? DBNull.Value);
            count += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return count;
    }

    private async ValueTask<int> RestoreReleaseRecordingsAsync(
        string releaseId,
        JsonElement recordingsElem,
        CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (var recElem in recordingsElem.EnumerateArray())
        {
            var recordingId = recElem.TryGetProperty("recordingId", out var recIdProp) ? recIdProp.GetString() : "";
            var discNumber = recElem.TryGetProperty("discNumber", out var discProp) ? recElem.GetInt32() : (int?)null;
            var trackNumber = recElem.TryGetProperty("trackNumber", out var trackProp) ? trackProp.GetInt32() : (int?)null;

            const string query = """
                INSERT INTO release_recording (release_id, recording_id, disc_number, track_number)
                VALUES (@releaseId, @recordingId, @discNumber, @trackNumber)
                """;
            using var cmd = new SqliteCommand(query, _connection) { Transaction = _transaction };
            cmd.Parameters.AddWithValue("@releaseId", releaseId);
            cmd.Parameters.AddWithValue("@recordingId", recordingId ?? "");
            cmd.Parameters.AddWithValue("@discNumber", (object?)discNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@trackNumber", (object?)trackNumber ?? DBNull.Value);
            count += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return count;
    }

    private async ValueTask<int> RestoreRecordingArtistCreditsAsync(
        string recordingId,
        JsonElement artistCreditsElem,
        CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (var creditElem in artistCreditsElem.EnumerateArray())
        {
            var artistId = creditElem.TryGetProperty("artistId", out var artistIdProp) ? artistIdProp.GetString() : "";
            var creditName = creditElem.TryGetProperty("creditName", out var creditNameProp) ? creditNameProp.GetString() : null;
            var role = creditElem.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : null;
            var position = creditElem.TryGetProperty("position", out var posProp) ? posProp.GetInt32() : (int?)null;

            const string query = """
                INSERT INTO recording_artist_credit (recording_id, artist_id, credit_name, role, position)
                VALUES (@recordingId, @artistId, @creditName, @role, @position)
                """;
            using var cmd = new SqliteCommand(query, _connection) { Transaction = _transaction };
            cmd.Parameters.AddWithValue("@recordingId", recordingId);
            cmd.Parameters.AddWithValue("@artistId", artistId ?? "");
            cmd.Parameters.AddWithValue("@creditName", (object?)creditName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@role", (object?)role ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@position", (object?)position ?? DBNull.Value);
            count += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return count;
    }

    private async ValueTask<int> RestoreRecordingReleaseLinksAsync(
        string recordingId,
        JsonElement releaseLinksElem,
        CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (var linkElem in releaseLinksElem.EnumerateArray())
        {
            var releaseId = linkElem.TryGetProperty("releaseId", out var relIdProp) ? relIdProp.GetString() : "";

            const string query = """
                INSERT INTO release_recording (release_id, recording_id)
                VALUES (@releaseId, @recordingId)
                ON CONFLICT DO NOTHING
                """;
            using var cmd = new SqliteCommand(query, _connection) { Transaction = _transaction };
            cmd.Parameters.AddWithValue("@releaseId", releaseId ?? "");
            cmd.Parameters.AddWithValue("@recordingId", recordingId);
            count += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return count;
    }

    private async ValueTask<int> RestoreRecordingRelationshipsAsync(
        string recordingId,
        JsonElement relationshipsElem,
        CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (var relElem in relationshipsElem.EnumerateArray())
        {
            var toRecordingId = relElem.TryGetProperty("toRecordingId", out var toIdProp) ? toIdProp.GetString() : "";
            var relationshipType = relElem.TryGetProperty("relationshipType", out var typeProp) ? typeProp.GetString() : "";
            var source = relElem.TryGetProperty("source", out var sourceProp) ? sourceProp.GetString() : null;
            var confidence = relElem.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : (double?)null;
            var notes = relElem.TryGetProperty("notes", out var notesProp) ? notesProp.GetString() : null;
            var now = DateTimeOffset.UtcNow.ToString("O");

            const string query = """
                INSERT INTO recording_relationship (from_recording_id, to_recording_id, relationship_type, source, confidence, notes, created_utc, updated_utc)
                VALUES (@fromId, @toId, @type, @source, @confidence, @notes, @now, @now)
                """;
            using var cmd = new SqliteCommand(query, _connection) { Transaction = _transaction };
            cmd.Parameters.AddWithValue("@fromId", recordingId);
            cmd.Parameters.AddWithValue("@toId", toRecordingId ?? "");
            cmd.Parameters.AddWithValue("@type", relationshipType ?? "");
            cmd.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@confidence", (object?)confidence ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notes", (object?)notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@now", now);
            count += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return count;
    }

}
