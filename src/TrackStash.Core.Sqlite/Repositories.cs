using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteLabelRepository : ILabelRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public SqliteLabelRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async ValueTask<Label?> GetByIdAsync(string labelId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT label_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc
            FROM label
            WHERE label_id = @labelId
            """;
        cmd.Parameters.AddWithValue("@labelId", labelId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadLabel(reader);
    }

    public async ValueTask<Label?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT l.label_id, l.name, l.normalized_name, l.sort_name, l.source_payload_json, l.created_utc, l.updated_utc
            FROM label l
            JOIN label_external_ref r ON r.label_id = l.label_id
            WHERE r.source = @source AND r.external_id = @externalId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@externalId", externalId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadLabel(reader);
    }

    public async ValueTask<Label?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT label_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc
            FROM label
            WHERE normalized_name = @normalizedName
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@normalizedName", normalizedName);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadLabel(reader);
    }

    public async ValueTask UpsertAsync(Label label, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            INSERT INTO label (label_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc)
            VALUES (@id, @name, @normalizedName, @sortName, @sourcePayloadJson, @createdUtc, @updatedUtc)
            ON CONFLICT (label_id) DO UPDATE SET
                name = excluded.name,
                normalized_name = excluded.normalized_name,
                sort_name = excluded.sort_name,
                source_payload_json = excluded.source_payload_json,
                updated_utc = excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("@id", label.Id);
        cmd.Parameters.AddWithValue("@name", label.Name ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@normalizedName", label.NormalizedName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sortName", label.SortName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourcePayloadJson", label.SourcePayloadJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdUtc", label.CreatedUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedUtc", label.UpdatedUtc?.ToString("O") ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var externalRef in label.ExternalReferences)
            await UpsertExternalRefAsync(label.Id, externalRef, cancellationToken).ConfigureAwait(false);

        foreach (var alias in label.Aliases)
            await UpsertAliasAsync(label.Id, alias, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UpsertExternalRefAsync(string labelId, EntityReference externalRef, CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            INSERT INTO label_external_ref (label_id, source, external_id, is_primary, last_seen_utc, payload_json)
            VALUES (@labelId, @source, @externalId, @isPrimary, @lastSeenUtc, @payloadJson)
            ON CONFLICT (label_id, source, external_id) DO UPDATE SET
                is_primary = excluded.is_primary,
                last_seen_utc = excluded.last_seen_utc,
                payload_json = excluded.payload_json
            """;
        cmd.Parameters.AddWithValue("@labelId", labelId);
        cmd.Parameters.AddWithValue("@source", externalRef.Source);
        cmd.Parameters.AddWithValue("@externalId", externalRef.ExternalId);
        cmd.Parameters.AddWithValue("@isPrimary", externalRef.IsPrimary ? 1 : 0);
        cmd.Parameters.AddWithValue("@lastSeenUtc", externalRef.LastSeenUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@payloadJson", externalRef.PayloadJson ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UpsertAliasAsync(string labelId, EntityAlias alias, CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            INSERT INTO label_alias (label_id, value, normalized_value, is_primary)
            VALUES (@labelId, @value, @normalizedValue, @isPrimary)
            ON CONFLICT (label_id, value) DO UPDATE SET
                normalized_value = excluded.normalized_value,
                is_primary = excluded.is_primary
            """;
        cmd.Parameters.AddWithValue("@labelId", labelId);
        cmd.Parameters.AddWithValue("@value", alias.Value);
        cmd.Parameters.AddWithValue("@normalizedValue", alias.NormalizedValue ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@isPrimary", alias.IsPrimary ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Label ReadLabel(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.IsDBNull(1) ? null : reader.GetString(1),
        NormalizedName = reader.IsDBNull(2) ? null : reader.GetString(2),
        SortName = reader.IsDBNull(3) ? null : reader.GetString(3),
        SourcePayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedUtc = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
        UpdatedUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
    };
}

internal sealed class SqliteArtistRepository : IArtistRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public SqliteArtistRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async ValueTask<Artist?> GetByIdAsync(string artistId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT artist_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc
            FROM artist
            WHERE artist_id = @artistId
            """;
        cmd.Parameters.AddWithValue("@artistId", artistId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadArtist(reader);
    }

    public async ValueTask<Artist?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT a.artist_id, a.name, a.normalized_name, a.sort_name, a.source_payload_json, a.created_utc, a.updated_utc
            FROM artist a
            JOIN artist_external_ref r ON r.artist_id = a.artist_id
            WHERE r.source = @source AND r.external_id = @externalId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@externalId", externalId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadArtist(reader);
    }

    public async ValueTask<Artist?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT artist_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc
            FROM artist
            WHERE normalized_name = @normalizedName
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@normalizedName", normalizedName);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadArtist(reader);
    }

    public async ValueTask UpsertAsync(Artist artist, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            INSERT INTO artist (artist_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc)
            VALUES (@id, @name, @normalizedName, @sortName, @sourcePayloadJson, @createdUtc, @updatedUtc)
            ON CONFLICT (artist_id) DO UPDATE SET
                name = excluded.name,
                normalized_name = excluded.normalized_name,
                sort_name = excluded.sort_name,
                source_payload_json = excluded.source_payload_json,
                updated_utc = excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("@id", artist.Id);
        cmd.Parameters.AddWithValue("@name", artist.Name ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@normalizedName", artist.NormalizedName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sortName", artist.SortName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourcePayloadJson", artist.SourcePayloadJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdUtc", artist.CreatedUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedUtc", artist.UpdatedUtc?.ToString("O") ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var alias in artist.Aliases)
        {
            using var aliasCmd = _connection.CreateCommand();
            aliasCmd.Transaction = _transaction;
            aliasCmd.CommandText = """
                INSERT INTO artist_alias (artist_id, value, normalized_value, is_primary)
                VALUES (@artistId, @value, @normalizedValue, @isPrimary)
                ON CONFLICT (artist_id, value) DO UPDATE SET
                    normalized_value = excluded.normalized_value,
                    is_primary = excluded.is_primary
                """;
            aliasCmd.Parameters.AddWithValue("@artistId", artist.Id);
            aliasCmd.Parameters.AddWithValue("@value", alias.Value);
            aliasCmd.Parameters.AddWithValue("@normalizedValue", alias.NormalizedValue ?? (object)DBNull.Value);
            aliasCmd.Parameters.AddWithValue("@isPrimary", alias.IsPrimary ? 1 : 0);
            await aliasCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var externalRef in artist.ExternalReferences)
        {
            using var refCmd = _connection.CreateCommand();
            refCmd.Transaction = _transaction;
            refCmd.CommandText = """
                INSERT INTO artist_external_ref (artist_id, source, external_id, is_primary, last_seen_utc, payload_json)
                VALUES (@artistId, @source, @externalId, @isPrimary, @lastSeenUtc, @payloadJson)
                ON CONFLICT (artist_id, source, external_id) DO UPDATE SET
                    is_primary = excluded.is_primary,
                    last_seen_utc = excluded.last_seen_utc,
                    payload_json = excluded.payload_json
                """;
            refCmd.Parameters.AddWithValue("@artistId", artist.Id);
            refCmd.Parameters.AddWithValue("@source", externalRef.Source);
            refCmd.Parameters.AddWithValue("@externalId", externalRef.ExternalId);
            refCmd.Parameters.AddWithValue("@isPrimary", externalRef.IsPrimary ? 1 : 0);
            refCmd.Parameters.AddWithValue("@lastSeenUtc", externalRef.LastSeenUtc?.ToString("O") ?? (object)DBNull.Value);
            refCmd.Parameters.AddWithValue("@payloadJson", externalRef.PayloadJson ?? (object)DBNull.Value);
            await refCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static Artist ReadArtist(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.IsDBNull(1) ? null : reader.GetString(1),
        NormalizedName = reader.IsDBNull(2) ? null : reader.GetString(2),
        SortName = reader.IsDBNull(3) ? null : reader.GetString(3),
        SourcePayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedUtc = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
        UpdatedUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
    };
}

internal sealed class SqliteReleaseRepository : IReleaseRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public SqliteReleaseRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async ValueTask<Release?> GetByIdAsync(string releaseId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT release_id, name, normalized_name, title, source_payload_json, created_utc, updated_utc
            FROM release
            WHERE release_id = @releaseId
            """;
        cmd.Parameters.AddWithValue("@releaseId", releaseId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadRelease(reader);
    }

    public async ValueTask<Release?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT r.release_id, r.name, r.normalized_name, r.title, r.source_payload_json, r.created_utc, r.updated_utc
            FROM release r
            JOIN release_external_ref ref ON ref.release_id = r.release_id
            WHERE ref.source = @source AND ref.external_id = @externalId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@externalId", externalId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadRelease(reader);
    }

    public async ValueTask<Release?> GetByNormalizedTitleAndLabelAsync(string normalizedTitle, string? labelId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;

        if (labelId is not null)
        {
            cmd.CommandText = """
                SELECT r.release_id, r.name, r.normalized_name, r.title, r.source_payload_json, r.created_utc, r.updated_utc
                FROM release r
                JOIN release_label_link l ON l.release_id = r.release_id
                WHERE r.normalized_name = @normalizedTitle AND l.label_id = @labelId
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@normalizedTitle", normalizedTitle);
            cmd.Parameters.AddWithValue("@labelId", labelId);
        }
        else
        {
            cmd.CommandText = """
                SELECT release_id, name, normalized_name, title, source_payload_json, created_utc, updated_utc
                FROM release
                WHERE normalized_name = @normalizedTitle
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@normalizedTitle", normalizedTitle);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadRelease(reader);
    }

    public async ValueTask UpsertAsync(Release release, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            INSERT INTO release (release_id, name, normalized_name, title, source_payload_json, created_utc, updated_utc)
            VALUES (@id, @name, @normalizedName, @title, @sourcePayloadJson, @createdUtc, @updatedUtc)
            ON CONFLICT (release_id) DO UPDATE SET
                name = excluded.name,
                normalized_name = excluded.normalized_name,
                title = excluded.title,
                source_payload_json = excluded.source_payload_json,
                updated_utc = excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("@id", release.Id);
        cmd.Parameters.AddWithValue("@name", release.Name ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@normalizedName", release.NormalizedName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@title", release.Title ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourcePayloadJson", release.SourcePayloadJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdUtc", release.CreatedUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedUtc", release.UpdatedUtc?.ToString("O") ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var externalRef in release.ExternalReferences)
        {
            using var refCmd = _connection.CreateCommand();
            refCmd.Transaction = _transaction;
            refCmd.CommandText = """
                INSERT INTO release_external_ref (release_id, source, external_id, is_primary, last_seen_utc, payload_json)
                VALUES (@releaseId, @source, @externalId, @isPrimary, @lastSeenUtc, @payloadJson)
                ON CONFLICT (release_id, source, external_id) DO UPDATE SET
                    is_primary = excluded.is_primary,
                    last_seen_utc = excluded.last_seen_utc,
                    payload_json = excluded.payload_json
                """;
            refCmd.Parameters.AddWithValue("@releaseId", release.Id);
            refCmd.Parameters.AddWithValue("@source", externalRef.Source);
            refCmd.Parameters.AddWithValue("@externalId", externalRef.ExternalId);
            refCmd.Parameters.AddWithValue("@isPrimary", externalRef.IsPrimary ? 1 : 0);
            refCmd.Parameters.AddWithValue("@lastSeenUtc", externalRef.LastSeenUtc?.ToString("O") ?? (object)DBNull.Value);
            refCmd.Parameters.AddWithValue("@payloadJson", externalRef.PayloadJson ?? (object)DBNull.Value);
            await refCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var credit in release.ArtistCredits)
        {
            using var creditCmd = _connection.CreateCommand();
            creditCmd.Transaction = _transaction;
            creditCmd.CommandText = """
                INSERT INTO release_artist_credit (release_id, artist_id, credit_name, position)
                VALUES (@releaseId, @artistId, @creditName, @position)
                ON CONFLICT (release_id, artist_id) DO UPDATE SET
                    credit_name = excluded.credit_name,
                    position = excluded.position
                """;
            creditCmd.Parameters.AddWithValue("@releaseId", release.Id);
            creditCmd.Parameters.AddWithValue("@artistId", credit.ArtistId);
            creditCmd.Parameters.AddWithValue("@creditName", credit.CreditName ?? (object)DBNull.Value);
            creditCmd.Parameters.AddWithValue("@position", credit.Position ?? (object)DBNull.Value);
            await creditCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var link in release.LabelLinks)
        {
            using var linkCmd = _connection.CreateCommand();
            linkCmd.Transaction = _transaction;
            linkCmd.CommandText = """
                INSERT INTO release_label_link (release_id, label_id, is_primary, role)
                VALUES (@releaseId, @labelId, @isPrimary, @role)
                ON CONFLICT (release_id, label_id) DO UPDATE SET
                    is_primary = excluded.is_primary,
                    role = excluded.role
                """;
            linkCmd.Parameters.AddWithValue("@releaseId", release.Id);
            linkCmd.Parameters.AddWithValue("@labelId", link.LabelId);
            linkCmd.Parameters.AddWithValue("@isPrimary", link.IsPrimary ? 1 : 0);
            linkCmd.Parameters.AddWithValue("@role", link.Role ?? (object)DBNull.Value);
            await linkCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static Release ReadRelease(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.IsDBNull(1) ? null : reader.GetString(1),
        NormalizedName = reader.IsDBNull(2) ? null : reader.GetString(2),
        Title = reader.IsDBNull(3) ? null : reader.GetString(3),
        SourcePayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedUtc = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
        UpdatedUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
    };
}

internal sealed class SqliteRecordingRepository : IRecordingRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public SqliteRecordingRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async ValueTask<Recording?> GetByIdAsync(string recordingId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT recording_id, name, normalized_name, title, mix_name, isrc, source_payload_json, created_utc, updated_utc
            FROM recording
            WHERE recording_id = @recordingId
            """;
        cmd.Parameters.AddWithValue("@recordingId", recordingId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadRecording(reader);
    }

    public async ValueTask<Recording?> GetByIsrcAsync(string isrc, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT recording_id, name, normalized_name, title, mix_name, isrc, source_payload_json, created_utc, updated_utc
            FROM recording
            WHERE isrc = @isrc
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@isrc", isrc);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadRecording(reader);
    }

    public async ValueTask<Recording?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT r.recording_id, r.name, r.normalized_name, r.title, r.mix_name, r.isrc, r.source_payload_json, r.created_utc, r.updated_utc
            FROM recording r
            JOIN recording_external_ref ref ON ref.recording_id = r.recording_id
            WHERE ref.source = @source AND ref.external_id = @externalId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@externalId", externalId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadRecording(reader);
    }

    public async ValueTask<Recording?> GetByNormalizedTitleAndMixNameAsync(string normalizedTitle, string? normalizedMixName, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;

        if (normalizedMixName is not null)
        {
            cmd.CommandText = """
                SELECT recording_id, name, normalized_name, title, mix_name, isrc, source_payload_json, created_utc, updated_utc
                FROM recording
                WHERE normalized_name = @normalizedTitle AND mix_name = @normalizedMixName
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@normalizedTitle", normalizedTitle);
            cmd.Parameters.AddWithValue("@normalizedMixName", normalizedMixName);
        }
        else
        {
            cmd.CommandText = """
                SELECT recording_id, name, normalized_name, title, mix_name, isrc, source_payload_json, created_utc, updated_utc
                FROM recording
                WHERE normalized_name = @normalizedTitle AND mix_name IS NULL
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@normalizedTitle", normalizedTitle);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadRecording(reader);
    }

    public async ValueTask UpsertAsync(Recording recording, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            INSERT INTO recording (recording_id, name, normalized_name, title, mix_name, isrc, source_payload_json, created_utc, updated_utc)
            VALUES (@id, @name, @normalizedName, @title, @mixName, @isrc, @sourcePayloadJson, @createdUtc, @updatedUtc)
            ON CONFLICT (recording_id) DO UPDATE SET
                name = excluded.name,
                normalized_name = excluded.normalized_name,
                title = excluded.title,
                mix_name = excluded.mix_name,
                isrc = excluded.isrc,
                source_payload_json = excluded.source_payload_json,
                updated_utc = excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("@id", recording.Id);
        cmd.Parameters.AddWithValue("@name", recording.Name ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@normalizedName", recording.NormalizedName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@title", recording.Title ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@mixName", recording.MixName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@isrc", recording.Isrc ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourcePayloadJson", recording.SourcePayloadJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdUtc", recording.CreatedUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedUtc", recording.UpdatedUtc?.ToString("O") ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var externalRef in recording.ExternalReferences)
        {
            using var refCmd = _connection.CreateCommand();
            refCmd.Transaction = _transaction;
            refCmd.CommandText = """
                INSERT INTO recording_external_ref (recording_id, source, external_id, is_primary, last_seen_utc, payload_json)
                VALUES (@recordingId, @source, @externalId, @isPrimary, @lastSeenUtc, @payloadJson)
                ON CONFLICT (recording_id, source, external_id) DO UPDATE SET
                    is_primary = excluded.is_primary,
                    last_seen_utc = excluded.last_seen_utc,
                    payload_json = excluded.payload_json
                """;
            refCmd.Parameters.AddWithValue("@recordingId", recording.Id);
            refCmd.Parameters.AddWithValue("@source", externalRef.Source);
            refCmd.Parameters.AddWithValue("@externalId", externalRef.ExternalId);
            refCmd.Parameters.AddWithValue("@isPrimary", externalRef.IsPrimary ? 1 : 0);
            refCmd.Parameters.AddWithValue("@lastSeenUtc", externalRef.LastSeenUtc?.ToString("O") ?? (object)DBNull.Value);
            refCmd.Parameters.AddWithValue("@payloadJson", externalRef.PayloadJson ?? (object)DBNull.Value);
            await refCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var credit in recording.ArtistCredits)
        {
            using var creditCmd = _connection.CreateCommand();
            creditCmd.Transaction = _transaction;
            creditCmd.CommandText = """
                INSERT INTO recording_artist_credit (recording_id, artist_id, credit_name, role, position)
                VALUES (@recordingId, @artistId, @creditName, @role, @position)
                ON CONFLICT (recording_id, artist_id) DO UPDATE SET
                    credit_name = excluded.credit_name,
                    role = excluded.role,
                    position = excluded.position
                """;
            creditCmd.Parameters.AddWithValue("@recordingId", recording.Id);
            creditCmd.Parameters.AddWithValue("@artistId", credit.ArtistId);
            creditCmd.Parameters.AddWithValue("@creditName", credit.CreditName ?? (object)DBNull.Value);
            creditCmd.Parameters.AddWithValue("@role", credit.Role ?? (object)DBNull.Value);
            creditCmd.Parameters.AddWithValue("@position", credit.Position ?? (object)DBNull.Value);
            await creditCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static Recording ReadRecording(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.IsDBNull(1) ? null : reader.GetString(1),
        NormalizedName = reader.IsDBNull(2) ? null : reader.GetString(2),
        Title = reader.IsDBNull(3) ? null : reader.GetString(3),
        MixName = reader.IsDBNull(4) ? null : reader.GetString(4),
        Isrc = reader.IsDBNull(5) ? null : reader.GetString(5),
        SourcePayloadJson = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedUtc = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        UpdatedUtc = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
    };
}

internal sealed class SqliteMediaFileRepository : IMediaFileRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public SqliteMediaFileRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async ValueTask<MediaFile?> GetByIdAsync(string mediaFileId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT media_file_id, path, normalized_path, content_hash, metadata_json, fingerprint, source_payload_json, created_utc, updated_utc
            FROM media_file
            WHERE media_file_id = @mediaFileId
            """;
        cmd.Parameters.AddWithValue("@mediaFileId", mediaFileId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadMediaFile(reader);
    }

    public async ValueTask<MediaFile?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT media_file_id, path, normalized_path, content_hash, metadata_json, fingerprint, source_payload_json, created_utc, updated_utc
            FROM media_file
            WHERE path = @path
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@path", path);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadMediaFile(reader);
    }

    public async ValueTask<MediaFile?> GetByContentHashAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT media_file_id, path, normalized_path, content_hash, metadata_json, fingerprint, source_payload_json, created_utc, updated_utc
            FROM media_file
            WHERE content_hash = @contentHash
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@contentHash", contentHash);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadMediaFile(reader);
    }

    public async ValueTask UpsertAsync(MediaFile mediaFile, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            INSERT INTO media_file (media_file_id, path, normalized_path, content_hash, metadata_json, fingerprint, source_payload_json, created_utc, updated_utc)
            VALUES (@id, @path, @normalizedPath, @contentHash, @metadataJson, @fingerprint, @sourcePayloadJson, @createdUtc, @updatedUtc)
            ON CONFLICT (media_file_id) DO UPDATE SET
                path = excluded.path,
                normalized_path = excluded.normalized_path,
                content_hash = excluded.content_hash,
                metadata_json = excluded.metadata_json,
                fingerprint = excluded.fingerprint,
                source_payload_json = excluded.source_payload_json,
                updated_utc = excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("@id", mediaFile.Id);
        cmd.Parameters.AddWithValue("@path", mediaFile.Path);
        cmd.Parameters.AddWithValue("@normalizedPath", mediaFile.NormalizedPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@contentHash", mediaFile.ContentHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@metadataJson", mediaFile.MetadataJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@fingerprint", mediaFile.Fingerprint ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourcePayloadJson", mediaFile.SourcePayloadJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdUtc", mediaFile.CreatedUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedUtc", mediaFile.UpdatedUtc?.ToString("O") ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MediaFile ReadMediaFile(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Path = reader.GetString(1),
        NormalizedPath = reader.IsDBNull(2) ? null : reader.GetString(2),
        ContentHash = reader.IsDBNull(3) ? null : reader.GetString(3),
        MetadataJson = reader.IsDBNull(4) ? null : reader.GetString(4),
        Fingerprint = reader.IsDBNull(5) ? null : reader.GetString(5),
        SourcePayloadJson = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedUtc = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        UpdatedUtc = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
    };
}

internal sealed class SqliteMatchRepository : IMatchRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public SqliteMatchRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async ValueTask<MatchRecord?> GetBestMatchByMediaFileIdAsync(string mediaFileId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT media_file_id, recording_id, override_state, score, confidence, evidence_json, created_utc, updated_utc
            FROM media_file_recording_match
            WHERE media_file_id = @mediaFileId
            """;
        cmd.Parameters.AddWithValue("@mediaFileId", mediaFileId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new MatchRecord
        {
            MediaFileId = reader.GetString(0),
            RecordingId = reader.GetString(1),
            OverrideState = (MatchOverrideState)reader.GetInt32(2),
            Score = reader.GetDecimal(3),
            Confidence = reader.GetDecimal(4),
            EvidenceJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
            UpdatedUtc = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        };
    }

    public async ValueTask<IReadOnlyList<MatchCandidate>> GetCandidatesByMediaFileIdAsync(string mediaFileId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT recording_id, rank, score, confidence, evidence_json
            FROM media_file_recording_candidate
            WHERE media_file_id = @mediaFileId
            ORDER BY rank
            """;
        cmd.Parameters.AddWithValue("@mediaFileId", mediaFileId);

        var results = new List<MatchCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new MatchCandidate
            {
                RecordingId = reader.GetString(0),
                Rank = reader.GetInt32(1),
                Score = reader.GetDecimal(2),
                Confidence = reader.GetDecimal(3),
                EvidenceJson = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return results;
    }

    public async ValueTask UpsertBestMatchAsync(MatchRecord matchRecord, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            INSERT INTO media_file_recording_match (media_file_id, recording_id, override_state, score, confidence, evidence_json, created_utc, updated_utc)
            VALUES (@mediaFileId, @recordingId, @overrideState, @score, @confidence, @evidenceJson, @createdUtc, @updatedUtc)
            ON CONFLICT (media_file_id) DO UPDATE SET
                recording_id = excluded.recording_id,
                override_state = excluded.override_state,
                score = excluded.score,
                confidence = excluded.confidence,
                evidence_json = excluded.evidence_json,
                updated_utc = excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("@mediaFileId", matchRecord.MediaFileId);
        cmd.Parameters.AddWithValue("@recordingId", matchRecord.RecordingId);
        cmd.Parameters.AddWithValue("@overrideState", (int)matchRecord.OverrideState);
        cmd.Parameters.AddWithValue("@score", matchRecord.Score);
        cmd.Parameters.AddWithValue("@confidence", matchRecord.Confidence);
        cmd.Parameters.AddWithValue("@evidenceJson", matchRecord.EvidenceJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdUtc", matchRecord.CreatedUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedUtc", matchRecord.UpdatedUtc?.ToString("O") ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpsertCandidatesAsync(string mediaFileId, IReadOnlyList<MatchCandidate> candidates, CancellationToken cancellationToken = default)
    {
        using var deleteCmd = _connection.CreateCommand();
        deleteCmd.Transaction = _transaction;
        deleteCmd.CommandText = "DELETE FROM media_file_recording_candidate WHERE media_file_id = @mediaFileId";
        deleteCmd.Parameters.AddWithValue("@mediaFileId", mediaFileId);
        await deleteCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            using var insertCmd = _connection.CreateCommand();
            insertCmd.Transaction = _transaction;
            insertCmd.CommandText = """
                INSERT INTO media_file_recording_candidate (media_file_id, recording_id, rank, score, confidence, evidence_json)
                VALUES (@mediaFileId, @recordingId, @rank, @score, @confidence, @evidenceJson)
                """;
            insertCmd.Parameters.AddWithValue("@mediaFileId", mediaFileId);
            insertCmd.Parameters.AddWithValue("@recordingId", candidate.RecordingId);
            insertCmd.Parameters.AddWithValue("@rank", candidate.Rank);
            insertCmd.Parameters.AddWithValue("@score", candidate.Score);
            insertCmd.Parameters.AddWithValue("@confidence", candidate.Confidence);
            insertCmd.Parameters.AddWithValue("@evidenceJson", candidate.EvidenceJson ?? (object)DBNull.Value);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask SetUserOverrideAsync(string mediaFileId, string recordingId, MatchOverrideState overrideState, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            UPDATE media_file_recording_match
            SET recording_id = @recordingId, override_state = @overrideState, updated_utc = @updatedUtc
            WHERE media_file_id = @mediaFileId
            """;
        cmd.Parameters.AddWithValue("@mediaFileId", mediaFileId);
        cmd.Parameters.AddWithValue("@recordingId", recordingId);
        cmd.Parameters.AddWithValue("@overrideState", (int)overrideState);
        cmd.Parameters.AddWithValue("@updatedUtc", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class SqliteEmbeddingRepository : IEmbeddingRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public SqliteEmbeddingRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async ValueTask<EmbeddingDocument?> GetByEntityIdAsync(string entityId, string modelName, string modelVersion, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT entity_id, entity_type, model_name, model_version, dimensions, document_hash, document_text, vector_data, source_payload_json, created_utc, updated_utc
            FROM embedding_document
            WHERE entity_id = @entityId AND model_name = @modelName AND model_version = @modelVersion
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@entityId", entityId);
        cmd.Parameters.AddWithValue("@modelName", modelName);
        cmd.Parameters.AddWithValue("@modelVersion", modelVersion);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new EmbeddingDocument
        {
            EntityId = reader.GetString(0),
            EntityType = reader.GetString(1),
            ModelName = reader.GetString(2),
            ModelVersion = reader.GetString(3),
            Dimensions = reader.GetInt32(4),
            DocumentHash = reader.GetString(5),
            DocumentText = reader.IsDBNull(6) ? null : reader.GetString(6),
            VectorData = reader.IsDBNull(7) ? null : (byte[])reader[7],
            SourcePayloadJson = reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedUtc = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            UpdatedUtc = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
        };
    }

    public async ValueTask UpsertAsync(EmbeddingDocument embeddingDocument, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            INSERT INTO embedding_document (entity_id, entity_type, model_name, model_version, dimensions, document_hash, document_text, vector_data, source_payload_json, created_utc, updated_utc)
            VALUES (@entityId, @entityType, @modelName, @modelVersion, @dimensions, @documentHash, @documentText, @vectorData, @sourcePayloadJson, @createdUtc, @updatedUtc)
            ON CONFLICT (entity_id, model_name, model_version) DO UPDATE SET
                dimensions = excluded.dimensions,
                document_hash = excluded.document_hash,
                document_text = excluded.document_text,
                vector_data = excluded.vector_data,
                source_payload_json = excluded.source_payload_json,
                updated_utc = excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("@entityId", embeddingDocument.EntityId);
        cmd.Parameters.AddWithValue("@entityType", embeddingDocument.EntityType);
        cmd.Parameters.AddWithValue("@modelName", embeddingDocument.ModelName);
        cmd.Parameters.AddWithValue("@modelVersion", embeddingDocument.ModelVersion);
        cmd.Parameters.AddWithValue("@dimensions", embeddingDocument.Dimensions);
        cmd.Parameters.AddWithValue("@documentHash", embeddingDocument.DocumentHash);
        cmd.Parameters.AddWithValue("@documentText", embeddingDocument.DocumentText ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@vectorData", embeddingDocument.VectorData ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourcePayloadJson", embeddingDocument.SourcePayloadJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdUtc", embeddingDocument.CreatedUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedUtc", embeddingDocument.UpdatedUtc?.ToString("O") ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteByDocumentHashAsync(string documentHash, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "DELETE FROM embedding_document WHERE document_hash = @documentHash";
        cmd.Parameters.AddWithValue("@documentHash", documentHash);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
