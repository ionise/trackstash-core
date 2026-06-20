using TrackStash.Core.Storage;

namespace TrackStash.Core.Services;

public enum EmbeddingChangeOperation
{
    Created,
    Updated,
    Deleted,
    Restored,
}

public sealed record EmbeddingModelIdentity(
    string ModelName,
    string ModelVersion);

public sealed record EmbeddingEntityChange(
    string EntityId,
    string EntityType,
    EmbeddingChangeOperation Operation,
    DateTimeOffset OccurredUtc,
    string? Trigger = null,
    bool ForceRegenerate = false,
    EmbeddingModelIdentity? PreferredModel = null);

public sealed record EmbeddingSourceDocument(
    string EntityId,
    string EntityType,
    string DocumentText,
    string DocumentHash,
    string? SourcePayloadJson = null);

public sealed record EmbeddingVector(
    int Dimensions,
    IReadOnlyList<float> Values,
    string? RawVectorFormat = null,
    byte[]? RawVectorBytes = null);

public enum EmbeddingJobStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
}

public sealed record EmbeddingWorkItem(
    string WorkItemId,
    string EntityId,
    string EntityType,
    EmbeddingModelIdentity Model,
    EmbeddingChangeOperation Operation,
    bool ForceRegenerate,
    int AttemptCount,
    DateTimeOffset EnqueuedUtc,
    DateTimeOffset? NextAttemptUtc = null,
    string? LastError = null);

public sealed record EmbeddingEnqueueResult(
    bool Enqueued,
    bool Deduplicated,
    string WorkItemId,
    string? Message = null);

public sealed record EmbeddingWorkResult(
    string WorkItemId,
    bool Success,
    bool Skipped,
    string? Message = null,
    string? Error = null);

/// <summary>
/// Accepts mutation notifications from canonical write paths.
/// Implementations should enqueue work and return quickly.
/// </summary>
public interface IEmbeddingOrchestrator
{
    ValueTask<EmbeddingEnqueueResult> QueueEntityChangedAsync(
        EmbeddingEntityChange change,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistence contract for asynchronous embedding work.
/// This is intentionally provider-agnostic and may be implemented with SQLite,
/// SQL Server, Postgres, or an external queue service.
/// </summary>
public interface IEmbeddingWorkItemStore
{
    ValueTask<EmbeddingEnqueueResult> EnqueueAsync(
        EmbeddingEntityChange change,
        EmbeddingModelIdentity model,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<EmbeddingWorkItem>> LeasePendingAsync(
        int maxItems,
        TimeSpan leaseDuration,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    ValueTask MarkCompletedAsync(
        string workItemId,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken = default);

    ValueTask MarkFailedAsync(
        string workItemId,
        string error,
        int nextAttemptInSeconds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds deterministic embedding source documents from canonical entities.
/// The returned DocumentHash must be stable for semantically equivalent content.
/// </summary>
public interface IEmbeddingDocumentBuilder
{
    ValueTask<EmbeddingSourceDocument> BuildAsync(
        CanonicalEntity entity,
        EmbeddingModelIdentity model,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls an embedding model implementation (for example MiniLM) and returns vector output.
/// </summary>
public interface IEmbeddingModelClient
{
    ValueTask<EmbeddingVector> GenerateAsync(
        EmbeddingSourceDocument source,
        EmbeddingModelIdentity model,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Background worker contract that processes queued embedding jobs.
/// </summary>
public interface IEmbeddingWorkProcessor
{
    ValueTask<IReadOnlyList<EmbeddingWorkResult>> ProcessBatchAsync(
        int maxItems,
        CancellationToken cancellationToken = default);
}
