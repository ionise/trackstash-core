using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

public sealed class SqliteMigrationRunner : IMigrationRunner
{
    private readonly string _connectionString;
    private readonly string? _artworkRoot;

    public SqliteMigrationRunner(string connectionString, string? artworkRoot = null)
    {
        _connectionString = connectionString;
        _artworkRoot = artworkRoot;
    }

    public async ValueTask<int> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migration'";
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
            return 0;
        return await GetVersionFromConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<MigrationResult> ApplyPendingMigrationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureMigrationTableAsync(connection, cancellationToken).ConfigureAwait(false);

        var currentVersion = await GetVersionFromConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        var applied = new List<string>();

        foreach (var migration in Migrations.All.Where(m => m.Version > currentVersion).OrderBy(m => m.Version))
        {
            await using var tx = connection.BeginTransaction();
            var exportedAssets = new List<string>();
            var exportedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            ManagedArtworkStore? artworkStore = null;
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (migration.Version == 6)
                {
                    using var legacy = connection.CreateCommand();
                    legacy.Transaction = tx;
                    legacy.CommandText = """
                        SELECT asset.asset_id,old.content FROM label_artwork old
                        JOIN artwork_asset asset ON asset.legacy_artwork_id=old.artwork_id
                        """;
                    await using (var reader = await legacy.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            artworkStore ??= new ManagedArtworkStore(_artworkRoot);
                            var id = reader.GetString(0);
                            var content = (byte[])reader.GetValue(1);
                            await artworkStore.SaveAsync(id, content, null, cancellationToken).ConfigureAwait(false);
                            exportedAssets.Add(id);
                            exportedHashes.Add(id, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)));
                        }
                    foreach (var id in exportedAssets)
                    {
                        using var hash = connection.CreateCommand();
                        hash.Transaction = tx;
                        hash.CommandText = "UPDATE artwork_asset SET content_hash=@hash WHERE asset_id=@id";
                        hash.Parameters.AddWithValue("@id", id);
                        hash.Parameters.AddWithValue("@hash", exportedHashes[id]);
                        await hash.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    legacy.CommandText = """
                        DROP TABLE label_artwork;
                        CREATE TRIGGER artwork_asset_immutable BEFORE UPDATE ON artwork_asset
                        BEGIN SELECT RAISE(ABORT,'Artwork assets are immutable; create a replacement'); END;
                        """;
                    await legacy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                using var recordCmd = connection.CreateCommand();
                recordCmd.Transaction = tx;
                recordCmd.CommandText = """
                    INSERT INTO schema_migration (version, name, applied_utc)
                    VALUES (@version, @name, @appliedUtc)
                    """;
                recordCmd.Parameters.AddWithValue("@version", migration.Version);
                recordCmd.Parameters.AddWithValue("@name", migration.Name);
                recordCmd.Parameters.AddWithValue("@appliedUtc", DateTimeOffset.UtcNow.ToString("O"));
                await recordCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                applied.Add(migration.Name);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var assetId in exportedAssets)
                    await artworkStore!.DeleteUncommittedAsync(assetId, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        var finalVersion = await GetVersionFromConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        return new MigrationResult
        {
            CurrentVersion = finalVersion,
            AppliedMigrations = applied,
            WasSuccessful = true,
        };
    }

    private static async ValueTask EnsureMigrationTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migration (
                version     INTEGER NOT NULL PRIMARY KEY,
                name        TEXT    NOT NULL,
                applied_utc TEXT    NOT NULL
            )
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> GetVersionFromConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migration";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }
}
