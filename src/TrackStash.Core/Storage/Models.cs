namespace TrackStash.Core.Storage;

public abstract record CanonicalEntity
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? NormalizedName { get; init; }

    public string? SourcePayloadJson { get; init; }

    public DateTimeOffset? CreatedUtc { get; init; }

    public DateTimeOffset? UpdatedUtc { get; init; }

    public IReadOnlyList<EntityReference> ExternalReferences { get; init; } = Array.Empty<EntityReference>();

    public IReadOnlyList<EntityAlias> Aliases { get; init; } = Array.Empty<EntityAlias>();
}

public sealed record Label : CanonicalEntity
{
    public string? SortName { get; init; }
}

public sealed record Artist : CanonicalEntity
{
    public string? SortName { get; init; }

    public IReadOnlyList<EntityRelationship> Relationships { get; init; } = Array.Empty<EntityRelationship>();
}

public sealed record Release : CanonicalEntity
{
    public string? Title { get; init; }

    public IReadOnlyList<ReleaseArtistCredit> ArtistCredits { get; init; } = Array.Empty<ReleaseArtistCredit>();

    public IReadOnlyList<ReleaseLabelLink> LabelLinks { get; init; } = Array.Empty<ReleaseLabelLink>();
}

public sealed record Recording : CanonicalEntity
{
    public string? Title { get; init; }

    public string? MixName { get; init; }

    public string? Isrc { get; init; }

    public IReadOnlyList<RecordingArtistCredit> ArtistCredits { get; init; } = Array.Empty<RecordingArtistCredit>();

    public IReadOnlyList<RecordingReleaseLink> ReleaseLinks { get; init; } = Array.Empty<RecordingReleaseLink>();

    public IReadOnlyList<RecordingRelationship> Relationships { get; init; } = Array.Empty<RecordingRelationship>();
}

public sealed record MediaFile
{
    public required string Id { get; init; }

    public required string Path { get; init; }

    public string? NormalizedPath { get; init; }

    public string? ContentHash { get; init; }

    public string? MetadataJson { get; init; }

    public string? Fingerprint { get; init; }

    public string? SourcePayloadJson { get; init; }

    public DateTimeOffset? CreatedUtc { get; init; }

    public DateTimeOffset? UpdatedUtc { get; init; }
}

public sealed record MatchRecord
{
    public required string MediaFileId { get; init; }

    public required string RecordingId { get; init; }

    public MatchOverrideState OverrideState { get; init; } = MatchOverrideState.None;

    public decimal Score { get; init; }

    public decimal Confidence { get; init; }

    public string? EvidenceJson { get; init; }

    public DateTimeOffset? CreatedUtc { get; init; }

    public DateTimeOffset? UpdatedUtc { get; init; }
}

public sealed record MatchCandidate
{
    public required string RecordingId { get; init; }

    public int Rank { get; init; }

    public decimal Score { get; init; }

    public decimal Confidence { get; init; }

    public string? EvidenceJson { get; init; }
}

public sealed record EmbeddingDocument
{
    public required string EntityId { get; init; }

    public required string EntityType { get; init; }

    public required string ModelName { get; init; }

    public required string ModelVersion { get; init; }

    public int Dimensions { get; init; }

    public required string DocumentHash { get; init; }

    public string? DocumentText { get; init; }

    public byte[]? VectorData { get; init; }

    public string? SourcePayloadJson { get; init; }

    public DateTimeOffset? CreatedUtc { get; init; }

    public DateTimeOffset? UpdatedUtc { get; init; }
}

public sealed record EntityReference
{
    public required string Source { get; init; }

    public required string ExternalId { get; init; }

    public bool IsPrimary { get; init; }

    public DateTimeOffset? LastSeenUtc { get; init; }

    public string? PayloadJson { get; init; }
}

public sealed record EntityAlias
{
    public required string Value { get; init; }

    public string? NormalizedValue { get; init; }

    public bool IsPrimary { get; init; }
}

public sealed record EntityRelationship
{
    public required string RelatedEntityId { get; init; }

    public required string RelationshipType { get; init; }

    public string? PayloadJson { get; init; }

    public DateTimeOffset? CreatedUtc { get; init; }
}

public sealed record ReleaseArtistCredit
{
    public required string ArtistId { get; init; }

    public string? CreditName { get; init; }

    public int? Position { get; init; }
}

public sealed record ReleaseLabelLink
{
    public required string LabelId { get; init; }

    public bool IsPrimary { get; init; }

    public string? Role { get; init; }
}

public sealed record RecordingArtistCredit
{
    public required string ArtistId { get; init; }

    public string? CreditName { get; init; }

    public string? Role { get; init; }

    public int? Position { get; init; }
}

public sealed record RecordingReleaseLink
{
    public required string ReleaseId { get; init; }

    public int? DiscNumber { get; init; }

    public int? TrackNumber { get; init; }
}

public sealed record RecordingRelationship
{
    public required string RelatedRecordingId { get; init; }

    public required string RelationshipType { get; init; }

    public string? Source { get; init; }

    public decimal? Confidence { get; init; }

    public string? Notes { get; init; }

    public DateTimeOffset? CreatedUtc { get; init; }

    public DateTimeOffset? UpdatedUtc { get; init; }
}

public sealed record StorageCapabilities
{
    public bool SupportsTransactions { get; init; } = true;

    public bool SupportsCaseInsensitiveSearch { get; init; }

    public bool SupportsBinaryVectorStorage { get; init; }

    public bool SupportsJsonPayloadStorage { get; init; } = true;

    public bool SupportsIndexedExternalRefs { get; init; } = true;
}

public sealed record MigrationResult
{
    public int CurrentVersion { get; init; }

    public IReadOnlyList<string> AppliedMigrations { get; init; } = Array.Empty<string>();

    public bool WasSuccessful { get; init; } = true;

    public string? Message { get; init; }
}

public enum MatchOverrideState
{
    None = 0,
    Accepted = 1,
    Rejected = 2,
    Manual = 3,
}
