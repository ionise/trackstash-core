namespace TrackStash.Core.Identifiers;

/// <summary>
/// Canonical entity ID generation for all TrackStash canonical entity types.
/// IDs are lowercase hex GUIDs without hyphens (32 characters).
/// </summary>
public static class EntityId
{
    /// <summary>
    /// Generate a new canonical entity ID.
    /// </summary>
    public static string New() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Returns true if the given string looks like a valid canonical entity ID.
    /// </summary>
    public static bool IsValid(string? id) =>
        id is { Length: 32 } && id.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
}
