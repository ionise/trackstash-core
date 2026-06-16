using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private bool _disposed;

    public SqliteUnitOfWork(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;

        Labels = new SqliteLabelRepository(connection, transaction);
        Artists = new SqliteArtistRepository(connection, transaction);
        Releases = new SqliteReleaseRepository(connection, transaction);
        Recordings = new SqliteRecordingRepository(connection, transaction);
        MediaFiles = new SqliteMediaFileRepository(connection, transaction);
        Matches = new SqliteMatchRepository(connection, transaction);
        Embeddings = new SqliteEmbeddingRepository(connection, transaction);
    }

    public ILabelRepository Labels { get; }
    public IArtistRepository Artists { get; }
    public IReleaseRepository Releases { get; }
    public IRecordingRepository Recordings { get; }
    public IMediaFileRepository MediaFiles { get; }
    public IMatchRepository Matches { get; }
    public IEmbeddingRepository? Embeddings { get; }

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
