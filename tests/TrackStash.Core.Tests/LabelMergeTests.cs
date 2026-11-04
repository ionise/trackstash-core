using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class LabelMergeTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"label-merge-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-11-04T00:00:00Z");
    private SqliteStorageProvider Provider => new(_path);

    public async Task InitializeAsync()
    {
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await unit.Labels.UpsertAsync(new Label
        {
            Id = "survivor", Name = "Univack", SortName = "Kept sort", SourcePayloadJson = """{"survivor":true}""",
            CreatedUtc = _now.AddYears(-1),
            Aliases = [new EntityAlias { Value = "Univack Records", IsPrimary = true }],
            ExternalReferences = [new EntityReference { Source = "beatport", ExternalId = "10955", IsPrimary = true, PayloadJson = """{"first":true}""" }],
        });
        await unit.Labels.UpsertAsync(new Label
        {
            Id = "retired", Name = "Univack Alternate", SortName = "Retired sort", SourcePayloadJson = """{"retired":true}""",
            ExternalReferences = [
                new EntityReference { Source = "beatport", ExternalId = "254639", IsPrimary = true },
                new EntityReference { Source = "beatport", ExternalId = "verified-third-test-id" }],
            Aliases = [new EntityAlias { Value = "UNIVACK", IsPrimary = true }],
        });
        await unit.Releases.UpsertAsync(new Release
        {
            Id = "release-both", LabelLinks = [new ReleaseLabelLink { LabelId = "survivor", Role = "label" },
                new ReleaseLabelLink { LabelId = "retired", Role = "label", IsPrimary = true }],
        });
        await unit.Releases.UpsertAsync(new Release
        {
            Id = "release-retired", LabelLinks = [new ReleaseLabelLink { LabelId = "retired" }],
        });
        await unit.CommitAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Foreign Keys=True");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Foreign Keys=True");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private async Task<LabelMergePreview> PreviewAsync(string survivor = "survivor", string retired = "retired")
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var result = await unit.LabelMerges.PreviewAsync(survivor, retired, _now);
        await unit.CommitAsync();
        return Assert.IsType<LabelMergePreview>(result.Preview);
    }

    private async Task<LabelMergeResult> ApplyAsync(LabelMergePreview preview, string reason = "Confirmed same identity", DateTimeOffset? now = null)
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var result = await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, reason, null, "test-principal", now ?? _now);
        await unit.CommitAsync();
        return result;
    }

    [Fact]
    public async Task Merge_PreservesThreeProviderIdsAliasesLinksAndRelationalAudit()
    {
        var preview = await PreviewAsync();
        Assert.Empty(preview.Blockers);
        Assert.Equal(3, preview.ExternalReferences.Count);
        Assert.Equal(2, preview.ReleaseLinks.Count);
        Assert.True(preview.ReleaseLinks.Single(l => l.ReleaseId == "release-both").IsPrimary);
        Assert.Equal("2", await ScalarAsync("SELECT count(*) FROM label"));
        var result = await ApplyAsync(preview);
        Assert.Equal(LabelMergeStatus.Applied, result.Status);
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            Assert.Null(await unit.Labels.GetByIdAsync("retired"));
            var label = Assert.IsType<Label>(await unit.Labels.GetByIdAsync("survivor"));
            Assert.Equal("Univack", label.Name);
            Assert.Equal("Kept sort", label.SortName);
            Assert.Equal(_now.AddYears(-1), label.CreatedUtc);
            Assert.Equal("""{"survivor":true}""", label.SourcePayloadJson);
            Assert.Equal(3, label.ExternalReferences.Count);
            Assert.Single(label.ExternalReferences, r => r.IsPrimary);
            Assert.Contains(label.Aliases, a => a.Value == "Univack Alternate" && !a.IsPrimary);
            Assert.Single(label.Aliases, a => a.IsPrimary);
            Assert.NotEqual(preview.Survivor.Version, label.Version);
            Assert.Equal(new LabelIdentity("retired", "survivor"), await unit.Labels.ResolveIdentityAsync("retired"));
            foreach (var reference in label.ExternalReferences)
                Assert.Equal("survivor", (await unit.Labels.GetByExternalRefAsync(reference.Source, reference.ExternalId))!.Id);
            Assert.Equal("survivor", Assert.Single((await unit.Releases.GetByIdAsync("release-retired"))!.LabelLinks).LabelId);
        }
        var audit = (await ScalarAsync("SELECT before_json FROM label_merge_audit"))!;
        using var json = JsonDocument.Parse(audit);
        Assert.Contains("retired", audit);
        Assert.Equal("""{"retired":true}""", json.RootElement.GetProperty("Retired").GetProperty("SourcePayloadJson").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("Rows").GetProperty("release_label_link").GetArrayLength());
        Assert.Contains("retired_label", (await ScalarAsync("SELECT sql FROM sqlite_master WHERE name='retired_label'"))!);
        Assert.Equal("0", await ScalarAsync("SELECT count(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task Retry_ReturnsOneSavedOutcomeEvenAfterSurvivorChanges()
    {
        var preview = await PreviewAsync();
        var applied = await ApplyAsync(preview);
        await ExecuteAsync("UPDATE label SET name='Later edit' WHERE label_id='survivor'");
        var replay = await ApplyAsync(preview, now: _now.AddDays(1));
        Assert.Equal(applied.Outcome, replay.Outcome);
        Assert.Equal("1", await ScalarAsync("SELECT count(*) FROM label_merge_audit"));
        Assert.Equal(LabelMergeStatus.ReplayConflict, (await ApplyAsync(preview, "Different reason")).Status);
    }

    [Fact]
    public async Task ConcurrentDuplicateApply_CommitsOneOutcome()
    {
        var preview = await PreviewAsync();
        var results = await Task.WhenAll(Task.Run(() => ApplyAsync(preview)), Task.Run(() => ApplyAsync(preview)));
        Assert.All(results, result => Assert.Equal(LabelMergeStatus.Applied, result.Status));
        Assert.Equal(results[0].Outcome, results[1].Outcome);
        Assert.Equal("1", await ScalarAsync("SELECT count(*) FROM label_merge_audit"));
    }

    [Theory]
    [InlineData("UPDATE label SET name='Changed' WHERE label_id='survivor'")]
    [InlineData("UPDATE label SET sort_name='Changed' WHERE label_id='retired'")]
    [InlineData("UPDATE release_label_link SET role='other' WHERE label_id='retired'")]
    [InlineData("INSERT INTO label VALUES ('third','Third',NULL,NULL,NULL,NULL,NULL,'v'); INSERT INTO label_external_ref VALUES ('third','beatport','10955',0,NULL,NULL)")]
    [InlineData("INSERT INTO embedding_work_item VALUES ('work','survivor','Label','test','v1','Updated',0,'Pending',0,'2026-11-04',NULL,NULL,NULL,NULL)")]
    public async Task DependencyChanges_RejectStalePreview(string sql)
    {
        var preview = await PreviewAsync();
        await ExecuteAsync(sql);
        Assert.Equal(LabelMergeStatus.Stale, (await ApplyAsync(preview)).Status);
        Assert.Equal("0", await ScalarAsync("SELECT count(*) FROM label_merge_audit"));
        Assert.Equal("1", await ScalarAsync("SELECT count(*) FROM label WHERE label_id='retired'"));
    }

    [Theory]
    [InlineData("UPDATE release_label_link SET role='distributor' WHERE label_id='retired' AND release_id='release-both'", "incompatible")]
    [InlineData("INSERT INTO label_external_ref VALUES ('retired','beatport','10955',0,NULL,'different')", "payload")]
    [InlineData("UPDATE label_external_ref SET is_primary=1 WHERE label_id='retired'", "primary")]
    [InlineData("INSERT INTO label_alias VALUES ('retired','Another',NULL,1)", "primary")]
    [InlineData("CREATE TABLE new_relation(label_id TEXT REFERENCES label(label_id)); INSERT INTO new_relation VALUES ('retired')", "Unsupported")]
    [InlineData("INSERT INTO embedding_work_item VALUES ('work','survivor','Label','test','v1','Updated',0,'Processing',0,'2026-11-04',NULL,NULL,NULL,NULL)", "processing")]
    public async Task Conflicts_BlockWithoutMutation(string sql, string expected)
    {
        await ExecuteAsync(sql);
        var preview = await PreviewAsync();
        Assert.Contains(preview.Blockers, b => b.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LabelMergeStatus.Blocked, (await ApplyAsync(preview)).Status);
        Assert.Equal("2", await ScalarAsync("SELECT count(*) FROM label"));
    }

    [Fact]
    public async Task ProviderAmbiguity_IsExplicitAndBlocksMerge()
    {
        await ExecuteAsync("""
            INSERT INTO label(label_id,name) VALUES ('third','Third');
            INSERT INTO label_external_ref VALUES ('third','beatport','10955',0,NULL,NULL);
            """);
        var preview = await PreviewAsync();
        Assert.Contains(preview.Blockers, b => b.Contains("third"));
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Labels.GetByExternalRefAsync("beatport", "10955").AsTask());
    }

    [Fact]
    public async Task DuplicateReference_CoalescesPayloadAndLatestObservation()
    {
        await ExecuteAsync("""
            UPDATE label_external_ref SET last_seen_utc='2025-01-01T00:00:00Z' WHERE label_id='survivor';
            INSERT INTO label_external_ref VALUES ('retired','beatport','10955',0,'2026-01-01T00:00:00Z',NULL);
            INSERT INTO label_alias VALUES ('retired','Univack Records',NULL,0);
            """);
        var preview = await PreviewAsync();
        Assert.Empty(preview.Blockers);
        Assert.Equal(3, preview.ExternalReferences.Count);
        await ApplyAsync(preview);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var reference = (await unit.Labels.GetByIdAsync("survivor"))!.ExternalReferences.Single(r => r.ExternalId == "10955");
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), reference.LastSeenUtc);
        Assert.Equal("""{"first":true}""", reference.PayloadJson);
    }

    [Fact]
    public async Task Failure_RollsBackAllChangesEvenIfCallerCommits()
    {
        await ExecuteAsync("""
            INSERT INTO embedding_document(entity_id,entity_type,model_name,model_version,dimensions,document_hash,vector_data)
            VALUES ('retired','Label','test','v1',1,'old',X'0102');
            """);
        var preview = await PreviewAsync();
        await ExecuteAsync("""
            CREATE TRIGGER injected_failure BEFORE INSERT ON label_merge_audit
            BEGIN SELECT RAISE(ABORT, 'injected merge failure'); END;
            """);
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await Assert.ThrowsAsync<SqliteException>(() => unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version,
                "Reason", null, "test", _now).AsTask());
            Assert.NotNull(await unit.Labels.GetByIdAsync("retired"));
            Assert.Single((await unit.Labels.GetByIdAsync("survivor"))!.ExternalReferences);
            await unit.CommitAsync();
        }
        Assert.Equal("0", await ScalarAsync("SELECT count(*) FROM retired_label"));
        Assert.Equal("0", await ScalarAsync("SELECT count(*) FROM label_merge_audit"));
        Assert.Equal("3", await ScalarAsync("SELECT count(*) FROM release_label_link"));
        Assert.Equal("1", await ScalarAsync("SELECT count(*) FROM embedding_document"));
        Assert.Equal("0", await ScalarAsync("SELECT count(*) FROM embedding_work_item"));
    }

    [Fact]
    public async Task Retirement_PreventsUpsertCreateUpdateReleaseRestoreAndSurvivorDeletion()
    {
        await ApplyAsync(await PreviewAsync());
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Assert.ThrowsAsync<RetiredLabelException>(() => unit.Labels.UpsertAsync(new Label { Id = "retired", Name = "No resurrection" }).AsTask());
        await Assert.ThrowsAsync<RetiredLabelException>(() => unit.Labels.TryCreateAsync(new Label { Id = "retired" }).AsTask());
        await Assert.ThrowsAsync<RetiredLabelException>(() => unit.Labels.UpdateAsync("retired", "old", new LabelUpdate { SetName = true, Name = "No" }).AsTask());
        await Assert.ThrowsAsync<RetiredLabelException>(() => unit.Releases.UpsertAsync(new Release
        {
            Id = "new-release", LabelLinks = [new ReleaseLabelLink { LabelId = "retired" }],
        }).AsTask());
        var restore = await unit.EntityDelete!.AnalyzeRestoreAsync(new EntityTombstone
        {
            EntityType = "label", EntityId = "retired", EntityDataJson = "{}", DeletedUtc = _now,
        }, unit);
        Assert.False(restore.IsSafeToRestore);
        Assert.Contains(restore.Conflicts, c => c.Contains("survivor"));
        Assert.False((await unit.EntityDelete.DeleteEntityAsync("label", "survivor")).Success);
    }

    [Fact]
    public async Task ChainedMerges_RetargetAllHistoricalIdsAndKeepAudits()
    {
        var first = await PreviewAsync();
        await ApplyAsync(first);
        await ExecuteAsync("INSERT INTO label(label_id,name) VALUES ('final','Final survivor')");
        await ApplyAsync(await PreviewAsync("final", "survivor"));
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        Assert.Equal("final", (await unit.Labels.ResolveIdentityAsync("retired")).CanonicalId);
        Assert.Equal("final", (await unit.Labels.ResolveIdentityAsync("survivor")).CanonicalId);
        await Assert.ThrowsAsync<RetiredLabelException>(() => unit.LabelMerges.PreviewAsync("retired", "final", _now).AsTask());
    }

    [Fact]
    public async Task Embeddings_AreInvalidatedAndKnownModelsRegenerated()
    {
        await ExecuteAsync("""
            INSERT INTO embedding_document(entity_id,entity_type,model_name,model_version,dimensions,document_hash,vector_data)
            VALUES ('retired','Label','test','v1',1,'old',X'0102');
            """);
        var preview = await PreviewAsync();
        await ApplyAsync(preview);
        Assert.Equal("0", await ScalarAsync("SELECT count(*) FROM embedding_document"));
        Assert.Equal("survivor", await ScalarAsync("SELECT entity_id FROM embedding_work_item"));
        Assert.Equal("1", await ScalarAsync("SELECT force_regenerate FROM embedding_work_item"));
        Assert.Contains("AQI=", (await ScalarAsync("SELECT before_json FROM label_merge_audit"))!);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("""
            INSERT INTO embedding_document(entity_id,entity_type,model_name,model_version,dimensions,document_hash)
            VALUES ('retired','Label','test','v1',1,'invalid');
            """));
    }

    [Fact]
    public async Task InvalidExpiredMissingAndReversedOperations_CannotApply()
    {
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await Assert.ThrowsAsync<ArgumentException>(() => unit.LabelMerges.PreviewAsync("survivor", "survivor", _now).AsTask());
            Assert.Equal(LabelMergeStatus.NotFound, (await unit.LabelMerges.PreviewAsync("survivor", "missing", _now)).Status);
        }
        var preview = await PreviewAsync();
        Assert.Equal(LabelMergeStatus.Expired, (await ApplyAsync(preview, now: preview.ExpiresUtc)).Status);
        var reverse = await PreviewAsync("retired", "survivor");
        await ApplyAsync(preview);
        await Assert.ThrowsAsync<RetiredLabelException>(() => ApplyAsync(reverse));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("INSERT INTO label(label_id) VALUES ('retired')"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM label_merge_audit"));
    }
}
