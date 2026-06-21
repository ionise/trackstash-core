namespace TrackStash.Core.Storage;

/// <summary>
/// Represents a single blocking dependency that prevents an entity from being deleted.
/// </summary>
public sealed record DeleteBlocker
{
    /// <summary>
    /// The table that contains the blocking reference.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// The column(s) that reference the entity being deleted.
    /// </summary>
    public required string ColumnName { get; init; }

    /// <summary>
    /// The number of rows in this table that block the delete.
    /// </summary>
    public int RowCount { get; init; }

    /// <summary>
    /// Optional sample values or IDs from the blocking rows for reporting.
    /// </summary>
    public IReadOnlyList<string> SampleValues { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Human-readable reason why this is a blocker.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Represents an owned cleanup row that will be deleted when the parent entity is deleted.
/// </summary>
public sealed record OwnedCleanupTable
{
    /// <summary>
    /// The table that will be cleaned up.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// The number of rows that will be deleted.
    /// </summary>
    public int RowCount { get; init; }

    /// <summary>
    /// The column name that references the entity being deleted.
    /// </summary>
    public string? ReferencingColumn { get; init; }
}

/// <summary>
/// Result of analyzing dependencies before a delete operation.
/// </summary>
public sealed record DeleteAnalysisResult
{
    /// <summary>
    /// The entity type being analyzed for deletion (label, artist, release, recording).
    /// </summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// The entity ID being analyzed.
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// Blocking dependencies that prevent deletion.
    /// </summary>
    public IReadOnlyList<DeleteBlocker> Blockers { get; init; } = Array.Empty<DeleteBlocker>();

    /// <summary>
    /// Owned cleanup rows that will be deleted if blockers are cleared.
    /// </summary>
    public IReadOnlyList<OwnedCleanupTable> OwnedCleanupRows { get; init; } = Array.Empty<OwnedCleanupTable>();

    /// <summary>
    /// Whether the entity is safe to delete (no blockers remain).
    /// </summary>
    public bool IsSafeToDelete => Blockers.Count == 0;

    /// <summary>
    /// Total owned rows that will be cleaned up.
    /// </summary>
    public int TotalOwnedRowsToDelete => OwnedCleanupRows.Sum(t => t.RowCount);
}

/// <summary>
/// Represents a tombstone record of a deleted entity, captured for the recycle bin.
/// </summary>
public sealed record EntityTombstone
{
    /// <summary>
    /// The entity type that was deleted.
    /// </summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// The entity ID that was deleted.
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// The original entity data serialized as JSON.
    /// </summary>
    public required string EntityDataJson { get; init; }

    /// <summary>
    /// Who or what initiated the delete (optional).
    /// </summary>
    public string? DeletedBy { get; init; }

    /// <summary>
    /// The reason for deletion (optional).
    /// </summary>
    public string? DeleteReason { get; init; }

    /// <summary>
    /// When the entity was deleted.
    /// </summary>
    public DateTimeOffset DeletedUtc { get; init; }

    /// <summary>
    /// Expiration date after which this tombstone can be purged (optional).
    /// </summary>
    public DateTimeOffset? PurgeAfterUtc { get; init; }

    /// <summary>
    /// Whether this record has been permanently purged from the recycle bin.
    /// </summary>
    public bool IsPurged { get; init; }
}

/// <summary>
/// Result of executing a delete operation.
/// </summary>
public sealed record DeleteExecutionResult
{
    /// <summary>
    /// The entity type that was deleted.
    /// </summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// The entity ID that was deleted.
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// Whether the delete succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the delete failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Number of owned cleanup rows that were deleted.
    /// </summary>
    public int CleanupRowsDeleted { get; init; }

    /// <summary>
    /// The tombstone record created for the recycle bin.
    /// </summary>
    public EntityTombstone? Tombstone { get; init; }

    /// <summary>
    /// When the delete was executed.
    /// </summary>
    public DateTimeOffset ExecutedUtc { get; init; }
}

/// <summary>
/// Result of analyzing whether an entity can be restored from the recycle bin.
/// </summary>
public sealed record RestoreAnalysisResult
{
    /// <summary>
    /// The tombstone being analyzed.
    /// </summary>
    public required EntityTombstone Tombstone { get; init; }

    /// <summary>
    /// Whether the entity is safe to restore without conflicts.
    /// </summary>
    public bool IsSafeToRestore { get; init; }

    /// <summary>
    /// Conflicts that would prevent restore (e.g., entity ID already exists).
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Prerequisites the caller must satisfy first (e.g., related entities must be restored first).
    /// </summary>
    public IReadOnlyList<string> Prerequisites { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Tables that will be repopulated (entity + owned cleanup rows).
    /// </summary>
    public IReadOnlyList<string> TablesToRestore { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Derived data that should be rebuilt after restore (matches, embeddings).
    /// </summary>
    public IReadOnlyList<string> DerivedDataToRebuild { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Result of executing a restore operation.
/// </summary>
public sealed record RestoreExecutionResult
{
    /// <summary>
    /// The tombstone that was restored.
    /// </summary>
    public required EntityTombstone RestoredTombstone { get; init; }

    /// <summary>
    /// Whether the restore succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the restore failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Number of owned cleanup rows that were restored.
    /// </summary>
    public int CleanupRowsRestored { get; init; }

    /// <summary>
    /// When the restore was executed.
    /// </summary>
    public DateTimeOffset RestoredUtc { get; init; }

    /// <summary>
    /// Reminders about derived data that should be rebuilt.
    /// </summary>
    public IReadOnlyList<string> RebuildReminders { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Service contract for safe entity deletion and recycle-bin management.
/// </summary>
public interface IEntityDeleteService
{
    /// <summary>
    /// Analyze dependencies for a potential delete without making changes.
    /// </summary>
    ValueTask<DeleteAnalysisResult> AnalyzeDependenciesAsync(
        string entityType,
        string entityId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes only owned child rows for an entity (aliases, external refs, links) without deleting the entity row.
    /// Intended for replace-style upserts where child collections should be reset before re-applying.
    /// </summary>
    ValueTask<int> DeleteOwnedRowsAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a safe delete if no blockers remain.
    /// Captures a tombstone to the recycle bin in the same transaction.
    /// </summary>
    ValueTask<DeleteExecutionResult> DeleteEntityAsync(
        string entityType,
        string entityId,
        string? deletedBy = null,
        string? deleteReason = null,
        DateTimeOffset? purgeAfterUtc = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a deleted entity from the recycle bin.
    /// </summary>
    ValueTask<EntityTombstone?> GetTombstoneAsync(
        string entityType,
        string entityId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List tombstones in the recycle bin by entity type.
    /// </summary>
    ValueTask<IReadOnlyList<EntityTombstone>> ListTombstonesAsync(
        string entityType,
        int pageSize = 100,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze restore prerequisites before attempting to restore.
    /// </summary>
    ValueTask<RestoreAnalysisResult> AnalyzeRestoreAsync(
        EntityTombstone tombstone,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restore a deleted entity from the recycle bin.
    /// </summary>
    ValueTask<RestoreExecutionResult> RestoreEntityAsync(
        EntityTombstone tombstone,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently purge a tombstone from the recycle bin.
    /// </summary>
    ValueTask<bool> PurgeTombstoneAsync(
        string entityType,
        string entityId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default);
}
