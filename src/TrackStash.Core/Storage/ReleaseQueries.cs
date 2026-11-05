namespace TrackStash.Core.Storage;

public sealed record ReleaseQuery(int Page = 1, int PageSize = 25, string? Search = null, string? LabelId = null)
{
    public void Validate()
    {
        if (Page < 1 || PageSize is < 1 or > 100 || Search?.Length > 200 || LabelId?.Length > 200 ||
            LabelId is not null && string.IsNullOrWhiteSpace(LabelId))
            throw new ReleaseQueryValidationException("Use page >=1, pageSize 1-100, search up to 200 characters and a nonempty label ID.");
    }
}
public sealed record CatalogReleaseSummary(string Id, string? Title, string? CatalogNumber, string? PublishDate,
    string? LabelId, string? LabelName, int PlacementCount);
public sealed record CatalogReleasePage(IReadOnlyList<CatalogReleaseSummary> Items, int Total, int Page, int PageSize);
public sealed class ReleaseQueryValidationException(string message) : Exception(message);
