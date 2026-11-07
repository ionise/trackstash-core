using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class ReleaseImportMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationTenPreservesPopulatedNineAndRollsBackWholeMigrationOnFailure(bool fail)
    {
        var path = Path.Combine(Path.GetTempPath(), $"import-migration-{Guid.NewGuid():N}.db");
        try
        {
            var provider = new SqliteStorageProvider(path);
            await provider.Migrations.ApplyPendingMigrationsAsync();
            var artistId = Guid.NewGuid().ToString("N");
            string version;
            await using (var unit = await provider.BeginUnitOfWorkAsync())
            {
                await unit.CatalogFoundations!.SaveEntityAsync(new Artist { Id = artistId, Name = "Preserved Artist",
                    IdentityStatus = "unresolved_compound", ExternalReferences = [new() { Source = "beatport", ExternalId = "1" }] }, null);
                version = (await unit.Artists.GetByIdAsync(artistId))!.Version;
                await unit.CommitAsync();
            }
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = LegacyCatalogSchema.RemoveEnrichmentSql;
                await command.ExecuteNonQueryAsync();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND (name LIKE 'import_stamp_%' OR name LIKE 'embedding_invalidate_%')";
                var names = new List<string>();
                await using (var reader = await command.ExecuteReaderAsync())
                    while (await reader.ReadAsync()) names.Add(reader.GetString(0));
                foreach (var name in names) { command.CommandText = $"DROP TRIGGER \"{name}\""; await command.ExecuteNonQueryAsync(); }
                command.CommandText = """
                    DROP TABLE release_artwork_link; DROP TABLE release_artwork_revision;
                    DROP TABLE artist_artwork_link; DROP TABLE artist_artwork_revision; DROP TABLE artist_profile; DROP TABLE artist_profile_revision;
                    DROP TABLE release_import_binding; DROP TABLE release_import_outcome; DROP TABLE release_import_supersession;
                    DROP TABLE release_import_preview; DROP TABLE release_import_state;
                    DROP TABLE catalog_embedding_outbox; DROP TABLE catalog_embedding_source;
                    DELETE FROM schema_migration WHERE version>=10;
                    """;
                await command.ExecuteNonQueryAsync();
                if (fail)
                {
                    command.CommandText = "CREATE TRIGGER fail_import_migration BEFORE INSERT ON schema_migration WHEN NEW.version=10 BEGIN SELECT RAISE(ABORT,'injected'); END";
                    await command.ExecuteNonQueryAsync();
                    await Assert.ThrowsAsync<SqliteException>(() => provider.Migrations.ApplyPendingMigrationsAsync().AsTask());
                    Assert.Equal(9, await provider.Migrations.GetCurrentVersionAsync());
                    command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='release_import_preview'";
                    Assert.Equal(0L, await command.ExecuteScalarAsync());
                    command.CommandText = "DROP TRIGGER fail_import_migration"; await command.ExecuteNonQueryAsync();
                }
            }
            var migrated = await provider.Migrations.ApplyPendingMigrationsAsync();
            Assert.Equal(13, migrated.CurrentVersion); Assert.Equal(4, migrated.AppliedMigrations.Count);
            await using var check = await provider.BeginUnitOfWorkAsync();
            var artist = (await check.Artists.GetByIdAsync(artistId))!;
            Assert.Equal(version, artist.Version); Assert.Equal("Preserved Artist", artist.Name); Assert.Equal("unresolved_compound", artist.IdentityStatus);
            Assert.Single(artist.ExternalReferences); Assert.NotEmpty(await check.ReleaseImports!.GetCatalogStampAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
