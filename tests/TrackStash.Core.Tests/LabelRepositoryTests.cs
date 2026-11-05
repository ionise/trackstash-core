using Microsoft.Data.Sqlite;
using TrackStash.Core.Normalization;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class LabelRepositoryTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"label-repository-{Guid.NewGuid():N}.db");
    private SqliteStorageProvider Provider => new(_path);

    public async Task InitializeAsync() => await Provider.Migrations.ApplyPendingMigrationsAsync();

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return Task.CompletedTask;
    }

    private static Label MakeLabel(string id, string name = "Same Name") => new()
    {
        Id = id, Name = name, NormalizedName = EntityNameNormalizer.NormalizeStrict(name),
        SortName = "Original sort", SourcePayloadJson = """{"preserve":true}""",
        CreatedUtc = DateTimeOffset.Parse("2025-01-02T03:04:05Z"),
        Aliases = [new EntityAlias { Value = "Café %_ Archive", IsPrimary = true }],
        ExternalReferences = [new EntityReference { Source = "beatport", ExternalId = "42", PayloadJson = "{}" }],
    };

    private async Task<Label> SeedAsync(string id = "legacy-label", string name = "Same Name")
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await unit.Labels.UpsertAsync(MakeLabel(id, name));
        var stored = await unit.Labels.GetByIdAsync(id);
        await unit.CommitAsync();
        return Assert.IsType<Label>(stored);
    }

    [Fact]
    public async Task Query_PagesDistinctLabelsAndReturnsFilteredTotal()
    {
        await SeedAsync("b");
        await SeedAsync("a");
        await SeedAsync("c", "Unrelated");
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var query = new LabelQuery { NormalizedName = "samename", PageSize = 1 };
        var first = await unit.Labels.QueryAsync(query);
        var second = await unit.Labels.QueryAsync(query with { Page = 2 });
        var beyond = await unit.Labels.QueryAsync(query with { Page = 3 });
        Assert.Equal(2, first.Total);
        Assert.Equal("a", Assert.Single(first.Items).Id);
        Assert.Equal("b", Assert.Single(second.Items).Id);
        Assert.Equal(2, beyond.Total);
        Assert.Empty(beyond.Items);
        Assert.Equal(1, first.Page);
        Assert.Equal(1, first.PageSize);
        Assert.NotEmpty(first.Items[0].Version);
        var ambiguous = await unit.Labels.QueryAsync(new LabelQuery { Source = "beatport", ExternalId = "42" });
        Assert.Equal(3, ambiguous.Total);
        Assert.Equal(3, ambiguous.Items.Count);
    }

    [Fact]
    public async Task Query_SearchesUnnormalizedAliasesUsingSharedNormalization()
    {
        await SeedAsync();
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var page = await unit.Labels.QueryAsync(new LabelQuery { Search = "CAFE archive", Source = "beatport" });
        Assert.Single(page.Items);
        Assert.Equal(1, page.Total);
        var missing = await unit.Labels.QueryAsync(new LabelQuery { Source = "other" });
        Assert.Empty(missing.Items);
        Assert.Equal(0, missing.Total);
        var injection = await unit.Labels.QueryAsync(new LabelQuery { Search = "' OR 1=1 --" });
        Assert.Empty(injection.Items);
    }

    [Fact]
    public async Task Query_SupportsAllSortsAndKeepsAscendingIdTieBreaker()
    {
        await SeedAsync("a", "Alpha");
        await SeedAsync("b", "Beta");
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        foreach (var sort in Enum.GetValues<LabelSortField>())
        {
            var result = await unit.Labels.QueryAsync(new LabelQuery { SortBy = sort, Descending = true });
            Assert.Equal(sort == LabelSortField.SortName ? ["a", "b"] : new[] { "b", "a" },
                result.Items.Select(x => x.Id).ToArray());
        }
        var largeOffset = await unit.Labels.QueryAsync(new LabelQuery { Page = int.MaxValue, PageSize = 100 });
        Assert.Empty(largeOffset.Items);
        Assert.Equal(2, largeOffset.Total);
    }

    [Fact]
    public async Task Query_EnforcesMaximumPageShapeWithoutDuplicatingAliasMatches()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        for (var index = 0; index < 101; index++)
            await unit.Labels.UpsertAsync(new Label
            {
                Id = $"label-{index:D3}", Name = "Match",
                Aliases = [new EntityAlias { Value = "Match one" }, new EntityAlias { Value = "Match two" }],
            });
        var first = await unit.Labels.QueryAsync(new LabelQuery { Search = "match", PageSize = 100 });
        var second = await unit.Labels.QueryAsync(new LabelQuery { Search = "match", PageSize = 100, Page = 2 });
        Assert.Equal(101, first.Total);
        Assert.Equal(100, first.Items.Count);
        Assert.Equal(101, second.Total);
        Assert.Single(second.Items);
        Assert.Equal(101, first.Items.Concat(second.Items).Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public async Task Create_DoesNotOverwriteExistingIdentityOrChildren()
    {
        var original = await SeedAsync();
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        Assert.False(await unit.Labels.TryCreateAsync(MakeLabel(original.Id, "Changed")));
        var unchanged = Assert.IsType<Label>(await unit.Labels.GetByIdAsync(original.Id));
        Assert.Equal(original, unchanged with { Aliases = original.Aliases, ExternalReferences = original.ExternalReferences });
        Assert.True(await unit.Labels.TryCreateAsync(MakeLabel("new-id")));
        var created = Assert.IsType<Label>(await unit.Labels.GetByIdAsync("new-id"));
        Assert.Single(created.Aliases);
        Assert.Single(created.ExternalReferences);
        Assert.NotEmpty(created.Version);
        await unit.CommitAsync();
    }

    [Fact]
    public async Task Update_PreservesUnspecifiedStateAndRejectsStaleAndMissing()
    {
        var original = await SeedAsync();
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var result = await unit.Labels.UpdateAsync(original.Id, original.Version,
                new LabelUpdate { SetName = true, Name = " New Label! " });
            Assert.Equal(LabelUpdateStatus.Updated, result.Status);
            var updated = Assert.IsType<Label>(result.Label);
            Assert.Equal("New Label!", updated.Name);
            Assert.Equal("newlabel", updated.NormalizedName);
            Assert.Equal(original.SortName, updated.SortName);
            Assert.Equal(original.CreatedUtc, updated.CreatedUtc);
            Assert.Equal(original.SourcePayloadJson, updated.SourcePayloadJson);
            Assert.Equal(original.Aliases, updated.Aliases);
            Assert.Equal(original.ExternalReferences, updated.ExternalReferences);
            Assert.NotEqual(original.Version, updated.Version);
            Assert.NotNull(updated.UpdatedUtc);
            await unit.CommitAsync();
        }
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var patch = new LabelUpdate { SetSortName = true };
            Assert.Equal(LabelUpdateStatus.Stale, (await unit.Labels.UpdateAsync(original.Id, original.Version, patch)).Status);
            Assert.Equal(LabelUpdateStatus.NotFound, (await unit.Labels.UpdateAsync("missing", original.Version, patch)).Status);
            var current = Assert.IsType<Label>(await unit.Labels.GetByIdAsync(original.Id));
            Assert.Equal("New Label!", current.Name);
            Assert.Equal(original.SortName, current.SortName);
            Assert.Equal(LabelUpdateStatus.Updated, (await unit.Labels.UpdateAsync(current.Id, current.Version, patch)).Status);
            var cleared = Assert.IsType<Label>(await unit.Labels.GetByIdAsync(current.Id));
            Assert.Null(cleared.SortName);
            Assert.Equal(current.Name, cleared.Name);
            await unit.CommitAsync();
        }
    }

    [Fact]
    public async Task Update_RollbackRestoresFieldsAndVersion()
    {
        var original = await SeedAsync();
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.Labels.UpdateAsync(original.Id, original.Version, new LabelUpdate { SetName = true, Name = "Rolled back" });
            await unit.RollbackAsync();
        }
        await using var read = await Provider.BeginUnitOfWorkAsync();
        var stored = Assert.IsType<Label>(await read.Labels.GetByIdAsync(original.Id));
        Assert.Equal(original.Name, stored.Name);
        Assert.Equal(original.Version, stored.Version);
    }

    [Theory]
    [InlineData("UPDATE label SET name = 'CLI edit' WHERE label_id = 'legacy-label'")]
    [InlineData("INSERT INTO label_alias (label_id, value) VALUES ('legacy-label', 'New alias')")]
    [InlineData("UPDATE label_alias SET value = 'Edited alias' WHERE label_id = 'legacy-label'")]
    [InlineData("DELETE FROM label_alias WHERE label_id = 'legacy-label'")]
    [InlineData("INSERT INTO label_external_ref (label_id, source, external_id) VALUES ('legacy-label', 'other', '99')")]
    [InlineData("UPDATE label_external_ref SET external_id = '99' WHERE label_id = 'legacy-label'")]
    [InlineData("DELETE FROM label_external_ref WHERE label_id = 'legacy-label'")]
    public async Task AggregateVersion_ChangesForAllOrdinarySqlWriters(string sql)
    {
        var original = await SeedAsync();
        await using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        Assert.Equal(LabelUpdateStatus.Stale, (await unit.Labels.UpdateAsync(original.Id, original.Version,
            new LabelUpdate { SetName = true, Name = "Must not win" })).Status);
        Assert.NotEqual(original.Version, (await unit.Labels.GetByIdAsync(original.Id))!.Version);
    }

    [Theory]
    [InlineData("label_alias")]
    [InlineData("label_external_ref")]
    public async Task AggregateVersion_MovingChildInvalidatesBothLabels(string table)
    {
        var original = await SeedAsync();
        await SeedAsync("destination");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.EntityDelete!.DeleteOwnedRowsAsync("label", "destination");
            await unit.CommitAsync();
        }
        string destinationVersion;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
            destinationVersion = (await unit.Labels.GetByIdAsync("destination"))!.Version;
        await using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"UPDATE {table} SET label_id = 'destination' WHERE label_id = 'legacy-label'";
            await cmd.ExecuteNonQueryAsync();
        }
        await using var read = await Provider.BeginUnitOfWorkAsync();
        Assert.NotEqual(original.Version, (await read.Labels.GetByIdAsync(original.Id))!.Version);
        Assert.NotEqual(destinationVersion, (await read.Labels.GetByIdAsync("destination"))!.Version);
    }

    [Fact]
    public async Task AggregateVersion_ChangesForLegacyUpsertAndRestore()
    {
        var original = await SeedAsync();
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.Labels.UpsertAsync(original);
            Assert.NotEqual(original.Version, (await unit.Labels.GetByIdAsync(original.Id))!.Version);
            await unit.CommitAsync();
        }
        Label beforeDelete;
        EntityTombstone tombstone;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            beforeDelete = (await unit.Labels.GetByIdAsync(original.Id))!;
            // Existing restore consumes source_payload_json, not a relational snapshot.
            var snapshot = System.Text.Json.JsonSerializer.Serialize(beforeDelete,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            await unit.Labels.UpsertAsync(beforeDelete with { SourcePayloadJson = snapshot });
            beforeDelete = (await unit.Labels.GetByIdAsync(original.Id))!;
            var deleted = await unit.EntityDelete!.DeleteEntityAsync("label", original.Id);
            Assert.True(deleted.Success, deleted.ErrorMessage);
            tombstone = Assert.IsType<EntityTombstone>(deleted.Tombstone);
            await unit.CommitAsync();
        }
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var restored = await unit.EntityDelete!.RestoreEntityAsync(tombstone, unit);
            Assert.True(restored.Success, restored.ErrorMessage);
            var label = Assert.IsType<Label>(await unit.Labels.GetByIdAsync(original.Id));
            Assert.NotEqual(beforeDelete.Version, label.Version);
            Assert.Single(label.Aliases);
            Assert.Single(label.ExternalReferences);
            Assert.Equal(LabelUpdateStatus.Stale, (await unit.Labels.UpdateAsync(original.Id, beforeDelete.Version,
                new LabelUpdate { SetName = true, Name = "Outdated" })).Status);
            await unit.CommitAsync();
        }
    }

    [Fact]
    public async Task Migration_BackfillsExistingLabelsWithoutChangingIdentityOrMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"label-v2-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE schema_migration (version INTEGER PRIMARY KEY, name TEXT, applied_utc TEXT);
                    INSERT INTO schema_migration VALUES (2, 'embedding-work-item-queue', '2025-01-01');
                    CREATE TABLE label (label_id TEXT PRIMARY KEY, name TEXT, normalized_name TEXT, sort_name TEXT,
                        source_payload_json TEXT, created_utc TEXT, updated_utc TEXT);
                    CREATE TABLE label_alias (label_id TEXT, value TEXT, normalized_value TEXT, is_primary INTEGER);
                    CREATE TABLE label_external_ref (label_id TEXT, source TEXT, external_id TEXT, is_primary INTEGER,
                        last_seen_utc TEXT, payload_json TEXT);
                    CREATE TABLE release_label_link (release_id TEXT, label_id TEXT, is_primary INTEGER, role TEXT);
                    CREATE TABLE embedding_document (entity_id TEXT, entity_type TEXT);
                    CREATE TABLE embedding_work_item (entity_id TEXT, entity_type TEXT);
                    INSERT INTO label (label_id, name) VALUES ('legacy-id', 'Keep me'), ('other-id', 'Keep me');
                    INSERT INTO label_alias VALUES ('legacy-id', 'Original alias', NULL, 0);
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            var provider = new SqliteStorageProvider(path);
            var migrated = await provider.Migrations.ApplyPendingMigrationsAsync();
            Assert.Equal(7, migrated.CurrentVersion);
            Assert.Equal(5, migrated.AppliedMigrations.Count);
            Assert.Empty((await provider.Migrations.ApplyPendingMigrationsAsync()).AppliedMigrations);
            await using var unit = await provider.BeginUnitOfWorkAsync();
            var label = Assert.IsType<Label>(await unit.Labels.GetByIdAsync("legacy-id"));
            var other = Assert.IsType<Label>(await unit.Labels.GetByIdAsync("other-id"));
            Assert.Equal("Keep me", label.Name);
            Assert.Single(label.Aliases);
            Assert.Equal(32, label.Version.Length);
            Assert.NotEqual(label.Version, other.Version);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidQueriesAndUpdatesFailExplicitly()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        foreach (var query in new[]
        {
            new LabelQuery { Page = 0 }, new LabelQuery { PageSize = 101 },
            new LabelQuery { PageSize = 0 }, new LabelQuery { SortBy = (LabelSortField)999 },
            new LabelQuery { ExternalId = "42" }, new LabelQuery { Search = " %_ " },
            new LabelQuery { Source = " " }, new LabelQuery { NormalizedName = "" },
            new LabelQuery { Source = "beatport", ExternalId = "" },
        })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => unit.Labels.QueryAsync(query).AsTask());
        foreach (var patch in new[]
        {
            new LabelUpdate(), new LabelUpdate { SetName = true },
            new LabelUpdate { SetName = true, Name = " " }, new LabelUpdate { Name = "Missing flag" },
        })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => unit.Labels.UpdateAsync("id", "version", patch).AsTask());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => unit.Labels.UpdateAsync("id", "",
            new LabelUpdate { SetName = true, Name = "Name" }).AsTask());
    }
}
