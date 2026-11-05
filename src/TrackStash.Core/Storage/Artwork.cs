namespace TrackStash.Core.Storage;

public sealed record ArtworkAsset(
    string Id, string ContentHash, string MediaType, long ByteLength, int? Width, int? Height,
    string? OriginalFilename, string? SourceUrl, string? Attribution, string UsageRights,
    DateTimeOffset CreatedUtc);

public sealed record ArtworkEntry(string Id, string AssetId, string Role, string? Caption, bool IsPreferred);
public sealed record ArtworkGallery(string LabelId, string Version, IReadOnlyList<ArtworkEntry> Items);
public sealed record ArtworkGalleryResult(LabelUpdateStatus Status, ArtworkGallery? Gallery = null);
public sealed record ReleaseArtworkGallery(string ReleaseId, string Version, IReadOnlyList<ArtworkEntry> Items);
public sealed record ReleaseArtworkGalleryResult(LabelUpdateStatus Status, ReleaseArtworkGallery? Gallery = null);
public sealed record ArtworkSourceIdentity(string Provider, string CandidateId, string Revision);

/// <summary>Shared asset metadata and label associations. Content is stored separately, never exposed as a filesystem path.</summary>
public interface IArtworkRepository
{
    ValueTask<ArtworkAsset?> GetAssetAsync(string assetId, CancellationToken ct = default);
    ValueTask AddAssetAsync(ArtworkAsset asset, CancellationToken ct = default);
    ValueTask<ArtworkGallery?> GetLabelGalleryAsync(string labelId, CancellationToken ct = default);
    ValueTask<ArtworkGalleryResult> ReplaceLabelGalleryAsync(string labelId, string expectedVersion,
        IReadOnlyList<ArtworkEntry> items, CancellationToken ct = default);
    ValueTask<ReleaseArtworkGallery?> GetReleaseGalleryAsync(string releaseId, CancellationToken ct = default)
        => throw new NotSupportedException("Release artwork is not supported by this provider.");
    ValueTask<ReleaseArtworkGalleryResult> ReplaceReleaseGalleryAsync(string releaseId, string expectedVersion,
        IReadOnlyList<ArtworkEntry> items, CancellationToken ct = default, ArtworkSourceIdentity? source = null)
        => throw new NotSupportedException("Release artwork is not supported by this provider.");
}

public static class ArtworkRules
{
    public const int MaximumItems = 100;
    public const int MaximumBytes = 10 * 1024 * 1024;
    public const long MaximumPixels = 25_000_000;
    public static void ValidateSource(ArtworkSourceIdentity? source)
    {
        if (source is not null && (string.IsNullOrWhiteSpace(source.Provider) || source.Provider.Length > 100 ||
            string.IsNullOrWhiteSpace(source.CandidateId) || source.CandidateId.Length > 200 ||
            string.IsNullOrWhiteSpace(source.Revision) || source.Revision.Length > 200))
            throw new ArgumentException("Artwork source identity exceeds its supported limits.", nameof(source));
    }

    public static void ValidateAsset(ArtworkAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ValidateAssetId(asset.Id);
        if (string.IsNullOrEmpty(asset.ContentHash) || asset.ContentHash.Length != 64 || asset.ContentHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
            || asset.MediaType is not ("image/jpeg" or "image/png") || asset.ByteLength is < 1 or > MaximumBytes
            || asset.Width is null or < 1 or > 16384 || asset.Height is null or < 1 or > 16384
            || (long)asset.Width * asset.Height > MaximumPixels)
            throw new ArgumentException("Asset hash, format, dimensions or size is invalid.", nameof(asset));
        if (string.IsNullOrWhiteSpace(asset.UsageRights) || asset.UsageRights.Length > 2000
            || asset.Attribution?.Length > 1000 || asset.OriginalFilename?.Length > 255)
            throw new ArgumentException("Asset provenance exceeds its supported limits.", nameof(asset));
        if (asset.SourceUrl is not null) LabelProfile.ValidateUrl(asset.SourceUrl, nameof(asset.SourceUrl));
    }

    public static void ValidateGallery(IReadOnlyList<ArtworkEntry> items, bool release = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Any(item => item is null) || items.Count > MaximumItems || items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != items.Count
            || items.Count(i => i.IsPreferred) > 1)
            throw new ArgumentException("A gallery allows at most 100 unique entries and one preferred image.", nameof(items));
        foreach (var item in items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Id);
            ValidateAssetId(item.AssetId);
            if (!(release ? item.Role is "cover" or "image" : item.Role is "logo" or "image") || item.Caption?.Length > 1000)
                throw new ArgumentException($"Use {(release ? "cover" : "logo")} or image, and a caption of at most 1000 characters.", nameof(items));
        }
    }

    public static void ValidateAssetId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 32 || id.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid artwork asset ID.", nameof(id));
    }
}
