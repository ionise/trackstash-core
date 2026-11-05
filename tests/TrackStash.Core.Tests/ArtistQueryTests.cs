using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class ArtistQueryTests
{
    [Fact]
    public async Task ArtistQueriesPreserveIdentityScopesRepeatedPlacementsAndLiteralStableLabelFiltering()
    {
        var path = Path.Combine(Path.GetTempPath(), $"artist-query-{Guid.NewGuid():N}.db");
        try
        {
            var provider = new SqliteStorageProvider(path); await provider.Migrations.ApplyPendingMigrationsAsync();
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO label(label_id,name) VALUES('label','Label'),('secondary','Secondary');
                    INSERT INTO artist(artist_id,name,artist_kind,identity_status) VALUES
                      ('a','Same 100%','person','accepted'),('b','same 100%','unknown','unresolved_compound'),
                      ('c','Recording artist','unknown','unknown'),('d','Uncredited','unknown','unknown');
                    INSERT INTO artist_alias(artist_id,value) VALUES('a','Alias_One');
                    INSERT INTO release(release_id,title) VALUES('r','Release');
                    INSERT INTO release_label_link(release_id,label_id,is_primary) VALUES('r','label',1),('r','secondary',0);
                    INSERT INTO recording(recording_id,title) VALUES('t','Track'),('unplaced','Unplaced track');
                    INSERT INTO release_recording(placement_id,release_id,recording_id,display_order,legacy) VALUES
                      ('p1','r','t',1,0),('p2','r','t',2,0);
                    INSERT INTO release_artist_credit(credit_id,release_id,artist_id,role,position,credit_name) VALUES
                      ('rc','r','a','primary',1,'Credited A');
                    INSERT INTO placement_artist_credit(credit_id,placement_id,artist_id,role,position) VALUES
                      ('pc1','p1','b','remixer',1),('pc2','p2','b','remixer',1);
                    INSERT INTO recording_artist_credit(credit_id,recording_id,artist_id,role,position) VALUES
                      ('tc','t','c','primary',1),('uc','unplaced','a','composer',1);
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using var unit = await provider.BeginUnitOfWorkAsync();
            var all = await unit.Artists.QueryAsync(new());
            Assert.Equal(4, all.Total);
            var filtered = await unit.Artists.QueryAsync(new(LabelId: "secondary"));
            Assert.Equal(3, filtered.Total); Assert.Equal(3, filtered.Items.Select(a => a.Id).Distinct().Count());
            var remixers = await unit.Artists.QueryAsync(new(LabelId: "label", Role: "remixer"));
            Assert.Equal("b", Assert.Single(remixers.Items).Id); Assert.Equal("unresolved_compound", remixers.Items[0].IdentityStatus);
            Assert.Equal(["remixer"], remixers.Items[0].Roles);
            var first = await unit.Artists.QueryAsync(new(PageSize: 1, Search: "%"));
            var second = await unit.Artists.QueryAsync(new(Page: 2, PageSize: 1, Search: "%"));
            Assert.Equal(2, first.Total); Assert.Equal("a", Assert.Single(first.Items).Id); Assert.Equal("b", Assert.Single(second.Items).Id);
            Assert.Equal("a", Assert.Single((await unit.Artists.QueryAsync(new(Search: "_"))).Items).Id);
            Assert.Empty((await unit.Artists.QueryAsync(new(LabelId: "missing"))).Items);
            Assert.Empty((await unit.Artists.QueryAsync(new(Page: int.MaxValue, PageSize: 100))).Items);
            var tracks = await unit.Artists.CreditsAsync(new("c", true));
            Assert.Equal(2, tracks.Total); Assert.Equal(new string?[] { "p1", "p2" }, tracks.Items.Select(c => c.PlacementId).ToArray());
            Assert.All(tracks.Items, c => Assert.Equal("recording", c.Scope));
            Assert.Single((await unit.Artists.CreditsAsync(new("c", false))).Items);
            Assert.Equal(2, (await unit.Artists.CreditsAsync(new("b", true))).Total);
            var unplaced = Assert.Single((await unit.Artists.CreditsAsync(new("a", true))).Items);
            Assert.Null(unplaced.ReleaseId); Assert.Null(unplaced.PlacementId); Assert.Equal("Unplaced track", unplaced.TrackTitle);
            Assert.Equal("Credited A", Assert.Single((await unit.Artists.CreditsAsync(new("a", false))).Items).CreditedName);
            Assert.Empty((await unit.Artists.CreditsAsync(new("d", true))).Items);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
