namespace TrackStash.Core.Storage;

public sealed record LabelWebLink
{
    public required string Kind { get; init; }
    public required string Url { get; init; }
    public string? Title { get; init; }
    public string? SourceUrl { get; init; }
}

public sealed record LabelProfile
{
    public const int MaximumLinks = 100;
    public string? Description { get; init; }
    public string? CountryCode { get; init; }
    public string? City { get; init; }
    public int? FoundedYear { get; init; }
    public string LifecycleStatus { get; init; } = "unknown";
    public string? Notes { get; init; }
    public bool Favourite { get; init; }
    public string ReviewStatus { get; init; } = "unreviewed";
    public IReadOnlyList<LabelWebLink> Links { get; init; } = [];

    private static readonly string[] LinkKinds = ["official", "bandcamp", "discogs", "musicbrainz", "resident_advisor", "beatport", "social", "other"];
    private static readonly string[] LifecycleStatuses = ["unknown", "active", "inactive", "closed"];
    private static readonly string[] ReviewStatuses = ["unreviewed", "in_progress", "reviewed"];

    public void Validate()
    {
        Length(Description, 12000, nameof(Description));
        Length(City, 200, nameof(City));
        Length(Notes, 12000, nameof(Notes));
        if (CountryCode is not null && (CountryCode.Length != 2 || CountryCode.Any(c => c is < 'A' or > 'Z')))
            throw new ArgumentException("Country code must have two uppercase letters.", nameof(CountryCode));
        if (FoundedYear is < 1 or > 9999) throw new ArgumentException("Founded year must be between 1 and 9999.", nameof(FoundedYear));
        if (!LifecycleStatuses.Contains(LifecycleStatus, StringComparer.Ordinal))
            throw new ArgumentException("Use unknown, active, inactive, or closed.", nameof(LifecycleStatus));
        if (!ReviewStatuses.Contains(ReviewStatus, StringComparer.Ordinal))
            throw new ArgumentException("Use unreviewed, in_progress, or reviewed.", nameof(ReviewStatus));
        if (Links is null || Links.Count > MaximumLinks) throw new ArgumentException($"At most {MaximumLinks} links are supported.", nameof(Links));
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in Links)
        {
            ArgumentNullException.ThrowIfNull(link);
            if (!LinkKinds.Contains(link.Kind, StringComparer.Ordinal)) throw new ArgumentException("Unknown link kind.", nameof(Links));
            ValidateUrl(link.Url, nameof(Links));
            if (!urls.Add(link.Url)) throw new ArgumentException("Duplicate link URLs are not allowed.", nameof(Links));
            Length(link.Title, 200, nameof(Links));
            if (link.SourceUrl is not null) ValidateUrl(link.SourceUrl, nameof(Links));
        }
    }

    public static void ValidateUrl(string url, string field)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || url != url.Trim()
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Use an absolute HTTP(S) URL without embedded credentials.", field);
    }

    private static void Length(string? value, int max, string field)
    {
        if (value?.Length > max) throw new ArgumentException($"Value exceeds {max} characters.", field);
    }
}
