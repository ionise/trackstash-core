using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class EnrichmentStorageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"enrichment-storage-{Guid.NewGuid():N}");
    private string Database => Path.Combine(_root, "catalog.db");
    private SqliteStorageProvider Provider => new(Database);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await Sql("INSERT INTO recording(recording_id,title,mix_name) VALUES('r','Preserved track','Original Mix'); INSERT INTO release(release_id,title) VALUES('l','Preserved release')");
    }
    private async Task<object?> Sql(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False;Foreign Keys=True");
        await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); return Task.CompletedTask;
    }
    [Fact]
    public async Task MigrationPreservesPopulatedSchema12AndFailureRollsBack()
    {
        var original = await Sql("SELECT version FROM recording WHERE recording_id='r'");
        await Sql(LegacyCatalogSchema.RemoveEnrichmentSql);
        Assert.Equal(12, await Provider.Migrations.GetCurrentVersionAsync());
        await Sql("CREATE TRIGGER fail_enrichment_migration BEFORE INSERT ON schema_migration WHEN NEW.version=13 BEGIN SELECT RAISE(ABORT,'injected'); END");
        await Assert.ThrowsAsync<SqliteException>(() => Provider.Migrations.ApplyPendingMigrationsAsync().AsTask());
        Assert.Equal(12, await Provider.Migrations.GetCurrentVersionAsync());
        Assert.Equal(0L, await Sql("SELECT count(*) FROM sqlite_master WHERE name='enrichment_review'"));
        await Sql("DROP TRIGGER fail_enrichment_migration");
        var result = await Provider.Migrations.ApplyPendingMigrationsAsync();
        Assert.Equal(13, result.CurrentVersion); Assert.Single(result.AppliedMigrations);
        Assert.Equal(original, await Sql("SELECT version FROM recording WHERE recording_id='r'"));
        Assert.Equal("Original Mix", await Sql("SELECT mix_name FROM recording WHERE recording_id='r'"));
        Assert.Empty((await Provider.Migrations.ApplyPendingMigrationsAsync()).AppliedMigrations);
    }
    [Fact]
    public async Task MetadataPersistsTypedFieldsBumpsVersionAndRejectsInvalidClassificationAndKinds()
    {
        var original = await Sql("SELECT version FROM recording WHERE recording_id='r'");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var metadata = new CatalogMusicalMetadata(124.5m, "11A", "Camelot", ["House"], ["Progressive House"], "Progressive House");
            await unit.Enrichment!.SaveMetadataAsync(CatalogEntityKind.Recording, "r", metadata);
            var saved = await unit.Enrichment.GetMetadataAsync(CatalogEntityKind.Recording, "r");
            Assert.Equal(metadata.Bpm, saved.Bpm); Assert.Equal(metadata.KeyNotation, saved.KeyNotation);
            Assert.Equal(metadata.Genres, saved.Genres); Assert.Equal(metadata.Subgenres, saved.Subgenres);
            Assert.Equal(metadata.PreferredGenre, saved.PreferredGenre);
            await Assert.ThrowsAsync<CatalogFoundationValidationException>(() => unit.Enrichment.SaveMetadataAsync(CatalogEntityKind.Recording, "r", metadata with { PreferredGenre = "Unlisted genre" }).AsTask());
            await Assert.ThrowsAsync<CatalogFoundationValidationException>(() => unit.Enrichment.SaveMetadataAsync(CatalogEntityKind.Release, "l", metadata).AsTask());
            await unit.CommitAsync();
        }
        Assert.NotEqual(original, await Sql("SELECT version FROM recording WHERE recording_id='r'"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql("DELETE FROM recording WHERE recording_id='r'"));
    }
    [Fact]
    public async Task ReviewHistoryIsImmutableAndSchema13BackupRestoresMetadataAndEvidence()
    {
        var review = new StoredEnrichmentReview("review", CatalogEntityKind.Recording, "r", "frozen", """{"evidence":"original SonoVault response"}""", DateTimeOffset.UtcNow);
        var audit = new EnrichmentApplyAudit("review", """["Genre"]""", "Reviewed evidence", """{"genres":[]}""", """{"genres":["House"]}""", review.CreatedAt);
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.Enrichment!.SaveMetadataAsync(CatalogEntityKind.Recording, "r", new(Genres: ["House"], Subgenres: []));
            await unit.Enrichment.SaveReviewAsync(review); await unit.Enrichment.SaveAuditAsync(audit);
            var analysis = await unit.EntityDelete!.AnalyzeDependenciesAsync("recording", "r", unit);
            Assert.Contains(analysis.Blockers, blocker => blocker.TableName == "enrichment_review");
            Assert.Contains(analysis.Blockers, blocker => blocker.TableName == "recording_musical_metadata");
            await unit.CommitAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => Sql("DELETE FROM enrichment_review"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql("UPDATE enrichment_apply_revision SET reason='changed'"));
        var artwork = new ManagedArtworkStore(Path.Combine(_root, "artwork"));
        var backup = new CatalogBackupService(new SqliteCatalogSnapshotSource(Database), artwork, Path.Combine(_root, "backups"));
        var result = await backup.CreateAsync();
        var archive = Path.Combine(_root, "backups", result.Backup.Filename);
        Assert.Equal(13, (await backup.VerifyAsync(archive)).SchemaVersion);
        var restore = Path.Combine(_root, "restored");
        await backup.RestoreAsync(archive, restore);
        await using var check = await new SqliteStorageProvider(Path.Combine(restore, "catalog.db")).BeginUnitOfWorkAsync();
        Assert.Equal(review, await check.Enrichment!.GetReviewAsync("review"));
        Assert.Equal(audit, await check.Enrichment.GetAuditAsync("review"));
        Assert.Equal(["House"], (await check.Enrichment.GetMetadataAsync(CatalogEntityKind.Recording, "r")).Genres);
    }
}
