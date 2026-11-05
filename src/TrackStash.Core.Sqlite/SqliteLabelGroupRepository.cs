using Microsoft.Data.Sqlite;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteLabelGroupRepository(SqliteConnection connection, SqliteTransaction transaction) : ILabelGroupRepository
{
    private readonly SqliteLabelRepository _labels = new(connection, transaction);
    private const string GroupColumns = "g.group_id,g.kind,g.name,g.sort_name,g.description,g.website,g.version,g.created_utc,g.updated_utc";
    private const string MemberColumns = """
        m.membership_id,m.label_id,m.group_id,m.status,m.since_date,m.until_date,m.source,m.source_url,m.notes,
        m.version,m.created_utc,m.updated_utc,l.name,l.version,
        """;
    private const string MemberJoin = " FROM label_group_membership m JOIN label l ON l.label_id=m.label_id JOIN label_group g ON g.group_id=m.group_id ";
    private const string CurrentCount = "(SELECT count(*) FROM label_group_membership c WHERE c.group_id=g.group_id AND c.status='current')";
    private SqliteCommand Command(string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);
        return command;
    }
    private async Task Execute(string sql, CancellationToken ct, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, values); await command.ExecuteNonQueryAsync(ct);
    }
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static LabelGroup Group(SqliteDataReader r, int offset = 0, int count = 0) => new(
        r.GetString(offset), r.GetString(offset + 1), r.GetString(offset + 2), Text(r, offset + 3),
        Text(r, offset + 4), Text(r, offset + 5), r.GetString(offset + 6),
        DateTimeOffset.Parse(r.GetString(offset + 7)), DateTimeOffset.Parse(r.GetString(offset + 8)), count);
    private static GroupMembership Membership(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2),
        new(r.GetString(3), Text(r, 4), Text(r, 5), r.GetString(6), Text(r, 7), Text(r, 8)),
        r.GetString(9), DateTimeOffset.Parse(r.GetString(10)), DateTimeOffset.Parse(r.GetString(11)));
    private static MembershipView View(SqliteDataReader r) => new(Membership(r), new(r.GetString(1), Text(r, 12), r.GetString(13)), Group(r, 14, r.GetInt32(23)));
    private async Task<bool> ActiveLabel(string id, CancellationToken ct)
    {
        var identity = await _labels.ResolveIdentityAsync(id, ct);
        if (identity.IsRetired) throw new RetiredLabelException(id, identity.CanonicalId!);
        return await _labels.GetByIdAsync(id, ct) is not null;
    }
    public async ValueTask<LabelGroup?> GetAsync(string id, CancellationToken ct = default)
    {
        using var command = Command($"SELECT {GroupColumns},{CurrentCount} FROM label_group g WHERE g.group_id=@id", ("id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Group(reader, count: reader.GetInt32(9)) : null;
    }
    public async ValueTask<GroupPage<LabelGroup>> QueryAsync(GroupQuery query, CancellationToken ct = default)
    {
        query.Validate();
        if (query.Status is not null) throw new GroupValidationException("Membership status is not a group-list filter.");
        if (query.Kind == "parent") throw new GroupValidationException("A parent label is not a group kind.");
        var where = " WHERE (@kind IS NULL OR g.kind=@kind) AND (@q IS NULL OR instr(g.normalized_name,@q)>0)";
        (string, object?)[] args = [("kind", query.Kind), ("q", query.Search is null ? null : EntityNameNormalizer.NormalizeStrict(query.Search)),
            ("limit", query.PageSize), ("offset", (query.Page - 1) * query.PageSize)];
        using var count = Command("SELECT count(*) FROM label_group g" + where, args);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        var sort = query.Sort switch { "id" => "g.group_id", "sortName" => "coalesce(g.sort_name,g.name)", _ => "g.name" };
        using var command = Command($"SELECT {GroupColumns},{CurrentCount} FROM label_group g{where} ORDER BY {sort} COLLATE NOCASE {(query.Descending ? "DESC" : "ASC")},g.group_id LIMIT @limit OFFSET @offset", args);
        var items = new List<LabelGroup>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Group(reader, count: reader.GetInt32(9)));
        return new(items, total, query.Page, query.PageSize);
    }
    public async ValueTask<GroupWriteResult> CreateAsync(string kind, GroupFields fields, CancellationToken ct = default)
    {
        fields.Validate();
        if (kind is not ("family" or "organisation")) throw new GroupValidationException("Choose family or organisation.");
        var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow.ToString("O");
        await Atomic(async () => await Execute("""
            INSERT INTO label_group(group_id,kind,name,normalized_name,sort_name,description,website,version,created_utc,updated_utc)
            VALUES(@id,@kind,@name,@norm,@sort,@description,@website,@version,@now,@now)
            """, ct, ("id", id), ("kind", kind), ("name", fields.Name.Trim()), ("norm", EntityNameNormalizer.NormalizeStrict(fields.Name)),
            ("sort", fields.SortName), ("description", fields.Description), ("website", fields.Website),
            ("version", Guid.NewGuid().ToString("N")), ("now", now)), ct);
        return new(GroupWriteStatus.Updated, await GetAsync(id, ct));
    }
    public async ValueTask<GroupWriteResult> UpdateAsync(string id, string version, GroupFields fields, CancellationToken ct = default)
    {
        fields.Validate();
        var current = await GetAsync(id, ct);
        if (current is null) return new(GroupWriteStatus.NotFound);
        if (current.Version != version) return new(GroupWriteStatus.Stale);
        await Atomic(async () => await Execute("""
            UPDATE label_group SET name=@name,normalized_name=@norm,sort_name=@sort,description=@description,website=@website,updated_utc=@now
            WHERE group_id=@id AND version=@version
            """, ct, ("id", id), ("version", version), ("name", fields.Name.Trim()), ("norm", EntityNameNormalizer.NormalizeStrict(fields.Name)),
            ("sort", fields.SortName), ("description", fields.Description), ("website", fields.Website), ("now", DateTimeOffset.UtcNow.ToString("O"))), ct);
        return new(GroupWriteStatus.Updated, await GetAsync(id, ct));
    }
    public async ValueTask<MembershipView?> MembershipAsync(string id, CancellationToken ct = default)
    {
        using var command = Command("SELECT " + MemberColumns + GroupColumns + "," + CurrentCount + MemberJoin + " WHERE m.membership_id=@id", ("id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? View(reader) : null;
    }
    public async ValueTask<GroupPage<MembershipView>?> MembershipsAsync(string? labelId, string? groupId, GroupQuery query, CancellationToken ct = default)
    {
        query.Validate();
        if (query.Search is not null || query.Sort != "name" || query.Descending)
            throw new GroupValidationException("Membership pages support status, kind and pagination, ordered by endpoint name.");
        if ((labelId is null) == (groupId is null)) throw new GroupValidationException("Select exactly one membership endpoint.");
        if (labelId is not null && !await ActiveLabel(labelId, ct) || groupId is not null && await GetAsync(groupId, ct) is null) return null;
        if (query.Kind == "parent") throw new GroupValidationException("A parent label is not a group kind.");
        var where = """
             WHERE (@label IS NULL OR m.label_id=@label) AND (@group IS NULL OR m.group_id=@group)
             AND (@status='all' OR m.status=@status) AND (@kind IS NULL OR g.kind=@kind)
            """;
        (string, object?)[] args = [("label", labelId), ("group", groupId), ("status", query.Status ?? "current"), ("kind", query.Kind),
            ("limit", query.PageSize), ("offset", (query.Page - 1) * query.PageSize)];
        using var count = Command("SELECT count(*)" + MemberJoin + where, args);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        var counts = new Dictionary<string, int> { ["current"] = 0, ["former"] = 0, ["unknown"] = 0 };
        using (var countsCommand = Command("""
            SELECT m.status,count(*) FROM label_group_membership m JOIN label_group g ON g.group_id=m.group_id
            WHERE (@label IS NULL OR m.label_id=@label) AND (@group IS NULL OR m.group_id=@group)
            AND (@kind IS NULL OR g.kind=@kind) GROUP BY m.status
            """, args))
        {
            await using var countsReader = await countsCommand.ExecuteReaderAsync(ct);
            while (await countsReader.ReadAsync(ct)) counts[countsReader.GetString(0)] = countsReader.GetInt32(1);
        }
        using var command = Command("SELECT " + MemberColumns + GroupColumns + "," + CurrentCount + MemberJoin + where +
            $" ORDER BY {(labelId is null ? "l.name" : "g.name")} COLLATE NOCASE,m.membership_id LIMIT @limit OFFSET @offset", args);
        var items = new List<MembershipView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(View(reader));
        return new(items, total, query.Page, query.PageSize, counts);
    }
    public async ValueTask<GroupWriteResult> SaveMembershipAsync(string? id, string? version, string labelId, string groupId,
        string labelVersion, string groupVersion, MembershipFields fields, bool confirmRejoin, CancellationToken ct = default)
    {
        fields.Validate(DateOnly.FromDateTime(DateTime.UtcNow));
        if (!await ActiveLabel(labelId, ct)) return new(GroupWriteStatus.NotFound);
        var label = (await _labels.GetByIdAsync(labelId, ct))!;
        var group = await GetAsync(groupId, ct);
        if (group is null) return new(GroupWriteStatus.NotFound);
        var previous = id is null ? null : await MembershipAsync(id, ct);
        if (id is not null && previous is null) return new(GroupWriteStatus.NotFound);
        if (previous is not null && (previous.Membership.LabelId != labelId || previous.Membership.GroupId != groupId))
            throw new GroupValidationException("Membership endpoints cannot be reassigned.");
        if (label.Version != labelVersion || group.Version != groupVersion || previous is not null && previous.Membership.Version != version)
            return new(GroupWriteStatus.Stale);
        if (previous?.Membership.Fields.Status == "former" && fields.Status == "current" && !confirmRejoin)
            throw new GroupValidationException("Confirm replacing the former period with the latest known membership period.");
        if (id is null)
        {
            using var duplicate = Command("SELECT membership_id FROM label_group_membership WHERE label_id=@label AND group_id=@group", ("label", labelId), ("group", groupId));
            if (await duplicate.ExecuteScalarAsync(ct) is string existing) return new(GroupWriteStatus.Conflict, Membership: await MembershipAsync(existing, ct));
        }
        id ??= Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var next = new GroupMembership(id, labelId, groupId, fields, Guid.NewGuid().ToString("N"), previous?.Membership.CreatedUtc ?? now, now);
        await Atomic(async () =>
        {
            if (previous is not null) await Execute("DELETE FROM label_group_membership WHERE membership_id=@id", ct, ("id", id));
            await InsertAsync(next, ct);
        }, ct);
        return new(GroupWriteStatus.Updated, Membership: await MembershipAsync(id, ct));
    }
    internal Task InsertAsync(GroupMembership m, CancellationToken ct) => Execute("""
        INSERT INTO label_group_membership VALUES(@id,@label,@group,@status,@since,@until,@source,@url,@notes,@version,@created,@updated)
        """, ct, ("id", m.Id), ("label", m.LabelId), ("group", m.GroupId), ("status", m.Fields.Status), ("since", m.Fields.Since),
        ("until", m.Fields.Until), ("source", m.Fields.Source), ("url", m.Fields.SourceUrl), ("notes", m.Fields.Notes),
        ("version", m.Version), ("created", m.CreatedUtc.ToString("O")), ("updated", m.UpdatedUtc.ToString("O")));
    public async ValueTask<GroupWriteResult> RemoveMembershipAsync(string id, string version, CancellationToken ct = default)
    {
        var current = await MembershipAsync(id, ct);
        if (current is null) return new(GroupWriteStatus.NotFound);
        if (!await ActiveLabel(current.Label.Id, ct)) return new(GroupWriteStatus.NotFound);
        if (current.Membership.Version != version) return new(GroupWriteStatus.Stale);
        await Atomic(async () => await Execute("DELETE FROM label_group_membership WHERE membership_id=@id", ct, ("id", id)), ct);
        return new(GroupWriteStatus.Updated);
    }
    internal async Task<GroupMembership[]> ForLabelsAsync(string a, string b, CancellationToken ct)
    {
        using var command = Command("SELECT " + MemberColumns + GroupColumns + MemberJoin + " WHERE m.label_id IN (@a,@b) ORDER BY m.membership_id", ("a", a), ("b", b));
        var items = new List<GroupMembership>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Membership(reader));
        return items.ToArray();
    }
    private async Task Atomic(Func<Task> action, CancellationToken ct)
    {
        await Execute("SAVEPOINT group_write", ct);
        try { await action(); await Execute("RELEASE group_write", ct); }
        catch { await Execute("ROLLBACK TO group_write; RELEASE group_write", CancellationToken.None); throw; }
    }
    public async ValueTask<GroupPage<RelatedLabelItem>?> RelatedAsync(string labelId, GroupQuery query, CancellationToken ct = default)
    {
        query.Validate();
        if (query.Search is not null || query.Status is not null || query.Sort != "name" || query.Descending)
            throw new GroupValidationException("Related-label pages support kind and pagination, ordered by label name.");
        if (!await ActiveLabel(labelId, ct)) return null;
        const string reasons = """
            WITH reasons AS (
                SELECT other.label_id, g.kind, g.group_id AS reason_id,g.name AS reason_name
                FROM label_group_membership own JOIN label_group_membership other ON own.group_id=other.group_id
                JOIN label_group g ON g.group_id=own.group_id
                WHERE own.label_id=@label AND other.label_id<>@label AND own.status='current' AND other.status='current'
                UNION
                SELECT other.from_label_id,'parent',p.label_id,p.name
                FROM label_relationship own JOIN label_relationship other ON own.to_label_id=other.to_label_id
                JOIN label p ON p.label_id=own.to_label_id
                WHERE own.from_label_id=@label AND other.from_label_id<>@label AND own.relationship_type='imprint_of' AND other.relationship_type='imprint_of'
            ), matching AS (SELECT * FROM reasons WHERE @kind IS NULL OR kind=@kind)
            """;
        (string, object?)[] args = [("label", labelId), ("kind", query.Kind), ("limit", query.PageSize), ("offset", (query.Page - 1) * query.PageSize)];
        using var count = Command(reasons + "SELECT count(DISTINCT label_id) FROM matching", args);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        using var command = Command(reasons + """
            , selected AS (
                SELECT l.label_id,l.name,l.version FROM label l WHERE l.label_id IN (SELECT label_id FROM matching)
                ORDER BY l.name COLLATE NOCASE,l.label_id LIMIT @limit OFFSET @offset
            )
            SELECT s.label_id,s.name,s.version,r.kind,r.reason_id,r.reason_name FROM selected s
            JOIN matching r ON r.label_id=s.label_id ORDER BY s.name COLLATE NOCASE,s.label_id,r.kind,r.reason_id
            """, args);
        var labels = new Dictionary<string, (RelatedLabel Label, List<RelatedLabelReason> Reasons)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            if (!labels.TryGetValue(id, out var item)) labels[id] = item = (new(id, Text(reader, 1), reader.GetString(2)), []);
            item.Reasons.Add(new(reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }
        return new(labels.Values.Select(v => new RelatedLabelItem(v.Label, v.Reasons)).ToArray(), total, query.Page, query.PageSize);
    }
}
