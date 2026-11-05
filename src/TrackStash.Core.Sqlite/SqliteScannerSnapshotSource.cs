using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

public sealed class SqliteScannerSnapshotSource(string databasePath) : ICatalogSnapshotSource
{
    private readonly string _database = Path.GetFullPath(databasePath);
    public string Kind => "scanner";
    public string CatalogKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_database)));

    public Task CreateSnapshotAsync(string destination, CancellationToken ct = default) =>
        SqliteCatalogSnapshotSource.CreateSqliteSnapshotAsync(_database, destination, ct);

    public async Task<CatalogSnapshot> InspectSnapshotAsync(string database, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        await connection.OpenAsync(ct);
        await SqliteCatalogSnapshotSource.CheckIntegrityAsync(connection, ct, requireForeignKeys: false);
        using var command = connection.CreateCommand();
        // The scanner evolves additively without a migration ledger. Check its stable identity, not catalog migrations.
        try
        {
            foreach (var sql in new[]
            {
                "SELECT media_file_id, content_hash, path, format, size_bytes, scanned_utc FROM media_file LIMIT 0",
                "SELECT metadata_id, media_file_id, artist, title, album FROM metadata LIMIT 0",
            })
            {
                command.CommandText = sql;
                await using var reader = await command.ExecuteReaderAsync(ct);
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("Database is not a supported scanner inventory: required media_file/metadata columns are missing.", ex);
        }
        command.CommandText = "SELECT count(*) FROM pragma_foreign_key_check";
        var violations = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        IReadOnlyList<string> warnings = violations == 0 ? [] :
            [$"Scanner inventory contains {violations} foreign-key violations. The backup preserves these existing inconsistencies unchanged; restore does not repair them."];
        command.CommandText = "PRAGMA user_version";
        return new(Convert.ToInt32(await command.ExecuteScalarAsync(ct)), [], warnings);
    }
}
