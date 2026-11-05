using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class ScannerBackupTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"scanner-backup-{Guid.NewGuid():N}");
    private string Database => Path.Combine(_directory, "scanner.db");
    private string Root => Path.Combine(_directory, "backups");
    private CatalogBackupService Service => new(new SqliteScannerSnapshotSource(Database), null, Root);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await ExecuteAsync(Database, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE media_file(media_file_id INTEGER PRIMARY KEY,content_hash TEXT,path TEXT,format TEXT,size_bytes INTEGER,scanned_utc TEXT);
            CREATE TABLE metadata(metadata_id INTEGER PRIMARY KEY,media_file_id INTEGER REFERENCES media_file(media_file_id),artist TEXT,title TEXT,album TEXT);
            CREATE TABLE provider_cache(id TEXT PRIMARY KEY,payload BLOB);
            CREATE INDEX ix_media_path ON media_file(path);
            CREATE VIEW file_paths AS SELECT path FROM media_file;
            CREATE TRIGGER media_delete AFTER DELETE ON media_file BEGIN DELETE FROM provider_cache WHERE id=cast(old.media_file_id AS TEXT); END;
            INSERT INTO media_file VALUES(1,'abc','/JetDrive/Library/track.flac','flac',123,'2026-11-05');
            INSERT INTO metadata VALUES(1,1,'Artist','Original title','Album');
            INSERT INTO provider_cache VALUES('1',x'0001FF');
            """);
    }
    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }
    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<string> SchemaAsync(string database)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(sql,';') FROM (SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name)";
        return (string)(await command.ExecuteScalarAsync())!;
    }
    [Fact]
    public async Task RoundTripPreservesAllTablesSchemaBlobsAndWalDataWithoutExternalFiles()
    {
        await using var writer = new SqliteConnection($"Data Source={Database};Pooling=False");
        await writer.OpenAsync();
        using var update = writer.CreateCommand();
        update.CommandText = "UPDATE metadata SET title='Committed in WAL'";
        await update.ExecuteNonQueryAsync();
        using var transaction = writer.BeginTransaction();
        update.Transaction = transaction;
        update.CommandText = "UPDATE metadata SET title='Uncommitted scan update'";
        await update.ExecuteNonQueryAsync();
        var result = await Service.CreateAsync();
        Assert.Matches(@"^trackstash-scanner-\d{8}T\d{6}Z-[a-f0-9]{32}\.zip$", result.Backup.Filename);
        var archive = Path.Combine(Root, result.Backup.Filename);
        var manifest = await Service.VerifyAsync(archive);
        Assert.Equal("scanner", manifest.Kind);
        Assert.Equal("inventory.db", Assert.Single(manifest.Files).Path);
        using (var zip = ZipFile.OpenRead(archive)) Assert.Equal(2, zip.Entries.Count);
        var target = Path.Combine(_directory, "restored");
        await Service.RestoreAsync(archive, target);
        var restored = Path.Combine(target, "inventory.db");
        Assert.False(Directory.Exists(Path.Combine(target, "artwork")));
        Assert.Equal(await SchemaAsync(Database), await SchemaAsync(restored));
        await using var check = new SqliteConnection($"Data Source={restored};Pooling=False");
        await check.OpenAsync();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT title FROM metadata";
        Assert.Equal("Committed in WAL", await query.ExecuteScalarAsync());
        query.CommandText = "SELECT hex(payload) FROM provider_cache";
        Assert.Equal("0001FF", await query.ExecuteScalarAsync());
        query.CommandText = "SELECT path FROM file_paths";
        Assert.Equal("/JetDrive/Library/track.flac", await query.ExecuteScalarAsync());
        await Assert.ThrowsAsync<IOException>(() => Service.RestoreAsync(archive, target));
    }
    [Fact]
    public async Task CatalogAndScannerRetentionAndDownloadsAreIsolatedInSameRoot()
    {
        var catalogDatabase = Path.Combine(_directory, "catalog.db");
        await new SqliteStorageProvider(catalogDatabase).Migrations.ApplyPendingMigrationsAsync();
        var catalog = new CatalogBackupService(new SqliteCatalogSnapshotSource(catalogDatabase),
            new ManagedArtworkStore(Path.Combine(_directory, "artwork")), Root);
        var catalogBackup = (await catalog.CreateAsync()).Backup;
        for (var index = 0; index < 12; index++) await Service.CreateAsync();
        Assert.Equal(10, (await Service.ListAsync()).Count);
        Assert.Equal(catalogBackup, Assert.Single(await catalog.ListAsync()));
        var scannerBackup = (await Service.ListAsync())[0];
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.VerifyAsync(Path.Combine(Root, scannerBackup.Filename)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.VerifyAsync(Path.Combine(Root, catalogBackup.Filename)));
        await using (var stream = await Service.OpenDownloadAsync(scannerBackup.Id))
            await Assert.ThrowsAsync<BackupBusyException>(() => catalog.CreateAsync());
        await Assert.ThrowsAsync<FileNotFoundException>(() => catalog.OpenDownloadAsync(scannerBackup.Id));
    }
    [Fact]
    public async Task InvalidSourcesFailWithoutPublication()
    {
        await ExecuteAsync(Database, "DROP TABLE metadata");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.CreateAsync());
        File.Delete(Database);
        await Assert.ThrowsAsync<FileNotFoundException>(() => Service.CreateAsync());
        Assert.Empty(await Service.ListAsync());
        Assert.Empty(Directory.GetDirectories(Root, ".work-*"));
    }
    [Fact]
    public async Task ForeignKeyViolationsAreWarnedAndPreservedThroughBackupVerificationAndRestore()
    {
        await ExecuteAsync(Database, "PRAGMA foreign_keys=OFF; INSERT INTO metadata VALUES(2,999,'a','b','c')");
        var result = await Service.CreateAsync();
        Assert.Contains("1 foreign-key violations", Assert.Single(result.Warnings));
        var archive = Path.Combine(Root, result.Backup.Filename);
        var manifest = await Service.VerifyAsync(archive);
        Assert.Equal(result.Warnings, manifest.Warnings);
        using (var zip = ZipFile.OpenRead(archive))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
            Assert.Contains("1 foreign-key violations", JsonNode.Parse(stream)!["warnings"]![0]!.GetValue<string>());
        var target = Path.Combine(_directory, "with-orphan");
        await Service.RestoreAsync(archive, target);
        var snapshot = await new SqliteScannerSnapshotSource(Path.Combine(target, "inventory.db"))
            .InspectSnapshotAsync(Path.Combine(target, "inventory.db"));
        Assert.Equal(result.Warnings, snapshot.Warnings);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(target, "inventory.db")};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT media_file_id FROM metadata WHERE metadata_id=2";
        Assert.Equal(999L, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task ScannerSnapshotExcludesChangesCommittedAfterSnapshot()
    {
        var changed = false;
        var backup = (await Service.CreateAsync(new CallbackProgress(stage =>
        {
            if (stage != "Verifying ZIP and scanner") return;
            using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE metadata SET title='Changed after snapshot'";
            command.ExecuteNonQuery();
            changed = true;
        }))).Backup;
        var target = Path.Combine(_directory, "consistent");
        await Service.RestoreAsync(Path.Combine(Root, backup.Filename), target);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(target, "inventory.db")};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM metadata";
        Assert.Equal("Original title", await command.ExecuteScalarAsync());
        Assert.True(changed);
    }
    [Fact]
    public async Task TamperedInventoryCannotBeRestoredOrPublished()
    {
        var backup = (await Service.CreateAsync()).Backup;
        var archive = Path.Combine(Root, backup.Filename);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            zip.GetEntry("inventory.db")!.Delete();
            using var output = zip.CreateEntry("inventory.db").Open();
            output.Write([0, 1, 2]);
        }
        var target = Path.Combine(_directory, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RestoreAsync(archive, target));
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetDirectories(_directory, ".restore-*"));
    }
    [Fact]
    public async Task CatalogForeignKeyViolationsStillFail()
    {
        var database = Path.Combine(_directory, "catalog.db");
        await new SqliteStorageProvider(database).Migrations.ApplyPendingMigrationsAsync();
        await ExecuteAsync(database, """
            PRAGMA foreign_keys=OFF;
            CREATE TABLE orphan_test(id INTEGER REFERENCES label(label_id));
            INSERT INTO orphan_test VALUES(999);
            """);
        var service = new CatalogBackupService(new SqliteCatalogSnapshotSource(database), null, Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync());
        Assert.Empty(await service.ListAsync());
    }
    [Fact]
    public async Task LegacyCatalogManifestWithoutKindStillVerifies()
    {
        var database = Path.Combine(_directory, "catalog.db");
        await new SqliteStorageProvider(database).Migrations.ApplyPendingMigrationsAsync();
        var service = new CatalogBackupService(new SqliteCatalogSnapshotSource(database), null, Root);
        var backup = (await service.CreateAsync()).Backup;
        var path = Path.Combine(Root, backup.Filename);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            JsonNode manifest;
            using (var input = entry.Open()) manifest = JsonNode.Parse(input)!;
            manifest.AsObject().Remove("kind");
            entry.Delete();
            using var output = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            output.Write(manifest.ToJsonString());
        }
        Assert.Equal("catalog", (await service.VerifyAsync(path)).Kind);
    }
    private sealed class CallbackProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }
}
