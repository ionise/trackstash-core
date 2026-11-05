namespace TrackStash.Core.Storage;

public sealed record LabelRelationship(string Id, string FromLabelId, string ToLabelId, string Type,
    string Source, string? SourceUrl, string? Notes, DateTimeOffset CreatedUtc);
public sealed record RelatedLabel(string Id, string? Name, string Version);
public sealed record LabelRelationshipView(LabelRelationship Relationship, RelatedLabel From, RelatedLabel To);
public sealed record LabelRelationships(string LabelId, string Version, IReadOnlyList<LabelRelationshipView> Items);
public sealed record LabelRelationshipInput(string? Id, string ToLabelId, string Type, string Source,
    string? SourceUrl, string? Notes, string TargetVersion);
public sealed record LabelRelationshipResult(LabelUpdateStatus Status, LabelRelationships? Relationships = null);
public sealed record LabelEdgeInput(string? Id, string RelatedLabelId, string Type, bool Incoming,
    string Source, string? SourceUrl, string? Notes, string TargetVersion, bool Remove = false);
public sealed class LabelRelationshipValidationException(string message) : ArgumentException(message);

public interface ILabelRelationshipRepository
{
    ValueTask<LabelRelationships?> GetAsync(string labelId, CancellationToken ct = default);
    ValueTask<LabelRelationshipResult> ReplaceOutgoingAsync(string labelId, string expectedVersion,
        IReadOnlyList<LabelRelationshipInput> items, CancellationToken ct = default);
    ValueTask<LabelRelationshipResult> SaveEdgeAsync(string labelId, string expectedVersion, LabelEdgeInput input, CancellationToken ct = default);
}

public static class LabelRelationshipRules
{
    public const int MaximumOutgoing = 100;
    public static void Validate(LabelRelationship edge)
    {
        if (string.IsNullOrWhiteSpace(edge.FromLabelId) || string.IsNullOrWhiteSpace(edge.ToLabelId))
            throw new LabelRelationshipValidationException("Both labels are required.");
        if (edge.FromLabelId == edge.ToLabelId) throw new LabelRelationshipValidationException("A label cannot relate to itself.");
        if (edge.Type is not ("imprint_of" or "distributed_by")) throw new LabelRelationshipValidationException("Use imprint_of or distributed_by.");
        if (string.IsNullOrWhiteSpace(edge.Source) || edge.Source.Length > 200)
            throw new LabelRelationshipValidationException("Source is required and must be at most 200 characters.");
        if (edge.SourceUrl is not null)
        {
            try { LabelProfile.ValidateUrl(edge.SourceUrl, nameof(edge.SourceUrl)); }
            catch (ArgumentException) { throw new LabelRelationshipValidationException("Evidence URL must be an absolute HTTP(S) URL of at most 2048 characters without embedded credentials."); }
        }
        if (edge.Notes?.Length > 2000) throw new LabelRelationshipValidationException("Relationship notes exceed 2000 characters.");
    }

    public static bool HasImprintCycle(IEnumerable<LabelRelationship> relationships)
    {
        var graph = relationships.Where(r => r.Type == "imprint_of").GroupBy(r => r.FromLabelId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ToLabelId).Distinct().ToArray());
        var indegree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (from, targets) in graph)
        {
            indegree.TryAdd(from, 0);
            foreach (var target in targets) indegree[target] = indegree.GetValueOrDefault(target) + 1;
        }
        var ready = new Queue<string>(indegree.Where(p => p.Value == 0).Select(p => p.Key));
        var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            if (!graph.TryGetValue(id, out var targets)) continue;
            foreach (var target in targets)
                if (--indegree[target] == 0) ready.Enqueue(target);
        }
        return visited != indegree.Count;
    }
}
