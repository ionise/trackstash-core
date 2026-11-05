namespace TrackStash.Core.Storage;

public sealed record ArtistProfile
{
    public string? Biography { get; init; }
    public string? BiographySourceUrl { get; init; }
    public bool Favourite { get; init; }
    public string? Notes { get; init; }
    public string ReviewStatus { get; init; } = "unreviewed";
    public IReadOnlyList<LabelWebLink> Links { get; init; } = [];
    public void Validate()
    {
        new LabelProfile { Description = Biography, Notes = Notes, Favourite = Favourite,
            ReviewStatus = ReviewStatus, Links = Links }.Validate();
        if (BiographySourceUrl is not null) LabelProfile.ValidateUrl(BiographySourceUrl, nameof(BiographySourceUrl));
    }
}
public sealed record ArtistProfileSnapshot(string ArtistId, string Version, ArtistProfile Profile);
public sealed record ArtistProfileResult(LabelUpdateStatus Status, ArtistProfileSnapshot? Snapshot = null);
