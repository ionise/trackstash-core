namespace TrackStash.Core.Storage;

public enum LabelSortField { Name, SortName, Id }

public sealed record LabelQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Search { get; init; }
    public string? NormalizedName { get; init; }
    public string? Source { get; init; }
    public string? ExternalId { get; init; }
    public LabelSortField SortBy { get; init; } = LabelSortField.Name;
    public bool Descending { get; init; }

    public void Validate()
    {
        if (Page < 1) throw new ArgumentOutOfRangeException(nameof(Page));
        if (PageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (!Enum.IsDefined(SortBy)) throw new ArgumentOutOfRangeException(nameof(SortBy));
        if (Source is not null && string.IsNullOrWhiteSpace(Source))
            throw new ArgumentException("Source cannot be blank.", nameof(Source));
        if (ExternalId is not null && string.IsNullOrWhiteSpace(ExternalId))
            throw new ArgumentException("ExternalId cannot be blank.", nameof(ExternalId));
        if (NormalizedName is not null && string.IsNullOrWhiteSpace(NormalizedName))
            throw new ArgumentException("NormalizedName cannot be blank.", nameof(NormalizedName));
        if (ExternalId is not null && string.IsNullOrWhiteSpace(Source))
            throw new ArgumentException("ExternalId requires Source.", nameof(Source));
        if (Search is not null && Normalization.EntityNameNormalizer.NormalizeStrict(Search).Length == 0)
            throw new ArgumentException("Search must contain searchable letters or digits.", nameof(Search));
    }
}

public sealed record LabelListItem(string Id, string? Name, string? SortName, string Version);

public sealed record LabelPage(IReadOnlyList<LabelListItem> Items, int Total, int Page, int PageSize);

public sealed record LabelUpdate
{
    public bool SetName { get; init; }
    public string? Name { get; init; }
    public bool SetSortName { get; init; }
    public string? SortName { get; init; }

    public void Validate()
    {
        if (!SetName && !SetSortName)
            throw new ArgumentException("At least one label field must be supplied.");
        if (SetName && string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Name cannot be null or blank.", nameof(Name));
        if ((!SetName && Name is not null) || (!SetSortName && SortName is not null))
            throw new ArgumentException("A field value requires its corresponding Set flag.");
    }
}

public enum LabelUpdateStatus { Updated, NotFound, Stale }

public sealed record LabelUpdateResult(LabelUpdateStatus Status, Label? Label = null);
