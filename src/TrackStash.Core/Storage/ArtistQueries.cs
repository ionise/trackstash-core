namespace TrackStash.Core.Storage;

public sealed record ArtistQuery(int Page = 1, int PageSize = 25, string? Search = null, string? LabelId = null, string? Role = null)
{
    public void Validate()
    {
        new ReleaseQuery(Page, PageSize, Search, LabelId).Validate();
        if (Role is not null && Role is not ("primary" or "remixer"))
            throw new ReleaseQueryValidationException("Artist role filter must be primary or remixer.");
    }
}
public sealed record CatalogArtistSummary(string Id, string? Name, string Kind, string IdentityStatus, IReadOnlyList<string> Roles);
public sealed record CatalogArtistPage(IReadOnlyList<CatalogArtistSummary> Items, int Total, int Page, int PageSize);
public sealed record ArtistCreditQuery(string ArtistId, bool Tracks, int Page = 1, int PageSize = 25);
public sealed record ArtistCreditListing(string CreditId, string? ReleaseId, string? ReleaseTitle, string? RecordingId,
    string? PlacementId, string? TrackTitle, string? MixName, string Role, string Scope, string? CreditedName, int? Position);
public sealed record ArtistCreditPage(IReadOnlyList<ArtistCreditListing> Items, int Total, int Page, int PageSize);
public sealed record ArtistAppearanceCredit(string Role, string Scope, string? CreditedName);
public sealed record ArtistAppearanceLabel(string Id, string? Name, bool IsPrimary);
public sealed record ArtistAppearanceListing(string? ReleaseId, string? ReleaseTitle, string? RecordingId,
    string? PlacementId, string? TrackTitle, string? MixName, string? CoverAssetId,
    IReadOnlyList<ArtistAppearanceLabel> Labels, IReadOnlyList<ArtistAppearanceCredit> Credits);
public sealed record ArtistAppearancePage(IReadOnlyList<ArtistAppearanceListing> Items, int Total, int Page, int PageSize);
