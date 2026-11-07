using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private bool _disposed;
    private IEntityDeleteService? _entityDeleteService;

    public SqliteUnitOfWork(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;

        Labels = new SqliteLabelRepository(connection, transaction);
        LabelMerges = new SqliteLabelMergeRepository(connection, transaction);
        LabelRelationships = new SqliteLabelRelationshipRepository(connection, transaction);
        LabelGroups = new SqliteLabelGroupRepository(connection, transaction);
        Artwork = new SqliteArtworkRepository(connection, transaction);
        Artists = new SqliteArtistRepository(connection, transaction);
        Releases = new SqliteReleaseRepository(connection, transaction);
        Recordings = new SqliteRecordingRepository(connection, transaction);
        CatalogFoundations = new SqliteCatalogFoundationRepository(connection, transaction);
        ReleaseImports = new SqliteReleaseImportRepository(connection, transaction);
        Enrichment = new SqliteEnrichmentRepository(connection, transaction);
        MediaFiles = new SqliteMediaFileRepository(connection, transaction);
        Matches = new SqliteMatchRepository(connection, transaction);
        Embeddings = new SqliteEmbeddingRepository(connection, transaction);
    }

    public ILabelRepository Labels { get; }
    public ILabelMergeRepository LabelMerges { get; }
    public ILabelRelationshipRepository LabelRelationships { get; }
    public ILabelGroupRepository LabelGroups { get; }
    public IArtworkRepository Artwork { get; }
    public IArtistRepository Artists { get; }
    public IReleaseRepository Releases { get; }
    public IRecordingRepository Recordings { get; }
    public ICatalogFoundationRepository CatalogFoundations { get; }
    public IReleaseImportRepository ReleaseImports { get; }
    public IEnrichmentRepository Enrichment { get; }
    public IMediaFileRepository MediaFiles { get; }
    public IMatchRepository Matches { get; }
    public IEmbeddingRepository? Embeddings { get; }
    public IEntityDeleteService? EntityDelete => _entityDeleteService ??= new SqliteEntityDeleteService(_connection, _transaction);

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
