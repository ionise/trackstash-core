using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class LabelRelationshipTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"label-relationships-{Guid.NewGuid():N}.db");
    private SqliteStorageProvider Provider => new(_path);
    public async Task InitializeAsync()
    {
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        foreach (var id in new[] { "a", "b", "c", "d" }) await unit.Labels.UpsertAsync(new Label { Id = id, Name = $"Label {id}" });
        await unit.CommitAsync();
    }
    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return Task.CompletedTask;
    }
    private async Task<LabelRelationships> GetAsync(string id)
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        return (await unit.LabelRelationships.GetAsync(id))!;
    }
    private async Task<LabelRelationships> SetAsync(string from, params (string Target, string Type, string Source)[] edges)
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var current = (await unit.LabelRelationships.GetAsync(from))!;
        var items = new List<LabelRelationshipInput>();
        foreach (var (target, type, source) in edges)
            items.Add(new(null, target, type, source, null, null, (await unit.Labels.GetByIdAsync(target))!.Version));
        var result = await unit.LabelRelationships.ReplaceOutgoingAsync(from, current.Version, items);
        Assert.Equal(LabelUpdateStatus.Updated, result.Status);
        await unit.CommitAsync();
        return result.Relationships!;
    }
    private async Task<object?> SqlAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Foreign Keys=True");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    [Fact]
    public async Task DirectionMultipleParentsAndDistributionPreserveIncomingAndInvalidateBothEndpoints()
    {
        var beforeA = await GetAsync("a"); var beforeB = await GetAsync("b");
        await SetAsync("a", ("b", "imprint_of", "manual"), ("c", "imprint_of", "manual"), ("d", "distributed_by", "manual"));
        var a = await GetAsync("a"); var b = await GetAsync("b");
        Assert.NotEqual(beforeA.Version, a.Version);
        Assert.NotEqual(beforeB.Version, b.Version);
        Assert.Equal(3, a.Items.Count);
        Assert.Equal("a", Assert.Single(b.Items).From.Id);
        await SetAsync("b", ("d", "distributed_by", "manual"));
        Assert.Equal(2, (await GetAsync("b")).Items.Count);
        await SetAsync("a");
        Assert.Single((await GetAsync("b")).Items);
        Assert.Equal(7L, await SqlAsync("SELECT count(*) FROM label_relationship_revision"));
        await Assert.ThrowsAsync<SqliteException>(() => SqlAsync("DELETE FROM label_relationship_revision"));
        await Assert.ThrowsAsync<SqliteException>(() => SqlAsync("UPDATE label_relationship_revision SET action='changed'"));
    }
    [Fact]
    public async Task RejectsCyclesAndSelfLinksButAllowsDistributionLoops()
    {
        await SetAsync("a", ("b", "imprint_of", "manual"));
        await SetAsync("b", ("c", "imprint_of", "manual"));
        await Assert.ThrowsAsync<LabelRelationshipValidationException>(() => SetAsync("c", ("a", "imprint_of", "manual")));
        await Assert.ThrowsAsync<LabelRelationshipValidationException>(() => SetAsync("a", ("a", "distributed_by", "manual")));
        await SetAsync("c", ("a", "distributed_by", "manual"));
        await SetAsync("d", ("c", "distributed_by", "manual"));
        await Assert.ThrowsAsync<SqliteException>(() => SqlAsync("""
            INSERT INTO label_relationship VALUES('unsafe','c','a','imprint_of','sql',NULL,NULL,'2026-11-05');
            """));
        Assert.Equal(0L, await SqlAsync("SELECT count(*) FROM label_relationship WHERE relationship_id='unsafe'"));
        await Assert.ThrowsAsync<SqliteException>(() => SqlAsync("UPDATE label_relationship SET relationship_type='imprint_of' WHERE from_label_id='c'"));
        await SqlAsync("UPDATE label_relationship SET notes='Updated directly' WHERE from_label_id='c'");
        Assert.Contains("Updated directly", (string)(await SqlAsync("SELECT after_json FROM label_relationship_revision WHERE action='update'"))!);
    }
    [Fact]
    public async Task MaximumOutgoingIsOneHundredAndFieldBoundariesAreEnforced()
    {
        var valid = new LabelRelationship("edge", "a", "b", "imprint_of", new string('s', 200), "https://example.test", new string('n', 2000), DateTimeOffset.UtcNow);
        LabelRelationshipRules.Validate(valid);
        foreach (var invalid in new[] { valid with { Source = new string('s', 201) }, valid with { Notes = new string('n', 2001) },
            valid with { Type = "parent_of" }, valid with { SourceUrl = "https://user:password@example.test" },
            valid with { SourceUrl = "https://example.test/" + new string('u', 2049) } })
            Assert.Throws<LabelRelationshipValidationException>(() => LabelRelationshipRules.Validate(invalid));
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var items = new List<LabelRelationshipInput>();
        for (var index = 0; index < 101; index++)
        {
            var id = $"target-{index}";
            await unit.Labels.UpsertAsync(new Label { Id = id, Name = id });
            items.Add(new(null, id, "imprint_of", "manual", null, null, (await unit.Labels.GetByIdAsync(id))!.Version));
        }
        var current = (await unit.LabelRelationships.GetAsync("a"))!;
        var result = await unit.LabelRelationships.ReplaceOutgoingAsync("a", current.Version, items.Take(100).ToArray());
        Assert.Equal(100, result.Relationships!.Items.Count);
        await Assert.ThrowsAsync<LabelRelationshipValidationException>(() => unit.LabelRelationships.ReplaceOutgoingAsync("a", result.Relationships.Version, items).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => unit.LabelRelationships.ReplaceOutgoingAsync("a", result.Relationships.Version, null!).AsTask());
        await unit.CommitAsync();
    }
    [Fact]
    public async Task RejectsDuplicateUnknownIdsMissingRetiredAndStaleTargetsWithoutPartialWrites()
    {
        await Assert.ThrowsAsync<LabelRelationshipValidationException>(() => SetAsync("a", ("b", "imprint_of", "manual"), ("b", "imprint_of", "manual")));
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var a = (await unit.LabelRelationships.GetAsync("a"))!;
        var b = (await unit.Labels.GetByIdAsync("b"))!;
        var stale = await unit.LabelRelationships.ReplaceOutgoingAsync("a", a.Version, [new(null, "b", "imprint_of", "manual", null, null, "stale")]);
        Assert.Equal(LabelUpdateStatus.Stale, stale.Status);
        await Assert.ThrowsAsync<LabelRelationshipValidationException>(() => unit.LabelRelationships.ReplaceOutgoingAsync("a", a.Version,
            [new("not-owned", "b", "imprint_of", "manual", null, null, b.Version)]).AsTask());
        await Assert.ThrowsAsync<LabelRelationshipValidationException>(() => unit.LabelRelationships.ReplaceOutgoingAsync("a", a.Version,
            [new(null, "missing", "imprint_of", "manual", null, null, "v")]).AsTask());
        await Assert.ThrowsAsync<LabelRelationshipValidationException>(() => unit.LabelRelationships.ReplaceOutgoingAsync("a", a.Version,
            [new(null, "b", "imprint_of", "", null, null, b.Version)]).AsTask());
        await Assert.ThrowsAsync<LabelRelationshipValidationException>(() => unit.LabelRelationships.ReplaceOutgoingAsync("a", a.Version,
            [new(null, "b", "imprint_of", "manual", "javascript:alert(1)", null, b.Version)]).AsTask());
        Assert.Empty((await unit.LabelRelationships.GetAsync("a"))!.Items);
        await unit.CommitAsync();
    }
    [Fact]
    public async Task MetadataEditsPreserveIdAndDateAndNoOpDoesNotInvalidateVersion()
    {
        var current = await SetAsync("a", ("b", "imprint_of", "manual"));
        var edge = Assert.Single(current.Items);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var item = new LabelRelationshipInput(edge.Relationship.Id, "b", "imprint_of", "manual", null, null, edge.To.Version);
        var noop = await unit.LabelRelationships.ReplaceOutgoingAsync("a", current.Version, [item]);
        Assert.Equal(current.Version, noop.Relationships!.Version);
        var changed = await unit.LabelRelationships.ReplaceOutgoingAsync("a", current.Version, [item with { SourceUrl = "https://example.test/evidence", Notes = "Reviewed source" }]);
        Assert.Equal(edge.Relationship.Id, Assert.Single(changed.Relationships!.Items).Relationship.Id);
        Assert.Equal(edge.Relationship.CreatedUtc, Assert.Single(changed.Relationships.Items).Relationship.CreatedUtc);
        Assert.Equal(LabelUpdateStatus.Stale, (await unit.LabelRelationships.ReplaceOutgoingAsync("a", current.Version, [])).Status);
        await unit.CommitAsync();
    }
    [Fact]
    public async Task StorageFailureRollsBackEdgesHistoryAndVersionsEvenWhenCallerCommits()
    {
        var before = await SetAsync("a", ("b", "imprint_of", "manual"));
        var revisionCount = await SqlAsync("SELECT count(*) FROM label_relationship_revision");
        await SqlAsync("CREATE TRIGGER fail_relationship BEFORE INSERT ON label_relationship BEGIN SELECT RAISE(ABORT,'Injected failure'); END;");
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var c = (await unit.Labels.GetByIdAsync("c"))!;
        await Assert.ThrowsAsync<SqliteException>(() => unit.LabelRelationships.ReplaceOutgoingAsync("a", before.Version,
            [new(null, "c", "imprint_of", "manual", null, null, c.Version)]).AsTask());
        var after = (await unit.LabelRelationships.GetAsync("a"))!;
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Items.ToArray(), after.Items.ToArray());
        await unit.CommitAsync();
        Assert.Equal(revisionCount, await SqlAsync("SELECT count(*) FROM label_relationship_revision"));
    }
    [Fact]
    public async Task MergeTransfersIncomingOutgoingAndCoalescesEqualEvidenceThenRetiresSafely()
    {
        await SetAsync("a", ("c", "imprint_of", "manual"));
        await SetAsync("b", ("c", "imprint_of", "manual"));
        await SetAsync("d", ("b", "distributed_by", "manual"));
        var beforeC = await GetAsync("c");
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var preview = (await unit.LabelMerges.PreviewAsync("a", "b", DateTimeOffset.UtcNow)).Preview!;
        Assert.Empty(preview.Blockers);
        Assert.Equal(2, preview.Relationships!.Count);
        var outcome = await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "Duplicate", null, "test", DateTimeOffset.UtcNow);
        Assert.Equal(LabelMergeStatus.Applied, outcome.Status);
        Assert.Equal(2, (await unit.LabelRelationships.GetAsync("a"))!.Items.Count);
        Assert.NotEqual(beforeC.Version, (await unit.Labels.GetByIdAsync("c"))!.Version);
        await Assert.ThrowsAsync<RetiredLabelException>(() => unit.LabelRelationships.GetAsync("b").AsTask());
        var a = (await unit.LabelRelationships.GetAsync("a"))!;
        await Assert.ThrowsAsync<RetiredLabelException>(() => unit.LabelRelationships.ReplaceOutgoingAsync("a", a.Version,
            [new(null, "b", "distributed_by", "manual", null, null, "v")]).AsTask());
        await unit.CommitAsync();
        Assert.Equal(0L, await SqlAsync("SELECT count(*) FROM pragma_foreign_key_check"));
        Assert.Contains("Relationships", (string)(await SqlAsync("SELECT after_json FROM label_merge_audit"))!);
    }
    [Theory]
    [InlineData("self")]
    [InlineData("cycle")]
    [InlineData("conflict")]
    public async Task MergeBlocksUnsafeGraphChanges(string scenario)
    {
        if (scenario == "self") await SetAsync("b", ("a", "distributed_by", "manual"));
        if (scenario == "cycle") { await SetAsync("a", ("c", "imprint_of", "manual")); await SetAsync("c", ("b", "imprint_of", "manual")); }
        if (scenario == "conflict") { await SetAsync("a", ("c", "imprint_of", "first")); await SetAsync("b", ("c", "imprint_of", "other")); }
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var preview = (await unit.LabelMerges.PreviewAsync("a", "b", DateTimeOffset.UtcNow)).Preview!;
        Assert.NotEmpty(preview.Blockers);
        Assert.Equal(LabelMergeStatus.Blocked, (await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "Same", null, "test", DateTimeOffset.UtcNow)).Status);
    }
    [Fact]
    public async Task GraphEditsInvalidateSavedMergeReview()
    {
        LabelMergePreview preview;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            preview = (await unit.LabelMerges.PreviewAsync("a", "b", DateTimeOffset.UtcNow)).Preview!;
            await unit.CommitAsync();
        }
        await SetAsync("c", ("d", "imprint_of", "manual"));
        await using var apply = await Provider.BeginUnitOfWorkAsync();
        Assert.Equal(LabelMergeStatus.Stale, (await apply.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "Same", null, "test", DateTimeOffset.UtcNow)).Status);
    }
    [Fact]
    public async Task FailedMergeTransferRollsBackRelationshipsAndHistoryEvenWhenCallerCommits()
    {
        await SetAsync("b", ("c", "imprint_of", "manual"));
        var before = await GetAsync("b");
        var history = await SqlAsync("SELECT count(*) FROM label_relationship_revision");
        await SqlAsync("CREATE TRIGGER fail_transfer BEFORE INSERT ON label_relationship BEGIN SELECT RAISE(ABORT,'Injected transfer failure'); END");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var preview = (await unit.LabelMerges.PreviewAsync("a", "b", DateTimeOffset.UtcNow)).Preview!;
            Assert.Empty(preview.Blockers);
            await Assert.ThrowsAsync<SqliteException>(() => unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "Same", null, "test", DateTimeOffset.UtcNow).AsTask());
            await unit.CommitAsync();
        }
        Assert.Equal(before.Version, (await GetAsync("b")).Version);
        Assert.Equal(before.Items.ToArray(), (await GetAsync("b")).Items.ToArray());
        Assert.Equal(history, await SqlAsync("SELECT count(*) FROM label_relationship_revision"));
        Assert.Equal(0L, await SqlAsync("SELECT count(*) FROM retired_label"));
        Assert.Equal(0L, await SqlAsync("SELECT count(*) FROM label_merge_audit"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForwardMigrationPreservesExistingEdgesAndBaselinesHistoryOrRollsBackCycles(bool cyclic)
    {
        await LegacyCatalogSchema.RevertEmptyFoundationsAsync(_path);
        var before = await SetAsync("a", ("b", "imprint_of", "original source"));
        await SqlAsync("""
            DROP TABLE label_group_membership; DROP TABLE label_group_membership_revision;
            DROP TABLE label_group; DROP TABLE label_group_revision;
            DROP TRIGGER relationship_history_insert; DROP TRIGGER relationship_history_update; DROP TRIGGER relationship_history_delete;
            DROP TRIGGER relationship_cycle_insert; DROP TRIGGER relationship_cycle_update;
            DROP TABLE label_relationship_revision;
            DELETE FROM schema_migration WHERE version>=7;
            """);
        if (cyclic)
        {
            await SqlAsync("INSERT INTO label_relationship VALUES('legacy-loop','b','a','imprint_of','legacy',NULL,NULL,'2026-11-05')");
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => Provider.Migrations.ApplyPendingMigrationsAsync().AsTask());
            Assert.Contains("cycle", failure.Message);
            Assert.Equal(6, await Provider.Migrations.GetCurrentVersionAsync());
            Assert.Equal(0L, await SqlAsync("SELECT count(*) FROM sqlite_master WHERE name='label_relationship_revision'"));
            Assert.Equal(2L, await SqlAsync("SELECT count(*) FROM label_relationship"));
        }
        else
        {
            Assert.Equal(11, (await Provider.Migrations.ApplyPendingMigrationsAsync()).CurrentVersion);
            var after = await GetAsync("a");
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.Items.ToArray(), after.Items.ToArray());
            Assert.Equal("baseline", await SqlAsync("SELECT action FROM label_relationship_revision"));
            Assert.Contains("original source", (string)(await SqlAsync("SELECT after_json FROM label_relationship_revision"))!);
            Assert.Empty((await Provider.Migrations.ApplyPendingMigrationsAsync()).AppliedMigrations);
            await Assert.ThrowsAsync<SqliteException>(() => SqlAsync("UPDATE label_relationship SET from_label_id='b',to_label_id='b'"));
        }
    }
}
