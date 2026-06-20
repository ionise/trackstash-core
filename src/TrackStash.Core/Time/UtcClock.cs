namespace TrackStash.Core.Time;

/// <summary>
/// UTC timestamp helpers for consistent timestamp formatting across all storage writes.
/// All timestamps stored in TrackStash use ISO 8601 round-trip format ("O").
/// </summary>
public static class UtcClock
{
    /// <summary>
    /// Current UTC time as a <see cref="DateTimeOffset"/>.
    /// </summary>
    public static DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>
    /// Current UTC time formatted as ISO 8601 round-trip string for storage column writes.
    /// </summary>
    public static string NowString => DateTimeOffset.UtcNow.ToString("O");

    /// <summary>
    /// Format a <see cref="DateTimeOffset"/> as an ISO 8601 round-trip string for storage.
    /// </summary>
    public static string Format(DateTimeOffset value) => value.ToString("O");

    /// <summary>
    /// Parse an ISO 8601 round-trip string back to a <see cref="DateTimeOffset"/>.
    /// Returns null if the input is null or empty.
    /// </summary>
    public static DateTimeOffset? ParseOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : DateTimeOffset.Parse(value);
}
