using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

public sealed class SqliteStorageProvider : IStorageProvider
{
    private readonly string _connectionString;

    public SqliteStorageProvider(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            // Allow transient writer locks to clear before failing commands.
            DefaultTimeout = 30,
        }.ToString();
    }

    public IMigrationRunner Migrations => new SqliteMigrationRunner(_connectionString);

    public StorageCapabilities Capabilities { get; } = new StorageCapabilities
    {
        SupportsTransactions = true,
        SupportsCaseInsensitiveSearch = true,
        SupportsBinaryVectorStorage = true,
        SupportsJsonPayloadStorage = true,
        SupportsIndexedExternalRefs = true,
    };

    public async ValueTask<IUnitOfWork> BeginUnitOfWorkAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var transaction = connection.BeginTransaction();
        return new SqliteUnitOfWork(connection, transaction);
    }
}
