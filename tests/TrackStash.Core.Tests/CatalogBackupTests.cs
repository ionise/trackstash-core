using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class CatalogBackupTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"backup-tests-{Guid.NewGuid():N}");
    private string Database => Path.Combine(_directory, "catalog.db");
    private string Root => Path.Combine(_directory, "backups");
    private ManagedArtworkStore Artwork => new(Path.Combine(_directory, "artwork"));
    private SqliteStorageProvider Provider => new(Database, Artwork.Root);
    private CatalogBackupService Service => new(new SqliteCatalogSnapshotSource(Database), Artwork, Root);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await unit.Labels.UpsertAsync(new Label { Id = "label", Name = "Private curated label" });
        await unit.CommitAsync();
    }
    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }
    private async Task<ArtworkAsset> AddAssetAsync(bool attach = false)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var asset = new ArtworkAsset(Guid.NewGuid().ToString("N"), Convert.ToHexString(SHA256.HashData(bytes)),
            "image/png", bytes.Length, 1, 1, "logo.png", null, null, "Test permission", DateTimeOffset.UtcNow);
        await Artwork.SaveAsync(asset.Id, bytes, [5, 6, 7]);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await unit.Artwork.AddAssetAsync(asset);
        if (attach)
        {
            var gallery = (await unit.Artwork.GetLabelGalleryAsync("label"))!;
            var entry = new ArtworkEntry(Guid.NewGuid().ToString("N"), asset.Id, "logo", "Caption", true);
            await unit.Artwork.ReplaceLabelGalleryAsync("label", gallery.Version, [entry]);
        }
        await unit.CommitAsync();
        return asset;
    }
    [Fact]
    public async Task RoundTripIncludesRetainedAssetsAndRejectsExistingRestoreTargets()
    {
        var attached = await AddAssetAsync(true);
        var retained = await AddAssetAsync();
        var result = await Service.CreateAsync();
        Assert.Empty(result.Warnings);
        Assert.Matches(@"^trackstash-catalog-\d{8}T\d{6}Z-[a-f0-9]{32}\.zip$", result.Backup.Filename);
        var path = Path.Combine(Root, result.Backup.Filename);
        var manifest = await Service.VerifyAsync(path);
        Assert.Equal(5, manifest.Files.Count);
        Assert.Equal(8, manifest.SchemaVersion);
        Assert.Contains(manifest.Files, f => f.Path == $"artwork/{retained.Id}/original");
        var restore = Path.Combine(_directory, "restored");
        await Service.RestoreAsync(path, restore);
        var restored = new SqliteStorageProvider(Path.Combine(restore, "catalog.db"), Path.Combine(restore, "artwork"));
        await using var unit = await restored.BeginUnitOfWorkAsync();
        Assert.Equal("Private curated label", (await unit.Labels.GetByIdAsync("label"))!.Name);
        Assert.Equal(attached.Id, Assert.Single((await unit.Artwork.GetLabelGalleryAsync("label"))!.Items).AssetId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(restore, "artwork", retained.Id, "original")));
        await Assert.ThrowsAsync<IOException>(() => Service.RestoreAsync(path, restore));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
    [Fact]
    public async Task Schema6ArchivesRemainVerifiableAfterUpgradeAndSchema7RestoresRelationshipsAndHistory()
    {
        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE label_group_membership; DROP TABLE label_group_membership_revision;
                DROP TABLE label_group; DROP TABLE label_group_revision;
                DROP TRIGGER relationship_history_insert; DROP TRIGGER relationship_history_update; DROP TRIGGER relationship_history_delete;
                DROP TRIGGER relationship_cycle_insert; DROP TRIGGER relationship_cycle_update;
                DROP TABLE label_relationship_revision;
                DELETE FROM schema_migration WHERE version>=7;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var old = await Service.CreateAsync();
        var oldPath = Path.Combine(Root, old.Backup.Filename);
        Assert.Equal(6, (await Service.VerifyAsync(oldPath)).SchemaVersion);
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.Labels.UpsertAsync(new Label { Id = "parent", Name = "Parent label" });
            var current = (await unit.LabelRelationships.GetAsync("label"))!;
            var parent = (await unit.Labels.GetByIdAsync("parent"))!;
            await unit.LabelRelationships.ReplaceOutgoingAsync("label", current.Version,
                [new(null, "parent", "imprint_of", "manual", "https://example.test", "Retained evidence", parent.Version)]);
            await unit.CommitAsync();
        }
        var latest = await Service.CreateAsync();
        var restore = Path.Combine(_directory, "relationships-restored");
        await Service.RestoreAsync(Path.Combine(Root, latest.Backup.Filename), restore);
        await using (var unit = await new SqliteStorageProvider(Path.Combine(restore, "catalog.db")).BeginUnitOfWorkAsync())
            Assert.Equal("Retained evidence", Assert.Single((await unit.LabelRelationships.GetAsync("label"))!.Items).Relationship.Notes);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(restore, "catalog.db")}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM label_relationship_revision";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        var oldRestore = Path.Combine(_directory, "old-schema-restored");
        await Service.RestoreAsync(oldPath, oldRestore);
        Assert.Equal(6, await new SqliteStorageProvider(Path.Combine(oldRestore, "catalog.db")).Migrations.GetCurrentVersionAsync());
    }
    [Fact]
    public async Task Schema7ArchivesRemainCompatibleAndSchema8RoundTripRetainsGroupsMembershipsAndPermanentHistory()
    {
        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE label_group_membership; DROP TABLE label_group_membership_revision;
                DROP TABLE label_group; DROP TABLE label_group_revision;
                DELETE FROM schema_migration WHERE version=8;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var old = await Service.CreateAsync();
        var oldPath = Path.Combine(Root, old.Backup.Filename);
        Assert.Equal(7, (await Service.VerifyAsync(oldPath)).SchemaVersion);
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        string groupId, memberId;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var group = (await unit.LabelGroups.CreateAsync("family", new("Anjuna", null, null, null))).Group!;
            groupId = group.Id;
            var label = (await unit.Labels.GetByIdAsync("label"))!;
            var member = (await unit.LabelGroups.SaveMembershipAsync(null, null, label.Id, group.Id, label.Version, group.Version,
                new("current", "2020", null, "Official website", "https://example.test", "Affiliation only"), false)).Membership!;
            memberId = member.Membership.Id;
            await unit.LabelGroups.SaveMembershipAsync(memberId, member.Membership.Version, label.Id, group.Id, member.Label.Version, member.Group.Version,
                member.Membership.Fields with { Status = "former", Until = "2024-03" }, false);
            group = (await unit.LabelGroups.GetAsync(group.Id))!;
            await unit.LabelGroups.UpdateAsync(group.Id, group.Version, new("Anjuna family", null, "Reviewed brand", null));
            await unit.Labels.UpsertAsync(new Label { Id = "removed-member", Name = "Incorrect affiliation" });
            group = (await unit.LabelGroups.GetAsync(group.Id))!;
            var other = (await unit.Labels.GetByIdAsync("removed-member"))!;
            var assertion = (await unit.LabelGroups.SaveMembershipAsync(null, null, other.Id, group.Id, other.Version, group.Version,
                new("unknown", null, null, "manual", null, null), false)).Membership!;
            await unit.LabelGroups.RemoveMembershipAsync(assertion.Membership.Id, assertion.Membership.Version);
            await unit.CommitAsync();
        }
        await AddAssetAsync(true);
        var backup = await Service.CreateAsync();
        var latestPath = Path.Combine(Root, backup.Backup.Filename);
        Assert.Equal(8, (await Service.VerifyAsync(latestPath)).SchemaVersion);
        Assert.Equal(7, (await Service.VerifyAsync(oldPath)).SchemaVersion);
        var restore = Path.Combine(_directory, "groups-restored");
        await Service.RestoreAsync(latestPath, restore);
        var restored = new SqliteStorageProvider(Path.Combine(restore, "catalog.db"), Path.Combine(restore, "artwork"));
        await using (var unit = await restored.BeginUnitOfWorkAsync())
        {
            var group = (await unit.LabelGroups.GetAsync(groupId))!;
            Assert.Equal("Anjuna family", group.Name); Assert.Equal(0, group.CurrentMembers);
            var member = (await unit.LabelGroups.MembershipAsync(memberId))!;
            Assert.Equal("2024-03", member.Membership.Fields.Until);
            Assert.Equal("former", member.Membership.Fields.Status);
            Assert.Equal("Affiliation only", member.Membership.Fields.Notes);
            Assert.Single((await unit.Artwork.GetLabelGalleryAsync("label"))!.Items);
            Assert.Single((await unit.LabelGroups.MembershipsAsync(null, groupId, new(Status: "all")))!.Items);
        }
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(restore, "catalog.db")}"))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM label_group_membership_revision";
            Assert.Equal(5L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT count(*) FROM label_group_revision";
            Assert.Equal(2L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT count(*) FROM label_group_membership_revision WHERE before_json LIKE '%removed-member%'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        var oldRestore = Path.Combine(_directory, "schema-seven-restored");
        await Service.RestoreAsync(oldPath, oldRestore);
        Assert.Equal(7, await new SqliteStorageProvider(Path.Combine(oldRestore, "catalog.db")).Migrations.GetCurrentVersionAsync());
    }
    [Fact]
    public async Task BackupFilenameIsIndependentOfCurrentCalendar()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CatalogBackupInfo backup;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            backup = (await Service.CreateAsync()).Backup;
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Assert.Equal(backup, Assert.Single(await Service.ListAsync()));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrCorruptOriginalFailsWithoutPublishingOrPruning(bool missing)
    {
        var asset = await AddAssetAsync();
        var previous = await Service.CreateAsync();
        var original = Path.Combine(Artwork.Root, asset.Id, "original");
        if (missing) File.Delete(original); else await File.WriteAllBytesAsync(original, [9]);
        if (missing) await Assert.ThrowsAsync<FileNotFoundException>(() => Service.CreateAsync());
        else await Assert.ThrowsAsync<InvalidDataException>(() => Service.CreateAsync());
        Assert.Equal(previous.Backup, Assert.Single(await Service.ListAsync()));
        Assert.Empty(Directory.GetDirectories(Root, ".work-*"));
    }
    [Fact]
    public async Task RetainsExactlyNewestTenAndDoesNotTouchUnrelatedArchives()
    {
        Directory.CreateDirectory(Root);
        var unrelated = Path.Combine(Root, "holiday.zip");
        await File.WriteAllTextAsync(unrelated, "not a backup");
        var otherDatabase = Path.Combine(_directory, "other.db");
        File.Copy(Database, otherDatabase);
        var otherService = new CatalogBackupService(new SqliteCatalogSnapshotSource(otherDatabase), Artwork, Root);
        var otherBackup = (await otherService.CreateAsync()).Backup;
        CatalogBackupInfo? first = null;
        for (var index = 0; index < 12; index++)
        {
            var result = await Service.CreateAsync();
            first ??= result.Backup;
            Assert.Empty(result.Warnings);
        }
        var list = await Service.ListAsync();
        Assert.Equal(10, list.Count);
        Assert.DoesNotContain(list, b => b.Id == first!.Id);
        Assert.True(File.Exists(unrelated));
        Assert.Equal(11, Directory.GetFiles(Root, "trackstash-*.zip").Length);
        Assert.Equal(otherBackup, Assert.Single(await otherService.ListAsync()));
    }
    [Fact]
    public async Task RetentionReportsCorruptOldArchiveWithoutDeletingItOrLosingNewBackup()
    {
        var first = (await Service.CreateAsync()).Backup;
        using (var zip = ZipFile.Open(Path.Combine(Root, first.Filename), ZipArchiveMode.Update))
        {
            zip.GetEntry("catalog.db")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("catalog.db").Open());
            writer.Write("damaged");
        }
        for (var index = 0; index < 9; index++) await Service.CreateAsync();
        var result = await Service.CreateAsync();
        Assert.Single(result.Warnings);
        Assert.Contains(first.Filename, result.Warnings[0]);
        Assert.True(File.Exists(Path.Combine(Root, first.Filename)));
        await Service.VerifyAsync(Path.Combine(Root, result.Backup.Filename));
        Assert.Equal(11, (await Service.ListAsync()).Count);
    }
    [Fact]
    public async Task CorruptRecentArchiveDoesNotCountTowardNewestTenVerifiedRetention()
    {
        CatalogBackupInfo? recent = null;
        for (var index = 0; index < 10; index++) recent = (await Service.CreateAsync()).Backup;
        using (var zip = ZipFile.Open(Path.Combine(Root, recent!.Filename), ZipArchiveMode.Update))
        {
            zip.GetEntry("catalog.db")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("catalog.db").Open());
            writer.Write("damaged");
        }
        var result = await Service.CreateAsync();
        Assert.Single(result.Warnings);
        Assert.Equal(11, (await Service.ListAsync()).Count);
        Assert.True(File.Exists(Path.Combine(Root, recent.Filename)));
    }
    [Fact]
    public async Task DetachedGalleryHistoryAndAbsentOptionalThumbnailRoundTrip()
    {
        var asset = await AddAssetAsync(true);
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var gallery = (await unit.Artwork.GetLabelGalleryAsync("label"))!;
            await unit.Artwork.ReplaceLabelGalleryAsync("label", gallery.Version, []);
            await unit.CommitAsync();
        }
        File.Delete(Path.Combine(Artwork.Root, asset.Id, "thumbnail.jpg"));
        var backup = (await Service.CreateAsync()).Backup;
        var manifest = await Service.VerifyAsync(Path.Combine(Root, backup.Filename));
        Assert.Equal(2, manifest.Files.Count);
        var restored = Path.Combine(_directory, "detached");
        await Service.RestoreAsync(Path.Combine(Root, backup.Filename), restored);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(restored, "catalog.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM label_artwork_revision";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "SELECT count(*) FROM label_artwork_link";
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.True(File.Exists(Path.Combine(restored, "artwork", asset.Id, "original")));
    }
    [Fact]
    public async Task UnsupportedManifestAndDuplicateZipEntriesAreRejected()
    {
        var backup = (await Service.CreateAsync()).Backup;
        var path = Path.Combine(Root, backup.Filename);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var duplicate = zip.CreateEntry("catalog.db").Open();
            duplicate.Write([1]);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.VerifyAsync(path));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("manifest.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(JsonSerializer.Serialize(new { formatVersion = 999, id = backup.Id }));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.VerifyAsync(path));
    }
    [Fact]
    public async Task RejectsGitRootsAndChangedSymlinkRootsWithoutPublishing()
    {
        var git = Path.Combine(_directory, "git");
        Directory.CreateDirectory(Path.Combine(git, ".git"));
        Assert.Throws<ArgumentException>(() => new CatalogBackupService(new SqliteCatalogSnapshotSource(Database), Artwork, Path.Combine(git, "backups")));
        if (OperatingSystem.IsWindows()) return;
        await Service.CreateAsync();
        var original = Root + "-original";
        Directory.Move(Root, original);
        var service = new CatalogBackupService(new SqliteCatalogSnapshotSource(Database), Artwork, original);
        var replacement = Path.Combine(_directory, "replacement");
        Directory.CreateDirectory(replacement);
        Directory.Move(original, original + "-kept");
        Directory.CreateSymbolicLink(original, replacement);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync());
        Assert.Empty(Directory.GetFiles(replacement));
    }
    [Fact]
    public async Task DownloadsHoldCrossInstanceLeaseUntilDisposedAndMatchSavedZip()
    {
        var backup = (await Service.CreateAsync()).Backup;
        await using (var stream = await Service.OpenDownloadAsync(backup.Id))
        {
            await Assert.ThrowsAsync<BackupBusyException>(() => Service.CreateAsync());
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(Root, backup.Filename)), memory.ToArray());
        }
        await Service.CreateAsync();
    }
    [Fact]
    public async Task TamperedZipAndTraversalEntriesAreRejectedWithoutRestorePublication()
    {
        var backup = (await Service.CreateAsync()).Backup;
        var path = Path.Combine(Root, backup.Filename);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open());
            writer.Write("unsafe");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.VerifyAsync(path));
        var target = Path.Combine(_directory, "unsafe-restore");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RestoreAsync(path, target));
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(_directory, "escape.txt")));
        Assert.Empty(Directory.GetDirectories(_directory, ".restore-*"));
    }
    [Fact]
    public async Task CorruptArchivePayloadFailsChecksumVerification()
    {
        var asset = await AddAssetAsync();
        var backup = (await Service.CreateAsync()).Backup;
        var path = Path.Combine(Root, backup.Filename);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry($"artwork/{asset.Id}/original")!.Delete();
            using var output = zip.CreateEntry($"artwork/{asset.Id}/original").Open();
            output.Write([9, 9, 9, 9]);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.VerifyAsync(path));
    }
    [Fact]
    public async Task SnapshotRemainsConsistentWhenLiveCatalogChangesDuringPackaging()
    {
        var changed = false;
        var progress = new CallbackProgress(stage =>
        {
            if (stage != "Verifying ZIP and catalog" || changed) return;
            using var connection = new SqliteConnection($"Data Source={Database}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE label SET name='Changed after snapshot' WHERE label_id='label'";
            cmd.ExecuteNonQuery();
            changed = true;
        });
        var backup = (await Service.CreateAsync(progress)).Backup;
        var restore = Path.Combine(_directory, "consistent");
        await Service.RestoreAsync(Path.Combine(Root, backup.Filename), restore);
        await using var unit = await new SqliteStorageProvider(Path.Combine(restore, "catalog.db")).BeginUnitOfWorkAsync();
        Assert.Equal("Private curated label", (await unit.Labels.GetByIdAsync("label"))!.Name);
        Assert.True(changed);
    }
    [Fact]
    public async Task CancellationCleansOnlyOwnStagingAndNeverPublishes()
    {
        using var cts = new CancellationTokenSource();
        var progress = new CallbackProgress(_ => cts.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.CreateAsync(progress, cts.Token));
        Assert.Empty(await Service.ListAsync());
        Assert.Empty(Directory.GetDirectories(Root, ".work-*"));
    }
    private sealed class CallbackProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }
}
