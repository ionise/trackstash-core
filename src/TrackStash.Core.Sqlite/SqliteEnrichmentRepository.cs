using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteEnrichmentRepository(SqliteConnection connection, SqliteTransaction transaction) : IEnrichmentRepository
{
    public async ValueTask UpdateEntityAsync(CanonicalEntity entity, string expectedVersion, CancellationToken ct = default)
    {
        var table = entity switch
        {
            Recording => "recording", Release => "release",
            _ => throw new CatalogFoundationValidationException("Reviewed enrichment supports Recordings and Releases only."),
        };
        var title = entity switch { Recording r => r.Title, Release r => r.Title, _ => null };
        if (title is not null && string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(expectedVersion))
            throw new CatalogFoundationValidationException("A supplied title must be nonblank; expected version is required.");
        var recording = entity as Recording;
        var release = entity as Release;
        if (release?.PublishDate is { } date && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new CatalogFoundationValidationException("Edition date must be an exact valid date.");
        using var command = Command($"""
            UPDATE {table} SET name=@name,title=@title,normalized_name=@normalized,
                {(recording is not null ? "mix_name=@mix,isrc=@isrc" : "catalog_number=@catalog,upc=@upc,publish_date=@publish")}
            WHERE {table}_id=@id AND version=@version
            """, ("@id", entity.Id), ("@version", expectedVersion), ("@name", entity.Name), ("@title", title),
            ("@normalized", entity.NormalizedName), ("@mix", recording?.MixName), ("@isrc", recording?.Isrc),
            ("@catalog", release?.CatalogNumber), ("@upc", release?.Upc), ("@publish", release?.PublishDate));
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new CatalogFoundationStaleException("Entity changed since review.");
        foreach (var reference in entity.ExternalReferences)
        {
            using var add = Command($"""
                INSERT INTO {table}_external_ref({table}_id,source,external_id,is_primary,last_seen_utc,payload_json)
                VALUES(@id,@source,@external,@primary,@utc,@payload)
                ON CONFLICT({table}_id,source,external_id) DO NOTHING
                """, ("@id", entity.Id), ("@source", reference.Source), ("@external", reference.ExternalId),
                ("@primary", reference.IsPrimary ? 1 : 0), ("@utc", reference.LastSeenUtc?.ToString("O")), ("@payload", reference.PayloadJson));
            await add.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
    private static string Table(CatalogEntityKind kind) => kind switch
    {
        CatalogEntityKind.Recording => "recording_musical_metadata",
        CatalogEntityKind.Release => "release_musical_metadata",
        _ => throw new CatalogFoundationValidationException("Musical metadata supports Recordings and Releases only."),
    };
    private SqliteCommand Command(string sql, params (string, object?)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public async ValueTask<CatalogMusicalMetadata> GetMetadataAsync(CatalogEntityKind kind, string id, CancellationToken ct = default)
    {
        using var command = Command($"SELECT bpm,key_value,key_notation,genres_json,subgenres_json,preferred_genre FROM {Table(kind)} WHERE entity_id=@id", ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new(Genres: [], Subgenres: []);
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return new(reader.IsDBNull(0) ? null : reader.GetDecimal(0), Text(1), Text(2),
            JsonSerializer.Deserialize<string[]>(reader.GetString(3))!, JsonSerializer.Deserialize<string[]>(reader.GetString(4))!, Text(5));
    }
    public async ValueTask SaveMetadataAsync(CatalogEntityKind kind, string id, CatalogMusicalMetadata metadata, CancellationToken ct = default)
    {
        var genres = metadata.Genres ?? [];
        var subgenres = metadata.Subgenres ?? [];
        if (metadata.Bpm is <= 0 or > 1000 || (metadata.KeyValue is null) != (metadata.KeyNotation is null) ||
            metadata.KeyValue is { Length: > 100 } || metadata.KeyNotation is { Length: > 100 } ||
            metadata.KeyValue is not null && (string.IsNullOrWhiteSpace(metadata.KeyValue) || string.IsNullOrWhiteSpace(metadata.KeyNotation)) ||
            kind == CatalogEntityKind.Release && (metadata.Bpm is not null || metadata.KeyValue is not null) ||
            genres.Count > 100 || subgenres.Count > 100 || genres.Concat(subgenres).Any(g => string.IsNullOrWhiteSpace(g) || g.Length > 200) ||
            metadata.PreferredGenre is not null && !genres.Concat(subgenres).Contains(metadata.PreferredGenre, StringComparer.Ordinal))
            throw new CatalogFoundationValidationException("Invalid musical metadata or preferred classification.");
        using var command = Command($"""
            INSERT INTO {Table(kind)}(entity_id,bpm,key_value,key_notation,genres_json,subgenres_json,preferred_genre)
            VALUES(@id,@bpm,@key,@notation,@genres,@subgenres,@preferred)
            ON CONFLICT(entity_id) DO UPDATE SET bpm=excluded.bpm,key_value=excluded.key_value,key_notation=excluded.key_notation,
              genres_json=excluded.genres_json,subgenres_json=excluded.subgenres_json,preferred_genre=excluded.preferred_genre
            """, ("@id", id), ("@bpm", metadata.Bpm), ("@key", metadata.KeyValue), ("@notation", metadata.KeyNotation),
            ("@genres", JsonSerializer.Serialize(genres)), ("@subgenres", JsonSerializer.Serialize(subgenres)), ("@preferred", metadata.PreferredGenre));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    public async ValueTask SaveReviewAsync(StoredEnrichmentReview review, CancellationToken ct = default)
    {
        _ = Table(review.Kind);
        using var command = Command("""
            INSERT INTO enrichment_review(review_id,entity_kind,entity_id,version,payload_json,created_utc)
            VALUES(@id,@kind,@entity,@version,@payload,@utc)
            """, ("@id", review.Id), ("@kind", review.Kind.ToString()), ("@entity", review.EntityId),
            ("@version", review.Version), ("@payload", review.PayloadJson), ("@utc", review.CreatedAt.ToString("O")));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    public async ValueTask<StoredEnrichmentReview?> GetReviewAsync(string id, CancellationToken ct = default)
    {
        using var command = Command("SELECT entity_kind,entity_id,version,payload_json,created_utc FROM enrichment_review WHERE review_id=@id", ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new(id, Enum.Parse<CatalogEntityKind>(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)) : null;
    }
    public async ValueTask<EnrichmentApplyAudit?> GetAuditAsync(string reviewId, CancellationToken ct = default)
    {
        using var command = Command("SELECT fields_json,reason,before_json,after_json,applied_utc FROM enrichment_apply_revision WHERE review_id=@id", ("@id", reviewId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new(reviewId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)) : null;
    }
    public async ValueTask SaveAuditAsync(EnrichmentApplyAudit audit, CancellationToken ct = default)
    {
        using var command = Command("""
            INSERT INTO enrichment_apply_revision(review_id,fields_json,reason,before_json,after_json,applied_utc)
            VALUES(@id,@fields,@reason,@before,@after,@utc)
            """, ("@id", audit.ReviewId), ("@fields", audit.FieldsJson), ("@reason", audit.Reason),
            ("@before", audit.BeforeJson), ("@after", audit.AfterJson), ("@utc", audit.AppliedAt.ToString("O")));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
