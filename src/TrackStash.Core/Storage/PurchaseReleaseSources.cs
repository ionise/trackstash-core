namespace TrackStash.Core.Storage;

public sealed record PurchaseReleaseQuery(int Page = 1, int PageSize = 25, string? Search = null)
{
    /// <summary>For explicit Catalog admission, inspect a cached non-corpus release without claiming a purchase. Does not widen corpus listings.</summary>
    public bool AllowCachedRelease { get; init; }
    /// <summary>Provider release IDs to omit before paging, such as releases already admitted to the catalog.</summary>
    public IReadOnlyCollection<long>? ExcludedReleaseIds { get; init; }
    /// <summary>When set, only these provider release IDs are eligible, applied before paging.</summary>
    public IReadOnlyCollection<long>? IncludedReleaseIds { get; init; }

    public void Validate()
    {
        if (Page < 1 || PageSize is < 1 or > 100 || Search?.Length > 200)
            throw new PurchaseSourceValidationException("Use page >= 1, pageSize between 1 and 100, and a search of at most 200 characters.");
    }
}

/// <summary>A paged source result; corpus flags do not prove purchase ownership.</summary>
public sealed record PurchaseSourcePage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
/// <summary>A derived purchase-corpus release candidate, not a receipt or canonical identity.</summary>
public sealed record PurchaseReleaseCandidate(long ReleaseId, string Title, string SourceKind, bool HasCachedRelease);
/// <summary>A source diagnostic, independent of canonical identity review.</summary>
public sealed record ReleaseSourceDiagnostic(string Code, string Severity, string Message, int? SourcePosition = null);
/// <summary>An individual source object and its credit role; no punctuation-based identity inference.</summary>
public sealed record SourceArtistCredit(long ProviderId, string Name, string Role, int Position);
/// <summary>A provider label observation, not an ownership or imprint assertion.</summary>
public sealed record SourceReleaseLabel(long ProviderId, string Name);
/// <summary>A hydrated listing or an explicit missing/invalid manifest item.</summary>
public sealed record SourceReleaseTrack(int SourcePosition, long? ProviderId, string Status, string? Title,
    string? MixName, string? Isrc, long? DurationMs, IReadOnlyList<SourceArtistCredit> Credits,
    DateTimeOffset? RetrievedUtc);
/// <summary>A complete source inspection with paged tracks and whole-bundle diagnostics/digest.</summary>
public sealed record PurchaseReleaseInspection(long ReleaseId, string SourceKind, string? Title,
    SourceReleaseLabel? Label, string? CatalogNumber, string? Upc, string? PublishDate, string? NewReleaseDate,
    DateTimeOffset? RetrievedUtc, string BundleDigest, bool SourceComplete, int? DeclaredTrackCount,
    int ManifestTrackCount, int HydratedTrackCount, IReadOnlyList<SourceArtistCredit> Credits,
    PurchaseSourcePage<SourceReleaseTrack> Tracks, IReadOnlyList<ReleaseSourceDiagnostic> Diagnostics);

public interface IPurchaseReleaseSource
{
    ValueTask<PurchaseSourcePage<PurchaseReleaseCandidate>> QueryAsync(PurchaseReleaseQuery query, CancellationToken cancellationToken = default);
    ValueTask<PurchaseReleaseInspection?> InspectAsync(long releaseId, PurchaseReleaseQuery query, CancellationToken cancellationToken = default);
    /// <summary>An opaque value that changes whenever the underlying source may have changed; used to invalidate derived summaries.</summary>
    ValueTask<string> GetSnapshotStampAsync(CancellationToken cancellationToken = default);
    /// <summary>Inspects every corpus release in one snapshot with the same rules as <see cref="InspectAsync"/>; track pages hold at most one item.</summary>
    ValueTask<IReadOnlyList<PurchaseReleaseInspection>> InspectAllAsync(CancellationToken cancellationToken = default);
}

public sealed class PurchaseSourceValidationException(string message) : Exception(message);
public sealed class PurchaseSourceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
