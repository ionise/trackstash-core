namespace TrackStash.Core.Storage;

public sealed record LabelIdentity(string RequestedId, string? CanonicalId)
{
    public bool IsRetired => CanonicalId is not null && CanonicalId != RequestedId;
}

public sealed class RetiredLabelException(string requestedId, string canonicalId)
    : InvalidOperationException($"Label '{requestedId}' is retired. Use canonical label '{canonicalId}' after review.")
{
    public string RequestedId { get; } = requestedId;
    public string CanonicalId { get; } = canonicalId;
}

public sealed record MergeLabelSummary(string Id, string? Name, string? SortName, string Version);
public sealed record MergeAlias(string Value, bool IsPrimary);
public sealed record MergeReference(string Source, string ExternalId, bool IsPrimary, DateTimeOffset? LastSeenUtc);
public sealed record MergeReleaseLink(string ReleaseId, bool IsPrimary, string? Role);
public sealed record LabelMergePreview(
    string OperationId, string Version, DateTimeOffset ExpiresUtc,
    MergeLabelSummary Survivor, MergeLabelSummary Retired,
    IReadOnlyList<MergeAlias> Aliases, IReadOnlyList<MergeReference> ExternalReferences,
    IReadOnlyList<MergeReleaseLink> ReleaseLinks, IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings, LabelProfile? Profile = null);
public sealed record LabelMergeOutcome(string OperationId, string AuditId, string CanonicalId, DateTimeOffset AppliedUtc);

public enum LabelMergeStatus { Ready, Applied, NotFound, Stale, Expired, Blocked, ReplayConflict }
public sealed record LabelMergeResult(LabelMergeStatus Status, LabelMergePreview? Preview = null, LabelMergeOutcome? Outcome = null);

/// <summary>Reviewed label operations within the caller's unit of work; apply never commits independently.</summary>
public interface ILabelMergeRepository
{
    ValueTask<LabelMergeResult> PreviewAsync(string survivorId, string retiredId, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    ValueTask<LabelMergeResult> GetPreviewAsync(string operationId, CancellationToken cancellationToken = default);
    ValueTask<LabelMergeResult> ApplyAsync(string operationId, string expectedVersion, string reason,
        string? operatorName, string principal, DateTimeOffset now, CancellationToken cancellationToken = default);
}
