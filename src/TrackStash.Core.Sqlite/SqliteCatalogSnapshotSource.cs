using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

public sealed class SqliteCatalogSnapshotSource(string databasePath) : ICatalogSnapshotSource
{
    private readonly string _database = Path.GetFullPath(databasePath);
    public string CatalogKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_database)));
    private static SqliteConnection ReadOnly(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
    }.ToString());

    public async Task CreateSnapshotAsync(string destination, CancellationToken ct = default)
    {
        if (!File.Exists(_database)) throw new FileNotFoundException("Catalog database does not exist.", _database);
        await using var input = ReadOnly(_database);
        await input.OpenAsync(ct);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var file = new FileStream(destination, options)) { }
        await using var output = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        await output.OpenAsync(ct);
        ct.ThrowIfCancellationRequested();
        input.BackupDatabase(output);
        ct.ThrowIfCancellationRequested();
    }

    public async Task<CatalogSnapshot> InspectSnapshotAsync(string database, CancellationToken ct = default)
    {
        await using var connection = ReadOnly(database);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        if ((string?)await command.ExecuteScalarAsync(ct) != "ok") throw new InvalidDataException("Catalog integrity check failed.");
        command.CommandText = "PRAGMA foreign_key_check";
        await using (var violations = await command.ExecuteReaderAsync(ct))
            if (await violations.ReadAsync(ct)) throw new InvalidDataException("Catalog has foreign-key violations.");
        command.CommandText = "SELECT max(version) FROM schema_migration";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        if (version != 6) throw new InvalidDataException("Backup currently requires catalog schema 6. Migrate deliberately before backing up.");
        command.CommandText = "SELECT asset_id,content_hash,byte_length FROM artwork_asset ORDER BY asset_id";
        var assets = new List<BackupAsset>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            ArtworkRules.ValidateAssetId(id);
            assets.Add(new(id, reader.GetString(1), reader.GetInt64(2)));
        }
        return new(version, assets);
    }
}
