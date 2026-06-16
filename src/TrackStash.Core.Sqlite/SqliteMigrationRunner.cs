using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

public sealed class SqliteMigrationRunner : IMigrationRunner
{
    private readonly string _connectionString;

    public SqliteMigrationRunner(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async ValueTask<int> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureMigrationTableAsync(connection, cancellationToken).ConfigureAwait(false);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migration";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result);
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
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

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
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
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
