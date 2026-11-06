namespace TrackStash.Core.Storage;

public enum CatalogEntityKind { Artist, Release, Recording }
public sealed record CatalogEntityMetadata(
    string EntityId, CatalogEntityKind Kind, string Version,
    string ArtistKind = "unknown", string IdentityStatus = "unknown",
    string? CatalogNumber = null, string? Upc = null, string? PublishDate = null,
    string? NewReleaseDate = null, long? DurationMs = null);

public sealed record CatalogCredit(string Id, string ArtistId, string Role, int? Position,
    string? CreditedName, string? Source, string? SourcePath, string? SourceExpression, string? EvidenceJson);

public sealed record ReleasePlacement(string Id, string ReleaseId, string RecordingId, string Version,
    int DisplayOrder, int? SourcePosition, string OrderSource, int? DiscNumber, int? TrackNumber,
    IReadOnlyList<CatalogCredit> Credits, IReadOnlyList<EntityReference> ExternalReferences);

public sealed record ExternalReferenceOwner(string ExternalId, string OwnerId);

public sealed class CatalogFoundationValidationException(string message) : Exception(message);
public sealed class CatalogFoundationConflictException(string message) : Exception(message);
public sealed class CatalogFoundationStaleException(string message) : Exception(message);

/// <summary>Conditional metadata, stable credits and placements for reviewed catalog workflows.</summary>
public interface ICatalogFoundationRepository
{
    ValueTask SaveEntityAsync(CanonicalEntity entity, string? expectedVersion, CancellationToken cancellationToken = default);
    ValueTask<CatalogEntityMetadata?> GetMetadataAsync(CatalogEntityKind kind, string id, CancellationToken cancellationToken = default);
    ValueTask<CatalogEntityMetadata> UpdateMetadataAsync(CatalogEntityMetadata metadata, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<string>> FindReferenceOwnersAsync(CatalogEntityKind kind, string source, string externalId, CancellationToken cancellationToken = default);
    /// <summary>Lists every external ID from one source with its owning entity, ordered by external ID then owner.</summary>
    ValueTask<IReadOnlyList<ExternalReferenceOwner>> ListReferenceOwnersAsync(CatalogEntityKind kind, string source, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<CatalogCredit>> GetCreditsAsync(CatalogEntityKind kind, string id, CancellationToken cancellationToken = default);
    ValueTask SaveCreditAsync(CatalogEntityKind kind, string entityId, string expectedVersion, CatalogCredit credit, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ReleasePlacement>> GetPlacementsAsync(string releaseId, CancellationToken cancellationToken = default);
    ValueTask SavePlacementAsync(ReleasePlacement placement, string expectedReleaseVersion, CancellationToken cancellationToken = default);
    ValueTask ReorderPlacementsAsync(string releaseId, string expectedVersion, IReadOnlyList<string> orderedPlacementIds, CancellationToken cancellationToken = default);
}
