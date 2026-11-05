using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class CatalogFoundationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catalog-foundations-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "catalog.db");
    private SqliteStorageProvider Provider => new(Database);
    private static string Id() => Guid.NewGuid().ToString("N");
    private readonly string _artist = Id(), _release = Id(), _recording = Id();
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await Provider.Migrations.ApplyPendingMigrationsAsync();
    }
    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }
    private async Task<object?> Sql(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Foreign Keys=True");
        await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    private async Task Seed(IUnitOfWork unit)
    {
        var foundations = unit.CatalogFoundations!;
        await foundations.SaveEntityAsync(new Artist { Id = _artist, Name = "Compound artist", IdentityStatus = "unresolved_compound" }, null);
        await foundations.SaveEntityAsync(new Release { Id = _release, Title = "Test release", CatalogNumber = "CAT001", Upc = "00123", PublishDate = "2020-02-29" }, null);
        await foundations.SaveEntityAsync(new Recording { Id = _recording, Title = "Track", MixName = "Original Mix", Isrc = "TEST001", DurationMs = 123456 }, null);
    }
    private CatalogCredit Credit(string role, int position = 1) => new(Id(), _artist, role, position, "Original credit expression",
        "beatport", $"artists[{position - 1}]", "Compound artist", "{\"review\":\"accepted source\"}");
    private ReleasePlacement Placement(int order, string? externalId = null) => new(Id(), _release, _recording, "", order,
        order, "source", null, null, [Credit("primary"), Credit("remixer")],
        externalId is null ? [] : [new EntityReference { Source = "beatport", ExternalId = externalId }]);

    [Fact]
    public async Task ConditionalEntitiesPersistMetadataWithoutInferringArtistKindOrChangingIdentity()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Seed(unit);
        var artist = (await unit.Artists.GetByIdAsync(_artist))!;
        var release = (await unit.Releases.GetByIdAsync(_release))!;
        var recording = (await unit.Recordings.GetByIdAsync(_recording))!;
        Assert.Equal("unknown", artist.Kind); Assert.Equal("unresolved_compound", artist.IdentityStatus); Assert.NotEmpty(artist.Version);
        Assert.Equal("CAT001", release.CatalogNumber); Assert.Equal("00123", release.Upc); Assert.Equal("2020-02-29", release.PublishDate);
        Assert.Equal(123456, recording.DurationMs);
        await unit.CatalogFoundations!.SaveEntityAsync(artist with { Name = "Curated expression" }, artist.Version);
        await Assert.ThrowsAsync<CatalogFoundationStaleException>(() => unit.CatalogFoundations.SaveEntityAsync(artist, artist.Version).AsTask());
        await Assert.ThrowsAsync<CatalogFoundationConflictException>(() => unit.CatalogFoundations.SaveEntityAsync(artist, null).AsTask());
        Assert.Equal("Curated expression", (await unit.Artists.GetByIdAsync(_artist))!.Name);
        await unit.CommitAsync();
    }

    [Fact]
    public async Task MultiRoleAndRepeatedCreditOccurrencesSurviveIndependentScopesAndLegacyRoundTrip()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Seed(unit);
        var foundations = unit.CatalogFoundations!;
        foreach (var kind in new[] { CatalogEntityKind.Release, CatalogEntityKind.Recording })
        {
            var id = kind == CatalogEntityKind.Release ? _release : _recording;
            foreach (var credit in new[] { Credit("primary"), Credit("remixer"), Credit("primary", 2) })
                await foundations.SaveCreditAsync(kind, id, (await foundations.GetMetadataAsync(kind, id))!.Version, credit);
            Assert.Equal(3, (await foundations.GetCreditsAsync(kind, id)).Count);
        }
        var release = (await unit.Releases.GetByIdAsync(_release))!;
        var recording = (await unit.Recordings.GetByIdAsync(_recording))!;
        Assert.All(release.ArtistCredits, c => Assert.NotNull(c.Detail));
        await unit.Releases.UpsertAsync(release);
        await unit.Recordings.UpsertAsync(recording);
        Assert.Equal(3, (await foundations.GetCreditsAsync(CatalogEntityKind.Release, _release)).Count);
        Assert.Equal(3, (await foundations.GetCreditsAsync(CatalogEntityKind.Recording, _recording)).Count);
        Assert.All(await foundations.GetCreditsAsync(CatalogEntityKind.Release, _release), c => Assert.Equal("Compound artist", c.SourceExpression));
        await unit.CommitAsync();
    }

    [Fact]
    public async Task RepeatedRecordingPlacementsSupportManualOrderingPreserveEvidenceAndVersions()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Seed(unit);
        var foundations = unit.CatalogFoundations!;
        foreach (var placement in new[] { Placement(1, "101"), Placement(2, "102") })
            await foundations.SavePlacementAsync(placement, (await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release))!.Version);
        var before = await foundations.GetPlacementsAsync(_release);
        Assert.Equal(2, before.Count); Assert.Equal(before[0].RecordingId, before[1].RecordingId);
        Assert.All(before, p => Assert.Equal(2, p.Credits.Count));
        var version = (await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release))!.Version;
        await foundations.ReorderPlacementsAsync(_release, version, before.Select(p => p.Id).Reverse().ToArray());
        var after = await foundations.GetPlacementsAsync(_release);
        Assert.Equal(before.Select(p => p.Id).Reverse(), after.Select(p => p.Id));
        Assert.Equal(new int?[] { 2, 1 }, after.Select(p => p.SourcePosition));
        Assert.All(after, p => { Assert.Equal("manual", p.OrderSource); Assert.Null(p.TrackNumber); Assert.Null(p.DiscNumber); });
        Assert.NotEqual(before[0].Version, after.Single(p => p.Id == before[0].Id).Version);
        await Assert.ThrowsAsync<CatalogFoundationStaleException>(() => foundations.ReorderPlacementsAsync(_release, version, before.Select(p => p.Id).ToArray()).AsTask());
        var recording = (await unit.Recordings.GetByIdAsync(_recording))!;
        Assert.All(recording.ReleaseLinks, l => Assert.NotNull(l.Placement));
        await unit.Recordings.UpsertAsync(recording);
        var roundTrip = await foundations.GetPlacementsAsync(_release);
        Assert.Equal(after.Select(p => (p.Id, p.DisplayOrder, p.SourcePosition, p.OrderSource)), roundTrip.Select(p => (p.Id, p.DisplayOrder, p.SourcePosition, p.OrderSource)));
        await unit.CommitAsync();
    }

    [Fact]
    public async Task ProviderPlacementConflictRollsBackAllWritesEvenWhenCallerCatchesAndCommits()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Seed(unit);
        var foundations = unit.CatalogFoundations!;
        var first = Placement(1, "101");
        await foundations.SavePlacementAsync(first, (await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release))!.Version);
        var before = await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release);
        await Assert.ThrowsAsync<CatalogFoundationConflictException>(() => foundations.SavePlacementAsync(Placement(2, "101"), before!.Version).AsTask());
        Assert.Equal(before, await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release));
        Assert.Single(await foundations.GetPlacementsAsync(_release));
        await unit.CommitAsync();
        Assert.Equal(2L, await Sql("SELECT count(*) FROM placement_artist_credit"));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM catalog_foundation_revision WHERE entity_type='placement'"));
    }

    [Fact]
    public async Task InvalidEntityMetadataAndConflictingProviderReferenceRollBackEntityCreation()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Seed(unit);
        var foundations = unit.CatalogFoundations!;
        var id = Id();
        await Assert.ThrowsAsync<CatalogFoundationValidationException>(() =>
            foundations.SaveEntityAsync(new Release { Id = id, Title = "Invalid", PublishDate = "2020-02-30" }, null).AsTask());
        Assert.Null(await unit.Releases.GetByIdAsync(id));
        await unit.Artists.UpsertAsync(new Artist { Id = _artist, Name = "Original", ExternalReferences = [new() { Source = "beatport", ExternalId = "123" }] });
        await Assert.ThrowsAsync<SqliteException>(() => foundations.SaveEntityAsync(new Artist
        {
            Id = id, Name = "Competing", ExternalReferences = [new() { Source = "beatport", ExternalId = "123" }],
        }, null).AsTask());
        Assert.Null(await unit.Artists.GetByIdAsync(id));
        Assert.Equal([_artist], await foundations.FindReferenceOwnersAsync(CatalogEntityKind.Artist, "beatport", "123"));
        await unit.CommitAsync();
    }

    [Fact]
    public async Task CrossAggregateCreditIdAndInvalidPermutationCannotMutateAcceptedData()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Seed(unit);
        var foundations = unit.CatalogFoundations!;
        var other = Id();
        await foundations.SaveEntityAsync(new Release { Id = other, Title = "Other" }, null);
        var credit = Credit("primary");
        await foundations.SaveCreditAsync(CatalogEntityKind.Release, _release, (await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release))!.Version, credit);
        var otherVersion = (await foundations.GetMetadataAsync(CatalogEntityKind.Release, other))!.Version;
        await Assert.ThrowsAsync<CatalogFoundationConflictException>(() => foundations.SaveCreditAsync(CatalogEntityKind.Release, other,
            otherVersion, credit).AsTask());
        await foundations.SavePlacementAsync(Placement(1), (await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release))!.Version);
        var releaseVersion = (await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release))!.Version;
        await Assert.ThrowsAsync<CatalogFoundationValidationException>(() => foundations.ReorderPlacementsAsync(_release,
            releaseVersion, []).AsTask());
        Assert.Single(await foundations.GetCreditsAsync(CatalogEntityKind.Release, _release));
        await unit.CommitAsync();
    }

    [Fact]
    public async Task PopulatedVersionEightMigrationPreservesLegacyDataAndAmbiguousBindingsWithoutSilentlyChoosing()
    {
        await LegacyCatalogSchema.RevertEmptyFoundationsAsync(Database);
        await Sql("""
            INSERT INTO artist(artist_id,name) VALUES ('legacy-a','Act'),('legacy-b','Act');
            INSERT INTO artist_external_ref(artist_id,source,external_id) VALUES ('legacy-a','beatport','99'),('legacy-b','beatport','99');
            INSERT INTO release(release_id,title) VALUES ('legacy-release','Release');
            INSERT INTO recording(recording_id,title,isrc) VALUES ('legacy-recording','Track','TEST001');
            INSERT INTO release_artist_credit VALUES ('legacy-release','legacy-a','Credited release name',1);
            INSERT INTO recording_artist_credit VALUES ('legacy-recording','legacy-a','Credited recording name','remixer',2);
            INSERT INTO release_recording VALUES ('legacy-release','legacy-recording',1,7);
            """);
        var result = await Provider.Migrations.ApplyPendingMigrationsAsync();
        Assert.Equal(9, result.CurrentVersion); Assert.Single(result.AppliedMigrations);
        Assert.Empty((await Provider.Migrations.ApplyPendingMigrationsAsync()).AppliedMigrations);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var artist = (await unit.Artists.GetByIdAsync("legacy-a"))!;
        Assert.Equal("Act", artist.Name); Assert.Equal("unknown", artist.IdentityStatus); Assert.NotEmpty(artist.Version);
        Assert.Equal(2, (await unit.CatalogFoundations!.FindReferenceOwnersAsync(CatalogEntityKind.Artist, "beatport", "99")).Count);
        await Assert.ThrowsAsync<CatalogFoundationConflictException>(() => unit.Artists.GetByExternalRefAsync("beatport", "99").AsTask());
        var release = (await unit.Releases.GetByIdAsync("legacy-release"))!;
        Assert.Equal("Credited release name", Assert.Single(release.ArtistCredits).CreditName);
        var recording = (await unit.Recordings.GetByIdAsync("legacy-recording"))!;
        Assert.Equal("remixer", Assert.Single(recording.ArtistCredits).Role);
        var placement = Assert.Single(await unit.CatalogFoundations.GetPlacementsAsync("legacy-release"));
        Assert.Equal(7, placement.TrackNumber); Assert.Equal(1, placement.DiscNumber); Assert.Equal("legacy", placement.OrderSource);
        Assert.Empty(placement.ExternalReferences);
        await unit.CommitAsync();
        Assert.Equal(0L, await Sql("SELECT count(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task FailedMigrationNineLeavesSchemaEightAndOriginalRowsIntact()
    {
        await LegacyCatalogSchema.RevertEmptyFoundationsAsync(Database);
        await Sql("INSERT INTO artist(artist_id,name) VALUES ('old','Keep')");
        await Sql("CREATE TRIGGER fail_foundation_migration BEFORE INSERT ON schema_migration WHEN NEW.version=9 BEGIN SELECT RAISE(ABORT,'Injected failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => Provider.Migrations.ApplyPendingMigrationsAsync().AsTask());
        Assert.Equal(8, await Provider.Migrations.GetCurrentVersionAsync());
        Assert.Equal("Keep", await Sql("SELECT name FROM artist WHERE artist_id='old'"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM pragma_table_info('artist') WHERE name='version'"));
        await Sql("DROP TRIGGER fail_foundation_migration");
        Assert.Equal(9, (await Provider.Migrations.ApplyPendingMigrationsAsync()).CurrentVersion);
    }

    [Fact]
    public async Task BackupRestoresStablePlacementsCreditsMetadataVersionsAndPermanentHistory()
    {
        string releaseVersion;
        IReadOnlyList<ReleasePlacement> placements;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await Seed(unit);
            var foundations = unit.CatalogFoundations!;
            await foundations.SavePlacementAsync(Placement(1, "101"), (await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release))!.Version);
            releaseVersion = (await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release))!.Version;
            placements = await foundations.GetPlacementsAsync(_release);
            var analysis = await unit.EntityDelete!.AnalyzeDependenciesAsync("release", _release, unit);
            Assert.Contains(analysis.Blockers, b => b.TableName == "catalog_foundation_revision");
            await unit.CommitAsync();
        }
        var service = new CatalogBackupService(new SqliteCatalogSnapshotSource(Database), new ManagedArtworkStore(Path.Combine(_root, "artwork")), Path.Combine(_root, "backups"));
        var result = await service.CreateAsync();
        var archive = Path.Combine(_root, "backups", result.Backup.Filename);
        Assert.Equal(9, (await service.VerifyAsync(archive)).SchemaVersion);
        var restored = Path.Combine(_root, "restored");
        await service.RestoreAsync(archive, restored);
        await using var check = await new SqliteStorageProvider(Path.Combine(restored, "catalog.db")).BeginUnitOfWorkAsync();
        Assert.Equal(releaseVersion, (await check.Releases.GetByIdAsync(_release))!.Version);
        var actual = Assert.Single(await check.CatalogFoundations!.GetPlacementsAsync(_release));
        Assert.Equal(placements[0].Id, actual.Id); Assert.Equal(placements[0].Version, actual.Version);
        Assert.Equal(placements[0].Credits, actual.Credits);
        await check.CommitAsync();
        await Assert.ThrowsAsync<SqliteException>(() => Sql("DELETE FROM catalog_foundation_revision"));
    }

    [Fact]
    public async Task AuditFailureRollsBackMetadataAndTokensAndLegacyCleanupCannotEraseReviewedState()
    {
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await Seed(unit); await unit.CommitAsync();
        }
        await Sql("CREATE TRIGGER fail_foundation_audit BEFORE INSERT ON catalog_foundation_revision BEGIN SELECT RAISE(ABORT,'Injected failure'); END");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var foundations = unit.CatalogFoundations!;
            var before = (await foundations.GetMetadataAsync(CatalogEntityKind.Artist, _artist))!;
            await Assert.ThrowsAsync<SqliteException>(() => foundations.UpdateMetadataAsync(before with { IdentityStatus = "accepted" }).AsTask());
            Assert.Equal(before, await foundations.GetMetadataAsync(CatalogEntityKind.Artist, _artist));
            await Assert.ThrowsAsync<CatalogFoundationConflictException>(() => unit.EntityDelete!.DeleteOwnedRowsAsync("artist", _artist).AsTask());
            await unit.CommitAsync();
        }
        await Sql("DROP TRIGGER fail_foundation_audit");
    }

    [Fact]
    public async Task SchemaEightArchiveRemainsVerifiableAndRestorableAfterFoundationUpgrade()
    {
        await LegacyCatalogSchema.RevertEmptyFoundationsAsync(Database);
        var service = new CatalogBackupService(new SqliteCatalogSnapshotSource(Database), new ManagedArtworkStore(Path.Combine(_root, "artwork")), Path.Combine(_root, "backups"));
        var old = await service.CreateAsync();
        var archive = Path.Combine(_root, "backups", old.Backup.Filename);
        Assert.Equal(8, (await service.VerifyAsync(archive)).SchemaVersion);
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        Assert.Equal(8, (await service.VerifyAsync(archive)).SchemaVersion);
        var restored = Path.Combine(_root, "schema8");
        await service.RestoreAsync(archive, restored);
        var provider = new SqliteStorageProvider(Path.Combine(restored, "catalog.db"));
        Assert.Equal(8, await provider.Migrations.GetCurrentVersionAsync());
        Assert.Equal(9, (await provider.Migrations.ApplyPendingMigrationsAsync()).CurrentVersion);
    }

    [Fact]
    public async Task LegacyWritersCannotDuplicateStableCreditsOrRepeatedPlacementsWhenDetailIsLost()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Seed(unit);
        var foundations = unit.CatalogFoundations!;
        await foundations.SaveCreditAsync(CatalogEntityKind.Recording, _recording,
            (await foundations.GetMetadataAsync(CatalogEntityKind.Recording, _recording))!.Version, Credit("primary"));
        await foundations.SavePlacementAsync(Placement(1), (await foundations.GetMetadataAsync(CatalogEntityKind.Release, _release))!.Version);
        var before = (await unit.Recordings.GetByIdAsync(_recording))!;
        await Assert.ThrowsAsync<CatalogFoundationConflictException>(() => unit.Recordings.UpsertAsync(new Recording
        {
            Id = _recording, Title = "Incorrect overwrite", ArtistCredits = [new() { ArtistId = _artist, Role = "primary" }],
        }).AsTask());
        await Assert.ThrowsAsync<CatalogFoundationConflictException>(() => unit.Recordings.UpsertAsync(new Recording
        {
            Id = _recording, Title = "Incorrect overwrite", ReleaseLinks = [new() { ReleaseId = _release }],
        }).AsTask());
        Assert.Equal(before.Version, (await unit.Recordings.GetByIdAsync(_recording))!.Version);
        Assert.Equal("Track", (await unit.Recordings.GetByIdAsync(_recording))!.Title);
        Assert.Single(await foundations.GetCreditsAsync(CatalogEntityKind.Recording, _recording));
        Assert.Single(await foundations.GetPlacementsAsync(_release));
        await unit.CommitAsync();
    }
}
