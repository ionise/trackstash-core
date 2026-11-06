namespace TrackStash.Core.Storage;

public sealed record ReleaseImportDecision(string Kind, long ProviderId, string Action, string? CanonicalId = null,
    string? Evidence = null);
/// <param name="BeatportIds">Beatport IDs already bound to the candidate (for Recordings, including its listings).</param>
public sealed record ReleaseImportCandidate(string Id, string? Name, string? MixName = null, string? Isrc = null,
    IReadOnlyList<string>? BeatportIds = null);
public sealed record ReleaseImportIdentity(string Kind, long ProviderId, string Name, string Action,
    string? CanonicalId, bool CompoundReview, IReadOnlyList<ReleaseImportCandidate> Candidates,
    string? MixName = null, string? Isrc = null);
public sealed record ReleaseImportArtistWrite(Artist? Before, Artist After);
public sealed record ReleaseImportRecordingWrite(Recording? Before, Recording After);
public sealed record ReleaseImportOutcome(string OperationId, string ReleaseId, DateTimeOffset AppliedUtc,
    bool NoOp, int CreatedArtists, int CreatedRecordings, int CreatedPlacements, string AuditId);
public sealed record ReleaseImportPlan(
    int FormatVersion, string OperationId, string Version, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc,
    string? BaseOperationId, string CatalogStamp, string SourceFingerprint, PurchaseReleaseInspection Source, string LabelId, Release? ExistingRelease, Release ProposedRelease,
    IReadOnlyList<ReleaseImportDecision> Decisions, IReadOnlyList<ReleaseImportIdentity> Identities,
    IReadOnlyList<ReleaseImportArtistWrite> Artists, IReadOnlyList<ReleaseImportRecordingWrite> Recordings,
    IReadOnlyList<ReleasePlacement> Placements, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings,
    bool NoOp);
public sealed record StoredReleaseImport(ReleaseImportPlan Plan, ReleaseImportOutcome? Outcome, string? SupersededBy = null);
public sealed record ReleaseImportBinding(string Source, string ExternalId, string ReleaseId, string SourceDigest, string SourceFingerprint);

public sealed class ReleaseImportException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Immutable review storage and transaction-enlisted application; never reads a provider or scanner.</summary>
public interface IReleaseImportRepository
{
    ValueTask<string> GetCatalogStampAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ReleaseImportCandidate>> FindCandidatesAsync(CatalogEntityKind kind, string normalizedName,
        string? isrc = null, CancellationToken cancellationToken = default);
    ValueTask<ReleaseImportBinding?> GetBindingAsync(string source, string externalId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<string>> FindPlacementOwnersAsync(string source, string externalId, CancellationToken cancellationToken = default);
    ValueTask SavePreviewAsync(ReleaseImportPlan plan, CancellationToken cancellationToken = default);
    ValueTask<StoredReleaseImport?> GetAsync(string operationId, CancellationToken cancellationToken = default);
    ValueTask<ReleaseImportOutcome> ApplyAsync(string operationId, string expectedVersion, string reason,
        DateTimeOffset now, CancellationToken cancellationToken = default);
}
