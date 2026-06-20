namespace TrackStash.Core.Storage;

public sealed record StorageProviderDescriptor(
    string Provider,
    string? DatabasePath = null,
    string? ConnectionString = null);

public interface IStorageProviderFactory
{
    IStorageProvider Create(StorageProviderDescriptor descriptor);
}

public interface IStorageProvider
{
    ValueTask<IUnitOfWork> BeginUnitOfWorkAsync(CancellationToken cancellationToken = default);

    IMigrationRunner Migrations { get; }

    StorageCapabilities Capabilities { get; }
}

public interface IUnitOfWork : IAsyncDisposable
{
    ILabelRepository Labels { get; }

    IArtistRepository Artists { get; }

    IReleaseRepository Releases { get; }

    IRecordingRepository Recordings { get; }

    IMediaFileRepository MediaFiles { get; }

    IMatchRepository Matches { get; }

    IEmbeddingRepository? Embeddings { get; }

    IEntityDeleteService? EntityDelete { get; }

    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

public interface IMigrationRunner
{
    ValueTask<int> GetCurrentVersionAsync(CancellationToken cancellationToken = default);

    ValueTask<MigrationResult> ApplyPendingMigrationsAsync(CancellationToken cancellationToken = default);
}

public interface ILabelRepository
{
    ValueTask<Label?> GetByIdAsync(string labelId, CancellationToken cancellationToken = default);

    ValueTask<Label?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default);

    ValueTask<Label?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);

    ValueTask<int> CountAsync(CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(Label label, CancellationToken cancellationToken = default);
}

public interface IArtistRepository
{
    ValueTask<Artist?> GetByIdAsync(string artistId, CancellationToken cancellationToken = default);

    ValueTask<Artist?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default);

    ValueTask<Artist?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);

    ValueTask<int> CountAsync(CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(Artist artist, CancellationToken cancellationToken = default);
}

public interface IReleaseRepository
{
    ValueTask<Release?> GetByIdAsync(string releaseId, CancellationToken cancellationToken = default);

    ValueTask<Release?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default);

    ValueTask<Release?> GetByNormalizedTitleAndLabelAsync(string normalizedTitle, string? labelId, CancellationToken cancellationToken = default);

    ValueTask<int> CountAsync(CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(Release release, CancellationToken cancellationToken = default);
}

public interface IRecordingRepository
{
    ValueTask<Recording?> GetByIdAsync(string recordingId, CancellationToken cancellationToken = default);

    ValueTask<Recording?> GetByIsrcAsync(string isrc, CancellationToken cancellationToken = default);

    ValueTask<Recording?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default);

    ValueTask<Recording?> GetByNormalizedTitleAndMixNameAsync(string normalizedTitle, string? normalizedMixName, CancellationToken cancellationToken = default);

    ValueTask<int> CountAsync(CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(Recording recording, CancellationToken cancellationToken = default);
}

public interface IMediaFileRepository
{
    ValueTask<MediaFile?> GetByIdAsync(string mediaFileId, CancellationToken cancellationToken = default);

    ValueTask<MediaFile?> GetByPathAsync(string path, CancellationToken cancellationToken = default);

    ValueTask<MediaFile?> GetByContentHashAsync(string contentHash, CancellationToken cancellationToken = default);

    ValueTask<int> CountAsync(CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(MediaFile mediaFile, CancellationToken cancellationToken = default);
}

public interface IMatchRepository
{
    ValueTask<MatchRecord?> GetBestMatchByMediaFileIdAsync(string mediaFileId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MatchCandidate>> GetCandidatesByMediaFileIdAsync(string mediaFileId, CancellationToken cancellationToken = default);

    ValueTask UpsertBestMatchAsync(MatchRecord matchRecord, CancellationToken cancellationToken = default);

    ValueTask UpsertCandidatesAsync(string mediaFileId, IReadOnlyList<MatchCandidate> candidates, CancellationToken cancellationToken = default);

    ValueTask SetUserOverrideAsync(string mediaFileId, string recordingId, MatchOverrideState overrideState, CancellationToken cancellationToken = default);
}

public interface IEmbeddingRepository
{
    ValueTask<EmbeddingDocument?> GetByEntityIdAsync(string entityId, string modelName, string modelVersion, CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(EmbeddingDocument embeddingDocument, CancellationToken cancellationToken = default);

    ValueTask DeleteByDocumentHashAsync(string documentHash, CancellationToken cancellationToken = default);
}
