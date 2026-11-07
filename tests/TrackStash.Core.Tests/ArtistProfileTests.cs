using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class ArtistProfileTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"artist-profile-{Guid.NewGuid():N}.db");
    private SqliteStorageProvider Provider => new(_path);
    public async Task InitializeAsync()
    {
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await Sql("INSERT INTO artist(artist_id,name) VALUES('a','Artist A'),('b','Artist B')");
    }
    private async Task Sql(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync(); using var command = connection.CreateCommand();
        command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); File.Delete(_path); return Task.CompletedTask; }
    [Fact]
    public async Task ProfileAndArtworkShareVersionsAndPermanentHistoryAndCaughtFailuresRemainAtomic()
    {
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var before = (await unit.Artists.GetProfileAsync("a"))!;
            var profile = new ArtistProfile { Biography = "Manual bio", BiographySourceUrl = "https://example.test/bio", Favourite = true, Notes = "Private notes" };
            var saved = await unit.Artists.SaveProfileAsync("a", before.Version, profile);
            Assert.Equal(profile.Biography, saved.Snapshot!.Profile.Biography); Assert.Equal(profile.BiographySourceUrl, saved.Snapshot.Profile.BiographySourceUrl);
            Assert.Equal(profile.Notes, saved.Snapshot.Profile.Notes); Assert.True(saved.Snapshot.Profile.Favourite);
            Assert.NotEqual(before.Version, saved.Snapshot.Version);
            Assert.Equal(saved.Snapshot.Version, (await unit.Artists.SaveProfileAsync("a", saved.Snapshot.Version, profile)).Snapshot!.Version);
            Assert.Equal(LabelUpdateStatus.Stale, (await unit.Artists.SaveProfileAsync("a", before.Version, new())).Status);
            var asset = new ArtworkAsset(Guid.NewGuid().ToString("N"), new string('A', 64), "image/png", 1, 1, 1, "portrait.png", null, null, "Permission", DateTimeOffset.UtcNow);
            await unit.Artwork.AddAssetAsync(asset);
            var entry = new ArtworkEntry(Guid.NewGuid().ToString("N"), asset.Id, "portrait", "Portrait", true);
            var gallery = await unit.Artwork.ReplaceArtistGalleryAsync("a", saved.Snapshot.Version, [entry]);
            Assert.NotEqual(saved.Snapshot.Version, gallery.Gallery!.Version);
            Assert.Equal(LabelUpdateStatus.Stale, (await unit.Artists.SaveProfileAsync("a", saved.Snapshot.Version, new())).Status);
            var other = (await unit.Artwork.GetArtistGalleryAsync("b"))!;
            await Assert.ThrowsAsync<ArgumentException>(() => unit.Artwork.ReplaceArtistGalleryAsync("b", other.Version, [entry]).AsTask());
            Assert.Empty((await unit.Artwork.GetArtistGalleryAsync("b"))!.Items);
            var analysis = await unit.EntityDelete!.AnalyzeDependenciesAsync("artist", "a", unit);
            Assert.Contains(analysis.Blockers, b => b.TableName == "artist_profile_revision");
            Assert.Contains(analysis.Blockers, b => b.TableName == "artist_artwork_revision");
            await unit.CommitAsync();
        }
        await Sql("CREATE TRIGGER fail_artist_profile BEFORE INSERT ON artist_profile_revision BEGIN SELECT RAISE(ABORT,'injected'); END");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var before = (await unit.Artists.GetProfileAsync("a"))!;
            await Assert.ThrowsAsync<SqliteException>(() => unit.Artists.SaveProfileAsync("a", before.Version, before.Profile with { Biography = "Failed edit" }).AsTask());
            Assert.Equal(before.Version, (await unit.Artists.GetProfileAsync("a"))!.Version);
            Assert.Equal("Manual bio", (await unit.Artists.GetProfileAsync("a"))!.Profile.Biography);
            await unit.CommitAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => Sql("DELETE FROM artist_profile_revision"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql("UPDATE artist_artwork_revision SET before_json='[]'"));
        await Sql("CREATE TRIGGER fail_artist_gallery BEFORE INSERT ON artist_artwork_revision BEGIN SELECT RAISE(ABORT,'injected'); END");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var before = (await unit.Artwork.GetArtistGalleryAsync("a"))!;
            await Assert.ThrowsAsync<SqliteException>(() => unit.Artwork.ReplaceArtistGalleryAsync("a", before.Version, []).AsTask());
            var after = (await unit.Artwork.GetArtistGalleryAsync("a"))!;
            Assert.Equal(before.Version, after.Version); Assert.Single(after.Items);
            await unit.CommitAsync();
        }
    }
    [Fact]
    public async Task MigrationFromElevenPreservesArtistsAndRejectsInvalidFields()
    {
        await Sql(LegacyCatalogSchema.RemoveEnrichmentSql);
        await Sql("DROP TABLE artist_profile; DROP TABLE artist_profile_revision; DROP TABLE artist_artwork_link; DROP TABLE artist_artwork_revision; DELETE FROM schema_migration WHERE version=12");
        var result = await Provider.Migrations.ApplyPendingMigrationsAsync();
        Assert.Equal(13, result.CurrentVersion); Assert.Equal(2, result.AppliedMigrations.Count);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        Assert.Equal("Artist A", (await unit.Artists.GetByIdAsync("a"))!.Name);
        var before = (await unit.Artists.GetProfileAsync("a"))!;
        await Assert.ThrowsAsync<ArgumentException>(() => unit.Artists.SaveProfileAsync("a", before.Version, new() { Biography = new string('x', 12001) }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => unit.Artists.SaveProfileAsync("a", before.Version, new() { BiographySourceUrl = "javascript:bad" }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => unit.Artists.SaveProfileAsync("a", before.Version, new() { ReviewStatus = "invalid" }).AsTask());
        Assert.Equal(LabelUpdateStatus.NotFound, (await unit.Artists.SaveProfileAsync("missing", before.Version, new())).Status);
    }
}
