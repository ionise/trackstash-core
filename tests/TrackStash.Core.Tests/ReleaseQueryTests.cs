using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class ReleaseQueryTests
{
    [Fact]
    public async Task QueriesAreLiteralStableLabelScopedAndDoNotMultiplyMultiLabelReleases()
    {
        var path = Path.Combine(Path.GetTempPath(), $"release-query-{Guid.NewGuid():N}.db");
        try
        {
            var provider = new SqliteStorageProvider(path); await provider.Migrations.ApplyPendingMigrationsAsync();
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO label(label_id,name) VALUES('a','Primary'),('b','Secondary');
                    INSERT INTO release(release_id,title,catalog_number) VALUES
                      ('first','Release 100%','CAT_01'),('second','release 100%','CAT_02'),('third','Other','CAT_03');
                    INSERT INTO release_label_link(release_id,label_id,is_primary) VALUES
                      ('first','a',1),('first','b',0),('second','b',1);
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using var unit = await provider.BeginUnitOfWorkAsync();
            var filtered = await unit.Releases.QueryAsync(new(LabelId: "b"));
            Assert.Equal(2, filtered.Total);
            Assert.Equal(["first", "second"], filtered.Items.Select(r => r.Id).ToArray());
            Assert.Equal("a", filtered.Items[0].LabelId);
            var page = await unit.Releases.QueryAsync(new(Page: 2, PageSize: 1, LabelId: "b"));
            Assert.Equal("second", Assert.Single(page.Items).Id);
            Assert.Equal(2, (await unit.Releases.QueryAsync(new(Search: "%"))).Total);
            Assert.Equal(3, (await unit.Releases.QueryAsync(new(Search: "_"))).Total);
            Assert.Empty((await unit.Releases.QueryAsync(new(Page: int.MaxValue, PageSize: 100))).Items);
            Assert.Equal(3, await unit.Releases.CountAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
