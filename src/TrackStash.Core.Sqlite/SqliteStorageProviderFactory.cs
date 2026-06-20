using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

public sealed class SqliteStorageProviderFactory : IStorageProviderFactory
{
    public IStorageProvider Create(StorageProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!string.Equals(descriptor.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported provider for {nameof(SqliteStorageProviderFactory)}: {descriptor.Provider}");

        if (string.IsNullOrWhiteSpace(descriptor.DatabasePath))
            throw new ArgumentException("DatabasePath is required for sqlite provider.");

        return new SqliteStorageProvider(descriptor.DatabasePath);
    }
}
