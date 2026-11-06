namespace TrackStash.Core.Storage;

/// <summary>
/// One query over tracks (release placements plus unplaced Recordings) shared by every track list.
/// Filters combine; <see cref="GroupByRecording"/> collapses placements to one row per Recording.
/// </summary>
public sealed record TrackQuery(int Page = 1, int PageSize = 25, string? Search = null, string? ReleaseId = null,
    string? LabelId = null, string? ArtistId = null, string? Role = null, string? RecordingId = null,
    bool GroupByRecording = false, string? Sort = null, bool? Descending = null)
{
    public static readonly IReadOnlyList<string> Sorts = ["date", "title", "artist", "release", "order"];

    /// <summary>Release lists default to release order; every other list defaults to newest release first.</summary>
    public string EffectiveSort => Sort ?? (ReleaseId is not null ? "order" : "date");
    public bool EffectiveDescending => Descending ?? EffectiveSort == "date";

    public void Validate()
    {
        new ReleaseQuery(Page, PageSize, Search, LabelId).Validate();
        foreach (var id in new[] { ReleaseId, ArtistId, RecordingId })
            if (id is not null && (string.IsNullOrWhiteSpace(id) || id.Length > 200))
                throw new ReleaseQueryValidationException("Track filter IDs must be nonempty and at most 200 characters.");
        if (Role is not null && Role is not ("primary" or "remixer"))
            throw new ReleaseQueryValidationException("Track role filter must be primary or remixer.");
        if (Role is not null && ArtistId is null)
            throw new ReleaseQueryValidationException("Track role filter requires an Artist filter.");
        if (Sort is not null && !Sorts.Contains(Sort))
            throw new ReleaseQueryValidationException($"Track sort must be one of {string.Join(", ", Sorts)}.");
        if (Sort == "order" && ReleaseId is null)
            throw new ReleaseQueryValidationException("Track order sort requires a release filter.");
    }
}

/// <summary>A track row: a placement on a release, or an unplaced Recording (null placement and release).</summary>
public sealed record TrackListing(string? PlacementId, string RecordingId, string? Title, string? MixName, string? Isrc,
    long? DurationMs, string? ReleaseId, string? ReleaseTitle, string? PublishDate, string? NewReleaseDate,
    int? DisplayOrder, int? SourcePosition, string? OrderSource, int? DiscNumber, int? TrackNumber, int ReleaseCount);
public sealed record TrackListingPage(IReadOnlyList<TrackListing> Items, int Total, int Page, int PageSize);
