using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class ArtworkTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"artwork-tests-{Guid.NewGuid():N}");
    private string Database => Path.Combine(_directory, "catalog.db");
    private string ContentRoot => Path.Combine(_directory, "images");
    private SqliteStorageProvider Provider => new(Database, ContentRoot);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await unit.Labels.UpsertAsync(new Label { Id = "survivor", Name = "Kept" });
        await unit.Labels.UpsertAsync(new Label { Id = "retired", Name = "Duplicate" });
        await unit.CommitAsync();
    }
    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }
    private static ArtworkAsset Asset() => new(Guid.NewGuid().ToString("N"), new string('A', 64), "image/png", 1, 1, 1,
        "image.png", "https://example.test", "Owner", "Permission recorded", DateTimeOffset.UtcNow);
    private static ArtworkEntry Entry(ArtworkAsset asset, bool preferred = false) => new(Guid.NewGuid().ToString("N"), asset.Id, "logo", "Caption", preferred);
    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Foreign Keys=True");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Gallery_OrdersSharesAndDetachesAssetsWithHistoryAndOriginalVersionProtection()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var first = Asset();
        var second = Asset();
        await unit.Artwork.AddAssetAsync(first);
        await unit.Artwork.AddAssetAsync(second);
        var empty = (await unit.Artwork.GetLabelGalleryAsync("survivor"))!;
        var a = Entry(first, true);
        var b = Entry(second);
        var saved = await unit.Artwork.ReplaceLabelGalleryAsync("survivor", empty.Version, [b, a]);
        Assert.Equal(LabelUpdateStatus.Updated, saved.Status);
        Assert.Equal(new[] { b, a }, saved.Gallery!.Items);
        Assert.NotEqual(empty.Version, saved.Gallery.Version);
        Assert.Equal(LabelUpdateStatus.Stale, (await unit.Artwork.ReplaceLabelGalleryAsync("survivor", empty.Version, [])).Status);
        var other = (await unit.Artwork.GetLabelGalleryAsync("retired"))!;
        Assert.Equal(LabelUpdateStatus.Updated, (await unit.Artwork.ReplaceLabelGalleryAsync("retired", other.Version, [Entry(first)])).Status);
        var detached = await unit.Artwork.ReplaceLabelGalleryAsync("survivor", saved.Gallery.Version, []);
        Assert.Empty(detached.Gallery!.Items);
        Assert.NotNull(await unit.Artwork.GetAssetAsync(first.Id));
        Assert.Single((await unit.Artwork.GetLabelGalleryAsync("retired"))!.Items);
        Assert.False((await unit.EntityDelete!.DeleteEntityAsync("label", "survivor")).Success);
        Assert.Equal(LabelUpdateStatus.NotFound, (await unit.Artwork.ReplaceLabelGalleryAsync("missing", empty.Version, [])).Status);
        await unit.CommitAsync();
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM artwork_asset"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("UPDATE artwork_asset SET usage_rights='Changed'"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM label_artwork_revision"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Merge_StacksRetiredGalleryBehindSurvivorAndRetainsPreferenceAndEvidence(bool survivorPreferred)
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var asset = Asset();
        await unit.Artwork.AddAssetAsync(asset);
        var kept = Entry(asset, survivorPreferred);
        var moved = Entry(asset, true);
        foreach (var (id, item) in new[] { ("survivor", kept), ("retired", moved) })
        {
            var before = (await unit.Artwork.GetLabelGalleryAsync(id))!;
            await unit.Artwork.ReplaceLabelGalleryAsync(id, before.Version, [item]);
        }
        var preview = (await unit.LabelMerges.PreviewAsync("survivor", "retired", DateTimeOffset.UtcNow)).Preview!;
        Assert.Empty(preview.Blockers);
        Assert.Equal(new[] { kept, moved with { IsPreferred = !survivorPreferred } }, preview.Artwork!);
        var result = await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "Same label", null, "test", DateTimeOffset.UtcNow);
        Assert.Equal(LabelMergeStatus.Applied, result.Status);
        Assert.Equal(preview.Artwork, (await unit.Artwork.GetLabelGalleryAsync("survivor"))!.Items);
        await Assert.ThrowsAsync<RetiredLabelException>(() => unit.Artwork.GetLabelGalleryAsync("retired").AsTask());
        await unit.CommitAsync();
        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT before_json FROM label_merge_audit";
        using var audit = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        Assert.Equal(2, audit.RootElement.GetProperty("Rows").GetProperty("label_artwork_link").GetArrayLength());
        Assert.Single(audit.RootElement.GetProperty("Rows").GetProperty("artwork_asset").EnumerateArray());
        command.CommandText = "SELECT count(*) FROM label_artwork_revision WHERE original_label_id='retired' AND label_id='survivor'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA foreign_key_check";
        Assert.Null(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task GalleryChange_InvalidatesMergeAndInjectedFailureRollsBackEvenWhenCallerCommits()
    {
        ArtworkGallery gallery;
        LabelMergePreview preview;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            gallery = (await unit.Artwork.GetLabelGalleryAsync("retired"))!;
            preview = (await unit.LabelMerges.PreviewAsync("survivor", "retired", DateTimeOffset.UtcNow)).Preview!;
            await unit.CommitAsync();
        }
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var asset = Asset();
            await unit.Artwork.AddAssetAsync(asset);
            await unit.Artwork.ReplaceLabelGalleryAsync("retired", gallery.Version, [Entry(asset)]);
            Assert.Equal(LabelMergeStatus.Stale, (await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "Same", null, "test", DateTimeOffset.UtcNow)).Status);
            await unit.CommitAsync();
        }
        await ExecuteAsync("CREATE TRIGGER fail_artwork BEFORE INSERT ON label_artwork_revision BEGIN SELECT RAISE(ABORT,'Injected failure'); END;");
        await using var edit = await Provider.BeginUnitOfWorkAsync();
        var before = (await edit.Artwork.GetLabelGalleryAsync("retired"))!;
        await Assert.ThrowsAsync<SqliteException>(() => edit.Artwork.ReplaceLabelGalleryAsync("retired", before.Version, []).AsTask());
        Assert.Equal(before.Version, (await edit.Artwork.GetLabelGalleryAsync("retired"))!.Version);
        Assert.Equal(before.Items, (await edit.Artwork.GetLabelGalleryAsync("retired"))!.Items);
        await edit.CommitAsync();
    }

    [Fact]
    public async Task Migration_ExportsPopulatedVersionFiveWithoutLosingBytesOrProvenance()
    {
        await PrepareLegacyAsync();
        var result = await Provider.Migrations.ApplyPendingMigrationsAsync();
        Assert.Equal(7, result.CurrentVersion);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var gallery = (await unit.Artwork.GetLabelGalleryAsync("survivor"))!;
        var item = Assert.Single(gallery.Items);
        Assert.Equal("original-id", item.Id);
        var asset = (await unit.Artwork.GetAssetAsync(item.AssetId))!;
        Assert.Null(asset.Width);
        Assert.Equal("https://example.test", asset.SourceUrl);
        Assert.Equal("Owner", asset.Attribution);
        Assert.Equal("Restricted use", asset.UsageRights);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 0, 1, 2, 255 })), asset.ContentHash);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, await new ManagedArtworkStore(ContentRoot).ReadAsync(asset.Id, false));
    }

    [Fact]
    public async Task Migration_StorageFailureLeavesVersionFiveAndOriginalContentUntouched()
    {
        await PrepareLegacyAsync();
        var badRoot = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(badRoot, "blocking file");
        await Assert.ThrowsAnyAsync<IOException>(() => new SqliteStorageProvider(Database, badRoot).Migrations.ApplyPendingMigrationsAsync().AsTask());
        Assert.Equal(5, await Provider.Migrations.GetCurrentVersionAsync());
        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hex(content) FROM label_artwork";
        Assert.Equal("000102FF", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Migration_DatabaseFailureRemovesOnlyUncommittedExportedFiles()
    {
        await PrepareLegacyAsync();
        await ExecuteAsync("""
            CREATE TRIGGER fail_migration BEFORE INSERT ON schema_migration WHEN NEW.version=6
            BEGIN SELECT RAISE(ABORT,'Injected migration failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => Provider.Migrations.ApplyPendingMigrationsAsync().AsTask());
        Assert.Equal(5, await Provider.Migrations.GetCurrentVersionAsync());
        Assert.Empty(Directory.GetDirectories(ContentRoot));
    }

    private Task PrepareLegacyAsync() => ExecuteAsync("""
        DROP TRIGGER relationship_history_insert;
        DROP TRIGGER relationship_history_update;
        DROP TRIGGER relationship_history_delete;
        DROP TRIGGER relationship_cycle_insert;
        DROP TRIGGER relationship_cycle_update;
        DROP TABLE label_relationship_revision;
        DROP TABLE label_artwork_link;
        DROP TABLE label_artwork_revision;
        DROP TRIGGER artwork_asset_no_delete;
        DROP TABLE artwork_asset;
        DELETE FROM schema_migration WHERE version>=6;
        CREATE TABLE label_artwork(
            artwork_id TEXT PRIMARY KEY,label_id TEXT REFERENCES label(label_id),role TEXT,mime_type TEXT,content_hash TEXT,
            content BLOB,source_url TEXT,attribution TEXT,usage_rights TEXT,created_utc TEXT);
        INSERT INTO label_artwork VALUES('original-id','survivor','logo','image/png','legacy-hash',X'000102FF',
            'https://example.test','Owner','Restricted use','2026-11-04T00:00:00Z');
        """);

    [Fact]
    public async Task ManagedFiles_RejectTraversalAndGitRootsAndRemainImmutable()
    {
        var store = new ManagedArtworkStore(ContentRoot);
        var id = Guid.NewGuid().ToString("N");
        await store.SaveAsync(id, [1, 2], [3, 4]);
        Assert.Equal(new byte[] { 1, 2 }, await store.ReadAsync(id, false));
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(id, [5], null).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync("../escape", false).AsTask());
        var gitDirectory = Path.Combine(_directory, "git-repository");
        Directory.CreateDirectory(Path.Combine(gitDirectory, ".git"));
        Assert.Throws<ArgumentException>(() => new ManagedArtworkStore(Path.Combine(gitDirectory, "images")));
        await store.DeleteUncommittedAsync(id);
        Assert.False(Directory.Exists(Path.Combine(store.Root, id)));
    }
}
