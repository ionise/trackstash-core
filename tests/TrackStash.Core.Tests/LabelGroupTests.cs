using Microsoft.Data.Sqlite;
using System.Text.Json;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests;

public sealed class LabelGroupTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"groups-{Guid.NewGuid():N}");
    private string Database => Path.Combine(_directory, "catalog.db");
    private SqliteStorageProvider Provider => new(Database);
    private static MembershipFields Fields(string status = "current") => new(status, "2020", null, "Official site", "https://example.test", "Reviewed affiliation");
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        foreach (var id in new[] { "a", "b", "c", "parent" }) await unit.Labels.UpsertAsync(new Label { Id = id, Name = id });
        await unit.CommitAsync();
    }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); return Task.CompletedTask; }
    private async Task<object?> Sql(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Foreign Keys=True");
        await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    private static async Task<LabelGroup> Group(IUnitOfWork unit, string kind = "family", string name = "Anjuna")
        => (await unit.LabelGroups.CreateAsync(kind, new(name, null, null, null))).Group!;
    private static async Task<MembershipView> Member(IUnitOfWork unit, string label, LabelGroup group, MembershipFields? fields = null)
    {
        var result = await unit.LabelGroups.SaveMembershipAsync(null, null, label, group.Id,
            (await unit.Labels.GetByIdAsync(label))!.Version, (await unit.LabelGroups.GetAsync(group.Id))!.Version, fields ?? Fields(), false);
        Assert.Equal(GroupWriteStatus.Updated, result.Status); return result.Membership!;
    }
    [Fact]
    public async Task GroupsRemainDistinctAndMembershipsInvalidateOnlyTheirEndpointsAndRetainHistory()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var family = await Group(unit); var organisation = await Group(unit, "organisation");
        var unrelated = (await unit.Labels.GetByIdAsync("c"))!;
        var a = (await unit.Labels.GetByIdAsync("a"))!;
        var member = await Member(unit, "a", family);
        Assert.Equal(1, member.Group.CurrentMembers);
        Assert.NotEqual(a.Version, member.Label.Version); Assert.NotEqual(family.Version, member.Group.Version);
        Assert.Equal(unrelated.Version, (await unit.Labels.GetByIdAsync("c"))!.Version);
        Assert.Equal(2, (await unit.LabelGroups.QueryAsync(new(Search: "ANJUNA"))).Total);
        Assert.NotEqual(family.Id, organisation.Id);
        var currentGroup = (await unit.LabelGroups.GetAsync(family.Id))!;
        Assert.Equal(1, currentGroup.CurrentMembers);
        var renamed = await unit.LabelGroups.UpdateAsync(family.Id, currentGroup.Version, new("Anjuna family", null, "Brand not owner", null));
        Assert.Equal(GroupWriteStatus.Updated, renamed.Status);
        Assert.Equal(member.Label.Version, (await unit.Labels.GetByIdAsync("a"))!.Version);
        Assert.Equal("Anjuna family", (await unit.LabelGroups.MembershipAsync(member.Membership.Id))!.Group.Name);
        Assert.Equal(GroupWriteStatus.Stale, (await unit.LabelGroups.SaveMembershipAsync(member.Membership.Id, member.Membership.Version,
            "a", family.Id, member.Label.Version, member.Group.Version, Fields("former"), false)).Status);
        var latest = (await unit.LabelGroups.MembershipAsync(member.Membership.Id))!;
        var edited = await unit.LabelGroups.SaveMembershipAsync(latest.Membership.Id, latest.Membership.Version, "a", family.Id,
            latest.Label.Version, latest.Group.Version, Fields("former") with { Until = "2021-02" }, false);
        Assert.Equal(member.Membership.CreatedUtc, edited.Membership!.Membership.CreatedUtc);
        Assert.Equal(GroupWriteStatus.Stale, (await unit.LabelGroups.RemoveMembershipAsync(member.Membership.Id, member.Membership.Version)).Status);
        Assert.Equal(GroupWriteStatus.Updated, (await unit.LabelGroups.RemoveMembershipAsync(member.Membership.Id, edited.Membership.Membership.Version)).Status);
        Assert.Equal(GroupWriteStatus.NotFound, (await unit.LabelGroups.RemoveMembershipAsync(member.Membership.Id, edited.Membership.Membership.Version)).Status);
        await unit.CommitAsync();
        Assert.Equal(4L, await Sql("SELECT count(*) FROM label_group_membership_revision"));
        Assert.Contains("Official site", (string)(await Sql("SELECT before_json FROM label_group_membership_revision WHERE action='delete' LIMIT 1"))!);
        await Assert.ThrowsAsync<SqliteException>(() => Sql("DELETE FROM label_group_membership_revision"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql("UPDATE label_group_revision SET actor='changed'"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"DELETE FROM label_group WHERE group_id='{family.Id}'"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE label_group SET kind='organisation' WHERE group_id='{family.Id}'"));
    }
    [Fact]
    public async Task DuplicateAndStaleWritesAndReassignmentAreRejectedAndRejoiningRequiresReview()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var group = await Group(unit);
        var member = await Member(unit, "a", group, Fields("former"));
        var duplicate = await unit.LabelGroups.SaveMembershipAsync(null, null, "a", group.Id, member.Label.Version, member.Group.Version, Fields(), false);
        Assert.Equal(GroupWriteStatus.Conflict, duplicate.Status);
        Assert.Equal(member.Membership.Id, duplicate.Membership!.Membership.Id);
        await Assert.ThrowsAsync<GroupValidationException>(() => unit.LabelGroups.SaveMembershipAsync(member.Membership.Id, member.Membership.Version,
            "b", group.Id, "x", "y", Fields(), false).AsTask());
        await Assert.ThrowsAsync<GroupValidationException>(() => unit.LabelGroups.SaveMembershipAsync(member.Membership.Id, member.Membership.Version,
            "a", group.Id, member.Label.Version, member.Group.Version, Fields(), false).AsTask());
        var rejoined = await unit.LabelGroups.SaveMembershipAsync(member.Membership.Id, member.Membership.Version, "a", group.Id,
            member.Label.Version, member.Group.Version, Fields() with { Since = "2023" }, true);
        Assert.Equal(GroupWriteStatus.Updated, rejoined.Status);
        Assert.Equal("2023", rejoined.Membership!.Membership.Fields.Since);
        Assert.Equal(GroupWriteStatus.NotFound, (await unit.LabelGroups.SaveMembershipAsync(null, null, "missing", group.Id, "v", "v", Fields(), false)).Status);
        Assert.Equal(GroupWriteStatus.Stale, (await unit.LabelGroups.SaveMembershipAsync(null, null, "b", group.Id, "stale", member.Group.Version, Fields(), false)).Status);
        var blockers = await unit.EntityDelete!.AnalyzeDependenciesAsync("label", "a", unit);
        Assert.Contains(blockers.Blockers, b => b.TableName == "label_group_membership");
        var groupBlockers = await unit.EntityDelete.AnalyzeDependenciesAsync("label_group", group.Id, unit);
        Assert.False(groupBlockers.IsSafeToDelete);
        Assert.Contains(groupBlockers.Blockers, b => b.TableName == "label_group_membership");
        Assert.Contains(groupBlockers.Blockers, b => b.TableName == "label_group");
        await unit.CommitAsync();
    }
    [Theory]
    [InlineData("current", "2020", "2021")]
    [InlineData("active", null, null)]
    [InlineData("former", "2022", "2021")]
    [InlineData("former", "2020-02-30", null)]
    [InlineData("former", "0000", null)]
    [InlineData("former", "2020-13", null)]
    [InlineData("former", "2020-1", null)]
    [InlineData("current", "9999", null)]
    [InlineData("former", "2020-01-01T00:00:00Z", null)]
    [InlineData("former", "2020\n", null)]
    public void RejectsInvalidDatesAndStatuses(string status, string? since, string? until) =>
        Assert.Throws<GroupValidationException>(() => (Fields(status) with { Since = since, Until = until }).Validate(new(2026, 11, 5)));
    [Fact]
    public void PreservesPartialDatePrecisionAndRejectsBadEvidence()
    {
        foreach (var value in new[] { "2020", "2020-02", "2020-02-29", "2026" }) (Fields() with { Since = value }).Validate(new(2026, 11, 5));
        (Fields("former") with { Since = "2020-12", Until = "2020" }).Validate(new(2026, 11, 5));
        Assert.Equal(new DateOnly(2020, 2, 29), GroupRules.DateRange("2020-02")!.Value.Last);
        foreach (var fields in new[] { Fields() with { Source = "" }, Fields() with { Notes = new string('n', 2001) },
            Fields() with { SourceUrl = "javascript:alert(1)" }, Fields() with { SourceUrl = "https://user:secret@example.test" } })
            Assert.Throws<GroupValidationException>(() => fields.Validate(new(2026, 11, 5)));
    }
    [Fact]
    public async Task RelatedLabelsDeduplicateBeforePagingAndRetainAllReasonsWithoutInferringHierarchy()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var family = await Group(unit); var org = await Group(unit, "organisation");
        await Member(unit, "a", family); await Member(unit, "b", family); await Member(unit, "c", family, Fields("former"));
        await Member(unit, "a", org); await Member(unit, "b", org);
        foreach (var id in new[] { "a", "b" })
        {
            var label = (await unit.LabelRelationships.GetAsync(id))!;
            var parent = (await unit.Labels.GetByIdAsync("parent"))!;
            await unit.LabelRelationships.ReplaceOutgoingAsync(id, label.Version, [new(null, "parent", "imprint_of", "manual", null, null, parent.Version)]);
        }
        var page = (await unit.LabelGroups.RelatedAsync("a", new(PageSize: 1)))!;
        Assert.Equal(1, page.Total); var b = Assert.Single(page.Items);
        Assert.Equal("b", b.Label.Id); Assert.Equal(3, b.Reasons.Count);
        Assert.Empty((await unit.LabelGroups.RelatedAsync("parent", new()))!.Items);
        Assert.Equal(2, (await unit.LabelGroups.MembershipsAsync(null, family.Id, new()))!.Total);
        Assert.Equal(3, (await unit.LabelGroups.MembershipsAsync(null, family.Id, new(Status: "all")))!.Total);
        Assert.Equal(1, (await unit.LabelGroups.MembershipsAsync(null, family.Id, new()))!.Counts!["former"]);
        Assert.Null(await unit.LabelGroups.RelatedAsync("missing", new()));
        await unit.CommitAsync();
    }
    [Fact]
    public async Task MoreThanOneHundredMembersArePagedWithoutAnAggregateReplacementLimit()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var group = await Group(unit);
        for (var i = 0; i < 101; i++)
        {
            var id = $"member-{i:D3}"; await unit.Labels.UpsertAsync(new Label { Id = id, Name = id }); await Member(unit, id, group);
        }
        var first = (await unit.LabelGroups.MembershipsAsync(null, group.Id, new(PageSize: 100)))!;
        var second = (await unit.LabelGroups.MembershipsAsync(null, group.Id, new(Page: 2, PageSize: 100)))!;
        Assert.Equal(101, first.Total); Assert.Equal(100, first.Items.Count); Assert.Single(second.Items);
        Assert.DoesNotContain(second.Items[0].Membership.Id, first.Items.Select(i => i.Membership.Id));
        await unit.CommitAsync();
    }
    [Fact]
    public async Task MembershipFailureRollsBackHistoryAndTokensEvenIfCallerCommits()
    {
        string id;
        await using (var unit = await Provider.BeginUnitOfWorkAsync()) { id = (await Member(unit, "a", await Group(unit))).Membership.Id; await unit.CommitAsync(); }
        await Sql("CREATE TRIGGER fail_member BEFORE INSERT ON label_group_membership BEGIN SELECT RAISE(ABORT,'Injected failure'); END");
        await using var edit = await Provider.BeginUnitOfWorkAsync();
        var before = (await edit.LabelGroups.MembershipAsync(id))!;
        await Assert.ThrowsAsync<SqliteException>(() => edit.LabelGroups.SaveMembershipAsync(id, before.Membership.Version,
            before.Label.Id, before.Group.Id, before.Label.Version, before.Group.Version, Fields("former"), false).AsTask());
        Assert.Equal(before, await edit.LabelGroups.MembershipAsync(id));
        await edit.CommitAsync();
        Assert.Equal(1L, await Sql("SELECT count(*) FROM label_group_membership_revision"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeTransfersOrBlocksConflictingMembershipsAndRejectsRetiredWrites(bool conflict)
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var group = await Group(unit);
        await Member(unit, "a", group); await Member(unit, "b", group, conflict ? Fields("former") : Fields());
        var preview = (await unit.LabelMerges.PreviewAsync("a", "b", DateTimeOffset.UtcNow)).Preview!;
        Assert.Single(preview.Memberships!);
        if (conflict) Assert.NotEmpty(preview.Blockers);
        var applied = await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "Same identity", null, "test", DateTimeOffset.UtcNow);
        Assert.Equal(conflict ? LabelMergeStatus.Blocked : LabelMergeStatus.Applied, applied.Status);
        if (!conflict)
        {
            Assert.Single((await unit.LabelGroups.MembershipsAsync("a", null, new()))!.Items);
            await Assert.ThrowsAsync<RetiredLabelException>(() => unit.LabelGroups.SaveMembershipAsync(null, null, "b", group.Id, "v", "v", Fields(), false).AsTask());
        }
        await unit.CommitAsync();
        Assert.Equal(0L, await Sql("SELECT count(*) FROM pragma_foreign_key_check"));
    }
    [Fact]
    public async Task EditingGroupInvalidatesMergeReviewAndFailedMergeRollsBackMembershipTransfers()
    {
        LabelMergePreview preview; LabelGroup group;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            group = await Group(unit); await Member(unit, "b", group);
            preview = (await unit.LabelMerges.PreviewAsync("a", "b", DateTimeOffset.UtcNow)).Preview!; await unit.CommitAsync();
        }
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var current = (await unit.LabelGroups.GetAsync(group.Id))!;
            await unit.LabelGroups.UpdateAsync(group.Id, current.Version, new("Renamed", null, null, null)); await unit.CommitAsync();
        }
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            Assert.Equal(LabelMergeStatus.Stale, (await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "same", null, "test", DateTimeOffset.UtcNow)).Status);
            preview = (await unit.LabelMerges.PreviewAsync("a", "b", DateTimeOffset.UtcNow)).Preview!; await unit.CommitAsync();
        }
        await Sql("CREATE TRIGGER fail_transfer BEFORE INSERT ON label_group_membership BEGIN SELECT RAISE(ABORT,'Injected transfer failure'); END");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await Assert.ThrowsAsync<SqliteException>(() => unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "same", null, "test", DateTimeOffset.UtcNow).AsTask());
            Assert.Single((await unit.LabelGroups.MembershipsAsync("b", null, new()))!.Items); await unit.CommitAsync();
        }
        Assert.Equal(1L, await Sql("SELECT count(*) FROM label_group_membership_revision"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM retired_label"));
    }
    [Fact]
    public async Task ParentSideEditsStoreOnlyChildToParentAndPreserveUnrelatedEdges()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var child = (await unit.LabelRelationships.GetAsync("a"))!;
        var parent = (await unit.LabelRelationships.GetAsync("parent"))!;
        var created = await unit.LabelRelationships.SaveEdgeAsync("parent", parent.Version,
            new(null, "a", "imprint_of", true, "manual", null, "From parent", child.Version));
        Assert.Equal(LabelUpdateStatus.Updated, created.Status);
        var edge = Assert.Single(created.Relationships!.Items);
        Assert.Equal("a", edge.From.Id); Assert.Equal("parent", edge.To.Id);
        Assert.Equal(LabelUpdateStatus.Stale, (await unit.LabelRelationships.SaveEdgeAsync("parent", parent.Version,
            new(null, "a", "imprint_of", true, "manual", null, null, child.Version))).Status);
        var updated = await unit.LabelRelationships.SaveEdgeAsync("parent", created.Relationships.Version,
            new(edge.Relationship.Id, "a", "imprint_of", true, "reviewed", null, null, edge.From.Version));
        Assert.Equal(edge.Relationship.Id, Assert.Single(updated.Relationships!.Items).Relationship.Id);
        var currentEdge = Assert.Single(updated.Relationships.Items);
        await unit.LabelRelationships.SaveEdgeAsync("parent", updated.Relationships.Version,
            new(currentEdge.Relationship.Id, "a", "imprint_of", true, "reviewed", null, null, currentEdge.From.Version, true));
        Assert.Empty((await unit.LabelRelationships.GetAsync("a"))!.Items);
        await unit.CommitAsync();
    }
    [Fact]
    public async Task DirectMembershipEditsInvalidateBothEndpointsAndKeepImmutableIdentityAndHistory()
    {
        MembershipView member;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            member = await Member(unit, "a", await Group(unit)); await unit.CommitAsync();
        }
        await Sql($"UPDATE label_group_membership SET notes='Changed evidence' WHERE membership_id='{member.Membership.Id}'");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var changed = (await unit.LabelGroups.MembershipAsync(member.Membership.Id))!;
            Assert.NotEqual(member.Label.Version, changed.Label.Version);
            Assert.NotEqual(member.Group.Version, changed.Group.Version);
            Assert.NotEqual(member.Membership.Version, changed.Membership.Version);
            Assert.Equal("Changed evidence", changed.Membership.Fields.Notes);
            Assert.Equal(GroupWriteStatus.Stale, (await unit.LabelGroups.RemoveMembershipAsync(member.Membership.Id, member.Membership.Version)).Status);
            await unit.CommitAsync();
        }
        Assert.Equal(2L, await Sql("SELECT count(*) FROM label_group_membership_revision"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE label_group_membership SET label_id='b' WHERE membership_id='{member.Membership.Id}'"));
        Assert.Equal(2L, await Sql("SELECT count(*) FROM label_group_membership_revision"));
    }
    [Fact]
    public async Task UnsupportedQueryFieldsFailExplicitlyInsteadOfBeingIgnored()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await Assert.ThrowsAsync<GroupValidationException>(() => unit.LabelGroups.QueryAsync(new(Status: "former")).AsTask());
        await Assert.ThrowsAsync<GroupValidationException>(() => unit.LabelGroups.MembershipsAsync("a", null, new(Search: "ignored")).AsTask());
        await Assert.ThrowsAsync<GroupValidationException>(() => unit.LabelGroups.MembershipsAsync("a", null, new(Descending: true)).AsTask());
        await Assert.ThrowsAsync<GroupValidationException>(() => unit.LabelGroups.RelatedAsync("a", new(Status: "former")).AsTask());
        await unit.CommitAsync();
    }
    [Fact]
    public async Task MigrationFromSevenPreservesExistingLabelsRelationshipsAndTokensAndIsIdempotent()
    {
        await LegacyCatalogSchema.RevertEmptyFoundationsAsync(Database);
        await Sql("""
            DROP TABLE label_group_membership; DROP TABLE label_group_membership_revision;
            DROP TABLE label_group; DROP TABLE label_group_revision;
            DELETE FROM schema_migration WHERE version=8;
            """);
        LabelRelationshipView edge; string aVersion, parentVersion;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var child = (await unit.LabelRelationships.GetAsync("a"))!;
            var parent = (await unit.Labels.GetByIdAsync("parent"))!;
            edge = Assert.Single((await unit.LabelRelationships.ReplaceOutgoingAsync("a", child.Version,
                [new(null, "parent", "imprint_of", "manual", null, "Preserve evidence", parent.Version)])).Relationships!.Items);
            aVersion = (await unit.Labels.GetByIdAsync("a"))!.Version; parentVersion = (await unit.Labels.GetByIdAsync("parent"))!.Version;
            await unit.CommitAsync();
        }
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        Assert.Equal(10, await Provider.Migrations.GetCurrentVersionAsync());
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            Assert.Equal(aVersion, (await unit.Labels.GetByIdAsync("a"))!.Version);
            Assert.Equal(parentVersion, (await unit.Labels.GetByIdAsync("parent"))!.Version);
            Assert.Equal(edge, Assert.Single((await unit.LabelRelationships.GetAsync("a"))!.Items));
            Assert.Empty((await unit.LabelGroups.QueryAsync(new())).Items); await unit.CommitAsync();
        }
        Assert.Equal(1L, await Sql("SELECT count(*) FROM label_relationship_revision"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM pragma_foreign_key_check"));
    }
    [Fact]
    public async Task FailedForwardMigrationLeavesNoPartialGroupTablesOrLedgerEntry()
    {
        await LegacyCatalogSchema.RevertEmptyFoundationsAsync(Database);
        await Sql("""
            DROP TABLE label_group_membership; DROP TABLE label_group_membership_revision;
            DROP TABLE label_group; DROP TABLE label_group_revision;
            DELETE FROM schema_migration WHERE version=8;
            CREATE INDEX idx_label_memberships ON label(name);
            """);
        await Assert.ThrowsAsync<SqliteException>(() => Provider.Migrations.ApplyPendingMigrationsAsync().AsTask());
        Assert.Equal(7, await Provider.Migrations.GetCurrentVersionAsync());
        Assert.Equal(0L, await Sql("SELECT count(*) FROM sqlite_master WHERE type='table' AND name LIKE 'label_group%'"));
        await Sql("DROP INDEX idx_label_memberships");
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        Assert.Equal(10, await Provider.Migrations.GetCurrentVersionAsync());
    }
    [Fact]
    public async Task ThirdMemberChangeStalesReviewAndFreshMergeTransfersDifferentGroupsWithAudit()
    {
        LabelMergePreview preview; LabelGroup family, organisation;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            family = await Group(unit); organisation = await Group(unit, "organisation");
            await Member(unit, "a", organisation); await Member(unit, "b", family);
            preview = (await unit.LabelMerges.PreviewAsync("a", "b", DateTimeOffset.UtcNow)).Preview!;
            Assert.Equal(2, preview.Memberships!.Count);
            await unit.CommitAsync();
        }
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await Member(unit, "c", family); await unit.CommitAsync();
        }
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            Assert.Equal(LabelMergeStatus.Stale, (await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "same", null, "test", DateTimeOffset.UtcNow)).Status);
            preview = (await unit.LabelMerges.PreviewAsync("a", "b", DateTimeOffset.UtcNow)).Preview!;
            Assert.Empty(preview.Blockers);
            Assert.Equal(LabelMergeStatus.Applied, (await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "same", null, "test", DateTimeOffset.UtcNow)).Status);
            var memberships = (await unit.LabelGroups.MembershipsAsync("a", null, new()))!.Items;
            Assert.Equal(2, memberships.Count);
            Assert.All(memberships, m => Assert.Equal("a", m.Label.Id));
            Assert.Single((await unit.LabelGroups.MembershipsAsync("c", null, new()))!.Items);
            await unit.CommitAsync();
        }
        Assert.Contains("memberships", (string)(await Sql("SELECT after_json FROM label_merge_audit LIMIT 1"))!, StringComparison.OrdinalIgnoreCase);
        using (var audit = JsonDocument.Parse((string)(await Sql("SELECT after_json FROM label_merge_audit LIMIT 1"))!))
        {
            await using var unit = await Provider.BeginUnitOfWorkAsync();
            foreach (var item in audit.RootElement.GetProperty("Memberships").EnumerateArray())
            {
                var membership = item.GetProperty("Membership");
                var current = (await unit.LabelGroups.MembershipAsync(membership.GetProperty("Id").GetString()!))!;
                Assert.Equal(current.Membership.Version, membership.GetProperty("Version").GetString());
                Assert.Equal(current.Group.Version, item.GetProperty("Group").GetProperty("Version").GetString());
                Assert.Equal(current.Label.Version, item.GetProperty("Label").GetProperty("Version").GetString());
            }
            await unit.CommitAsync();
        }
        Assert.Contains("\"label_id\":\"b\"", (string)(await Sql("SELECT before_json FROM label_group_membership_revision WHERE action='delete' AND before_json LIKE '%\"label_id\":\"b\"%'"))!);
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"""
            INSERT INTO label_group_membership VALUES('retired-direct','b','{family.Id}','current',NULL,NULL,'manual',NULL,NULL,'v','now','now')
            """));
    }
    [Fact]
    public async Task IncomingEdgeGuardsPreserveOtherOutgoingEdgesAndRejectBorrowedIdsAndCycles()
    {
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var a = (await unit.LabelRelationships.GetAsync("a"))!;
        var b = (await unit.Labels.GetByIdAsync("b"))!;
        var other = Assert.Single((await unit.LabelRelationships.ReplaceOutgoingAsync("a", a.Version,
            [new(null, "b", "distributed_by", "manual", null, "Unrelated", b.Version)])).Relationships!.Items);
        var parent = (await unit.LabelRelationships.GetAsync("parent"))!;
        a = (await unit.LabelRelationships.GetAsync("a"))!;
        var created = await unit.LabelRelationships.SaveEdgeAsync("parent", parent.Version,
            new(null, "a", "imprint_of", true, "manual", null, null, a.Version));
        Assert.Equal(LabelUpdateStatus.Updated, created.Status);
        Assert.Equal(2, (await unit.LabelRelationships.GetAsync("a"))!.Items.Count);
        Assert.Equal(other.Relationship, (await unit.LabelRelationships.GetAsync("a"))!.Items.Single(e => e.Relationship.Id == other.Relationship.Id).Relationship);
        parent = created.Relationships!;
        a = (await unit.LabelRelationships.GetAsync("a"))!;
        await Assert.ThrowsAsync<LabelRelationshipValidationException>(() => unit.LabelRelationships.SaveEdgeAsync("parent", parent.Version,
            new(other.Relationship.Id, "a", "imprint_of", true, "manual", null, null, a.Version)).AsTask());
        await Assert.ThrowsAsync<LabelRelationshipValidationException>(() => unit.LabelRelationships.SaveEdgeAsync("a", a.Version,
            new(null, "parent", "imprint_of", true, "manual", null, null, parent.Version)).AsTask());
        Assert.Equal(a.Version, (await unit.LabelRelationships.GetAsync("a"))!.Version);
        Assert.Equal(parent.Version, (await unit.LabelRelationships.GetAsync("parent"))!.Version);
        await unit.CommitAsync();
    }
}
