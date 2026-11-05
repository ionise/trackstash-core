using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteLabelRelationshipRepository(SqliteConnection connection, SqliteTransaction transaction) : ILabelRelationshipRepository
{
    private readonly SqliteLabelRepository _labels = new(connection, transaction);
    internal async ValueTask<LabelRelationship[]> ReadAllAsync(CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT relationship_id,from_label_id,to_label_id,relationship_type,source,source_url,notes,created_utc FROM label_relationship ORDER BY relationship_id";
        var result = new List<LabelRelationship>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7))));
        return result.ToArray();
    }
    public async ValueTask<LabelRelationships?> GetAsync(string labelId, CancellationToken ct = default)
    {
        var identity = await _labels.ResolveIdentityAsync(labelId, ct);
        if (identity.IsRetired) throw new RetiredLabelException(labelId, identity.CanonicalId!);
        var label = await _labels.GetByIdAsync(labelId, ct);
        if (label is null) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.relationship_id,r.from_label_id,r.to_label_id,r.relationship_type,r.source,r.source_url,r.notes,r.created_utc,
                   f.name,f.version,t.name,t.version
            FROM label_relationship r JOIN label f ON f.label_id=r.from_label_id JOIN label t ON t.label_id=r.to_label_id
            WHERE r.from_label_id=@id OR r.to_label_id=@id ORDER BY r.relationship_type,f.name,t.name,r.relationship_id
            """;
        command.Parameters.AddWithValue("@id", labelId);
        var items = new List<LabelRelationshipView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var from = reader.GetString(1); var to = reader.GetString(2);
            items.Add(new(new(reader.GetString(0), from, to, reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7))),
                new(from, reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9)),
                new(to, reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11))));
        }
        return new(labelId, label.Version, items);
    }
    public async ValueTask<LabelRelationshipResult> ReplaceOutgoingAsync(string labelId, string expectedVersion,
        IReadOnlyList<LabelRelationshipInput> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > LabelRelationshipRules.MaximumOutgoing) throw new LabelRelationshipValidationException("At most 100 outgoing relationships are supported.");
        var current = await GetAsync(labelId, ct);
        if (current is null) return new(LabelUpdateStatus.NotFound);
        if (current.Version != expectedVersion) return new(LabelUpdateStatus.Stale);
        var all = await ReadAllAsync(ct);
        var outgoing = all.Where(e => e.FromLabelId == labelId).ToDictionary(e => e.Id);
        var next = new List<LabelRelationship>();
        var ids = new HashSet<string>();
        var pairs = new HashSet<(string, string)>();
        foreach (var item in items)
        {
            if (item is null) throw new LabelRelationshipValidationException("A relationship cannot be null.");
            LabelRelationship? previous = null;
            if (item.Id is not null && !outgoing.TryGetValue(item.Id, out previous))
                throw new LabelRelationshipValidationException("Relationship ID does not belong to this label.");
            var edge = new LabelRelationship(previous?.Id ?? Guid.NewGuid().ToString("N"), labelId, item.ToLabelId,
                item.Type, item.Source, item.SourceUrl, item.Notes, previous?.CreatedUtc ?? DateTimeOffset.UtcNow);
            LabelRelationshipRules.Validate(edge);
            if (!ids.Add(edge.Id) || !pairs.Add((edge.ToLabelId, edge.Type))) throw new LabelRelationshipValidationException("Duplicate relationships are not allowed.");
            var identity = await _labels.ResolveIdentityAsync(edge.ToLabelId, ct);
            if (identity.IsRetired) throw new RetiredLabelException(edge.ToLabelId, identity.CanonicalId!);
            var target = await _labels.GetByIdAsync(edge.ToLabelId, ct);
            if (target is null) throw new LabelRelationshipValidationException("Related label not found.");
            if (target.Version != item.TargetVersion) return new(LabelUpdateStatus.Stale);
            next.Add(edge);
        }
        if (LabelRelationshipRules.HasImprintCycle(all.Where(e => e.FromLabelId != labelId).Concat(next)))
            throw new LabelRelationshipValidationException("Imprint relationships cannot form a cycle.");
        await ExecuteAsync("SAVEPOINT relationship_write", ct);
        try
        {
            // Removing first avoids transient cycles when reversing/replacing several edges in one reviewed operation.
            foreach (var previous in outgoing.Values.Where(p => !next.Contains(p)))
                await ExecuteAsync("DELETE FROM label_relationship WHERE relationship_id=@id", ct, ("id", previous.Id));
            foreach (var edge in next.Where(e => !outgoing.Values.Contains(e))) await InsertAsync(edge, ct);
            await ExecuteAsync("RELEASE relationship_write", ct);
        }
        catch
        {
            await ExecuteAsync("ROLLBACK TO relationship_write; RELEASE relationship_write", CancellationToken.None);
            throw;
        }
        return new(LabelUpdateStatus.Updated, await GetAsync(labelId, ct));
    }
    internal Task InsertAsync(LabelRelationship edge, CancellationToken ct) => ExecuteAsync("""
        INSERT INTO label_relationship(relationship_id,from_label_id,to_label_id,relationship_type,source,source_url,notes,created_utc)
        VALUES(@id,@from,@to,@type,@source,@url,@notes,@created)
        """, ct, ("id", edge.Id), ("from", edge.FromLabelId), ("to", edge.ToLabelId), ("type", edge.Type),
        ("source", edge.Source), ("url", edge.SourceUrl), ("notes", edge.Notes), ("created", edge.CreatedUtc.ToString("O")));
    public async ValueTask<LabelRelationshipResult> SaveEdgeAsync(string labelId, string expectedVersion, LabelEdgeInput input, CancellationToken ct = default)
    {
        var owner = await GetAsync(labelId, ct);
        if (owner is null) return new(LabelUpdateStatus.NotFound);
        if (owner.Version != expectedVersion) return new(LabelUpdateStatus.Stale);
        var related = await GetAsync(input.RelatedLabelId, ct);
        if (related is null) return new(LabelUpdateStatus.NotFound);
        if (related.Version != input.TargetVersion) return new(LabelUpdateStatus.Stale);
        var from = input.Incoming ? related : owner;
        var to = input.Incoming ? owner : related;
        var previous = input.Id is null ? null : from.Items.SingleOrDefault(e => e.Relationship.Id == input.Id);
        if (input.Id is not null && (previous is null || previous.From.Id != from.LabelId || previous.To.Id != to.LabelId))
            throw new LabelRelationshipValidationException("Relationship does not belong to these endpoints.");
        if (input.Remove && input.Id is null) throw new LabelRelationshipValidationException("Select an existing relationship to remove.");
        var drafts = from.Items.Where(e => e.From.Id == from.LabelId && e.Relationship.Id != input.Id)
            .Select(e => new LabelRelationshipInput(e.Relationship.Id, e.To.Id, e.Relationship.Type, e.Relationship.Source,
                e.Relationship.SourceUrl, e.Relationship.Notes, e.To.Version)).ToList();
        if (!input.Remove) drafts.Add(new(input.Id, to.LabelId, input.Type, input.Source, input.SourceUrl, input.Notes, to.Version));
        var result = await ReplaceOutgoingAsync(from.LabelId, from.Version, drafts, ct);
        return result.Status == LabelUpdateStatus.Updated ? new(result.Status, await GetAsync(labelId, ct)) : result;
    }
    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
