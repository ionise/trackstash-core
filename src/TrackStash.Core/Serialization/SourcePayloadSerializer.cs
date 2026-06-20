using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrackStash.Core.Serialization;

/// <summary>
/// Serialization helpers for <c>source_payload_json</c> columns and any other
/// structured JSON stored alongside canonical entities.
/// Uses web-defaults (camelCase, case-insensitive reads) with null values omitted.
/// </summary>
public static class SourcePayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Serialize a value to a JSON string suitable for storage in a payload column.
    /// </summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Deserialize a JSON string from a payload column. Returns null if the input is null.
    /// </summary>
    public static T? Deserialize<T>(string? json) =>
        json is null ? default : JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// Try to deserialize a JSON string, returning the default value on failure.
    /// </summary>
    public static T? TryDeserialize<T>(string? json, T? defaultValue = default)
    {
        if (json is null)
            return defaultValue;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return defaultValue;
        }
    }
}
