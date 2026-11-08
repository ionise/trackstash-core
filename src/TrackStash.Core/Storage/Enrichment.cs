namespace TrackStash.Core.Storage;

/// <summary>Canonical musical metadata, distinct from provider evidence and audio analysis.</summary>
public sealed record CatalogMusicalMetadata(
    decimal? Bpm = null, string? KeyValue = null, string? KeyNotation = null,
    IReadOnlyList<string>? Genres = null, IReadOnlyList<string>? Subgenres = null, string? PreferredGenre = null);

/// <summary>A frozen, server-owned review; payload belongs to the Catalog application.</summary>
public sealed record StoredEnrichmentReview(string Id, CatalogEntityKind Kind, string EntityId,
    string Version, string PayloadJson, DateTimeOffset CreatedAt);

/// <summary>Permanent provenance and before/after history for one reviewed apply.</summary>
public sealed record EnrichmentApplyAudit(string ReviewId, string FieldsJson, string Reason,
    string BeforeJson, string AfterJson, DateTimeOffset AppliedAt);

public interface IEnrichmentRepository
{
    /// <summary>Read applied typed application reviews, with a 1001-row overflow sentinel.</summary>
    ValueTask<IReadOnlyList<StoredEnrichmentReview>> ListAppliedReviewsAsync(string payloadType, CancellationToken ct = default)
        => throw new NotSupportedException("Typed applied review listing is not supported.");
    /// <summary>Update scalar identity metadata and add references, preserving all credits, aliases and placements.</summary>
    ValueTask UpdateEntityAsync(CanonicalEntity entity, string expectedVersion, CancellationToken ct = default);
    ValueTask<CatalogMusicalMetadata> GetMetadataAsync(CatalogEntityKind kind, string id, CancellationToken ct = default);
    ValueTask SaveMetadataAsync(CatalogEntityKind kind, string id, CatalogMusicalMetadata metadata, CancellationToken ct = default);
    ValueTask SaveReviewAsync(StoredEnrichmentReview review, CancellationToken ct = default);
    ValueTask<StoredEnrichmentReview?> GetReviewAsync(string id, CancellationToken ct = default);
    ValueTask<EnrichmentApplyAudit?> GetAuditAsync(string reviewId, CancellationToken ct = default);
    ValueTask SaveAuditAsync(EnrichmentApplyAudit audit, CancellationToken ct = default);
}
