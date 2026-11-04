using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class LabelProfileTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"label-profile-{Guid.NewGuid():N}.db");
    private SqliteStorageProvider Provider => new(_path);
    private static LabelProfile Profile => new()
    {
        Description = "Independent electronic label.", CountryCode = "ES", City = "Seville", FoundedYear = 2007,
        LifecycleStatus = "active", Notes = "Personal review note.", Favourite = true, ReviewStatus = "reviewed",
        Links = [new LabelWebLink { Kind = "official", Url = "https://example.test/", Title = "Official",
            SourceUrl = "https://musicbrainz.org/label/test" }],
    };

    public async Task InitializeAsync()
    {
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await unit.Labels.UpsertAsync(new Label { Id = "keep", Name = "Kept", SourcePayloadJson = "unchanged" });
        await unit.Labels.UpsertAsync(new Label { Id = "other", Name = "Other" });
        await unit.CommitAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return Task.CompletedTask;
    }

    private async Task<Label> SaveAsync(string id, LabelProfile profile)
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var old = (await unit.Labels.GetByIdAsync(id))!;
        var result = await unit.Labels.UpdateAsync(id, old.Version, new LabelUpdate { SetProfile = true, Profile = profile });
        Assert.Equal(LabelUpdateStatus.Updated, result.Status);
        await unit.CommitAsync();
        return result.Label!;
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
        await using var connection = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    [Fact]
    public async Task Profile_RoundtripsRevisionsAndConditionalClearWithoutTouchingIdentity()
    {
        var stored = await SaveAsync("keep", Profile);
        Assert.Equal(JsonSerializer.Serialize(Profile), JsonSerializer.Serialize(stored.Profile));
        Assert.Equal("unchanged", stored.SourcePayloadJson);
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var read = (await unit.Labels.GetByIdAsync("keep"))!;
            Assert.Equal(JsonSerializer.Serialize(Profile), JsonSerializer.Serialize(read.Profile));
            await unit.Labels.UpsertAsync(new Label { Id = "keep", Name = "Legacy name edit" });
            Assert.Equal(JsonSerializer.Serialize(Profile), JsonSerializer.Serialize((await unit.Labels.GetByIdAsync("keep"))!.Profile));
            Assert.Equal(LabelUpdateStatus.Stale, (await unit.Labels.UpdateAsync("keep", stored.Version,
                new LabelUpdate { SetProfile = true, Profile = new() })).Status);
            await unit.CommitAsync();
        }
        Assert.Equal("1", await ScalarAsync("SELECT count(*) FROM label_profile_revision"));
        var empty = await SaveAsync("keep", new());
        Assert.Null(empty.Profile.Description);
        Assert.Empty(empty.Profile.Links);
        Assert.False(empty.Profile.Favourite);
        Assert.Equal("2", await ScalarAsync("SELECT count(*) FROM label_profile_revision"));
        Assert.Contains("Personal review note.", (await ScalarAsync("SELECT before_json FROM label_profile_revision ORDER BY recorded_utc DESC LIMIT 1"))!);
        Assert.Equal("manual", await ScalarAsync("SELECT actor FROM label_profile_revision LIMIT 1"));
    }

    [Fact]
    public async Task Validation_RejectsMalformedValuesAndUnsafeLinks()
    {
        foreach (var profile in new[]
        {
            Profile with { CountryCode = "Spain" }, Profile with { CountryCode = "es" },
            Profile with { FoundedYear = 0 }, Profile with { FoundedYear = 10000 },
            Profile with { ReviewStatus = "anything" }, Profile with { LifecycleStatus = "anything" },
            Profile with { Links = [new LabelWebLink { Kind = "official", Url = "javascript:alert(1)" }] },
            Profile with { Links = [new LabelWebLink { Kind = "official", Url = "https://user:password@example.test" }] },
            Profile with { Links = [new LabelWebLink { Kind = "invalid", Url = "https://example.test" }] },
            Profile with { Links = [Profile.Links[0], Profile.Links[0]] },
            Profile with { Description = new string('x', 12001) },
        })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => SaveAsync("keep", profile));
        Assert.Equal("0", await ScalarAsync("SELECT count(*) FROM label_profile_revision"));
    }

    [Fact]
    public async Task UpdateFailure_RollsBackScalarsProfileLinksAndRevisionEvenIfCallerCommits()
    {
        var original = await SaveAsync("keep", Profile);
        await ExecuteAsync("""
            CREATE TRIGGER injected_profile_failure BEFORE INSERT ON label_profile_revision
            BEGIN SELECT RAISE(ABORT,'Injected failure'); END;
            """);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Assert.ThrowsAsync<SqliteException>(() => unit.Labels.UpdateAsync("keep", original.Version,
            new LabelUpdate { SetName = true, Name = "Should roll back", SetProfile = true, Profile = new() }).AsTask());
        var unchanged = (await unit.Labels.GetByIdAsync("keep"))!;
        Assert.Equal(original.Version, unchanged.Version);
        Assert.Equal(original.Name, unchanged.Name);
        Assert.Equal(JsonSerializer.Serialize(Profile), JsonSerializer.Serialize(unchanged.Profile));
        await unit.CommitAsync();
    }

    [Fact]
    public async Task Merge_CombinesProfilesLinksAndRetainsManualAndSourceEvidence()
    {
        await SaveAsync("other", Profile);
        await ExecuteAsync("""
            INSERT INTO label_observation(observation_id,label_id,original_label_id,field_name,value_json,source,retrieved_utc)
            VALUES ('observation','other','other','countryCode','"ES"','musicbrainz','2026-11-04');
            """);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var preview = (await unit.LabelMerges.PreviewAsync("keep", "other", DateTimeOffset.UtcNow)).Preview!;
        Assert.Empty(preview.Blockers);
        Assert.Equal("ES", preview.Profile!.CountryCode);
        Assert.Equal(LabelMergeStatus.Applied, (await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version,
            "Same identity", null, "test", DateTimeOffset.UtcNow)).Status);
        var stored = (await unit.Labels.GetByIdAsync("keep"))!;
        Assert.Equal(JsonSerializer.Serialize(Profile), JsonSerializer.Serialize(stored.Profile));
        await unit.CommitAsync();
        Assert.Equal("keep", await ScalarAsync("SELECT label_id FROM label_profile_revision"));
        Assert.Equal("other", await ScalarAsync("SELECT original_label_id FROM label_profile_revision"));
        Assert.Equal("keep", await ScalarAsync("SELECT label_id FROM label_observation"));
        Assert.Contains("label_observation", (await ScalarAsync("SELECT before_json FROM label_merge_audit"))!);
        Assert.Equal("0", await ScalarAsync("SELECT count(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task Merge_ConflictingProfilesBlockAndProfileEditsInvalidateReview()
    {
        await SaveAsync("keep", Profile);
        await SaveAsync("other", Profile with { Description = "Different description" });
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var blocked = (await unit.LabelMerges.PreviewAsync("keep", "other", DateTimeOffset.UtcNow)).Preview!;
            Assert.Contains(blocked.Blockers, b => b.Contains("description"));
            Assert.Equal(LabelMergeStatus.Blocked, (await unit.LabelMerges.ApplyAsync(blocked.OperationId, blocked.Version,
                "Reason", null, "test", DateTimeOffset.UtcNow)).Status);
            await unit.CommitAsync();
        }
        await SaveAsync("other", Profile);
        LabelMergePreview preview;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            preview = (await unit.LabelMerges.PreviewAsync("keep", "other", DateTimeOffset.UtcNow)).Preview!;
            await unit.CommitAsync();
        }
        await SaveAsync("other", Profile with { City = "Madrid" });
        await using var apply = await Provider.BeginUnitOfWorkAsync();
        Assert.Equal(LabelMergeStatus.Stale, (await apply.LabelMerges.ApplyAsync(preview.OperationId, preview.Version,
            "Reason", null, "test", DateTimeOffset.UtcNow)).Status);
    }

    [Theory]
    [InlineData("INSERT INTO label_relationship VALUES ('relation','other','keep','imprint_of','manual',NULL,NULL,'2026-11-04')")]
    public async Task SchemaOnlyData_BlocksUnsafeMergeAndDeletion(string sql)
    {
        await ExecuteAsync(sql);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var preview = (await unit.LabelMerges.PreviewAsync("keep", "other", DateTimeOffset.UtcNow)).Preview!;
        Assert.NotEmpty(preview.Blockers);
        Assert.False((await unit.EntityDelete!.DeleteEntityAsync("label", "other")).Success);
    }

    [Fact]
    public async Task RetiredGuards_ProtectAllNewMetadataTables()
    {
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var preview = (await unit.LabelMerges.PreviewAsync("keep", "other", DateTimeOffset.UtcNow)).Preview!;
            await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "Same", null, "test", DateTimeOffset.UtcNow);
            await unit.CommitAsync();
        }
        foreach (var sql in new[]
        {
            "INSERT INTO label_profile(label_id) VALUES ('other')",
            "INSERT INTO label_web_link VALUES ('other','https://example.test','official',NULL,NULL)",
            "INSERT INTO label_profile_revision VALUES ('revision','other','other','now','manual','{}','{}')",
            "INSERT INTO label_observation VALUES ('o','other','other','city','\"City\"','manual',NULL,NULL,'now',NULL,NULL,NULL,'proposed',NULL,NULL)",
            "INSERT INTO label_artwork_link VALUES ('a','other','0123456789abcdef0123456789abcdef','logo',NULL,0,0)",
            "INSERT INTO label_relationship VALUES ('r','other','keep','imprint_of','manual',NULL,NULL,'now')",
            "INSERT INTO label_relationship VALUES ('r','keep','other','imprint_of','manual',NULL,NULL,'now')",
        })
            Assert.Contains("retired", (await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(sql))).Message);
    }

    [Fact]
    public async Task Merge_TooManyCombinedLinksIsAnExplicitBlocker()
    {
        await SaveAsync("keep", new() { Links = Enumerable.Range(0, 100)
            .Select(i => new LabelWebLink { Kind = "other", Url = $"https://example.test/{i}" }).ToArray() });
        await SaveAsync("other", Profile);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var preview = (await unit.LabelMerges.PreviewAsync("keep", "other", DateTimeOffset.UtcNow)).Preview!;
        Assert.Contains(preview.Blockers, b => b.Contains("100-link limit"));
        Assert.Equal(LabelMergeStatus.Blocked, (await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version,
            "Same", null, "test", DateTimeOffset.UtcNow)).Status);
    }

    [Fact]
    public async Task ObservationChange_InvalidatesReviewAndRevisionContentIsPermanent()
    {
        await SaveAsync("other", Profile);
        LabelMergePreview preview;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            preview = (await unit.LabelMerges.PreviewAsync("keep", "other", DateTimeOffset.UtcNow)).Preview!;
            await unit.CommitAsync();
        }
        await ExecuteAsync("""
            INSERT INTO label_observation(observation_id,label_id,original_label_id,field_name,value_json,source,retrieved_utc)
            VALUES ('observation','other','other','countryCode','"ES"','musicbrainz','2026-11-04');
            """);
        await using (var apply = await Provider.BeginUnitOfWorkAsync())
            Assert.Equal(LabelMergeStatus.Stale, (await apply.LabelMerges.ApplyAsync(preview.OperationId, preview.Version,
                "Reason", null, "test", DateTimeOffset.UtcNow)).Status);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("UPDATE label_profile_revision SET after_json='{}'"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM label_profile_revision"));
        Assert.Equal("1", await ScalarAsync("SELECT count(*) FROM label_profile_revision"));
    }
}
