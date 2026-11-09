using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed partial class SqliteLabelRepository : ILabelRepository
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
            SELECT label_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc, version
            FROM label
            WHERE label_id = @labelId
            """;
        cmd.Parameters.AddWithValue("@labelId", labelId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var label = ReadLabel(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        var hydrated = await PopulateLabelRelatedDataAsync(label, cancellationToken).ConfigureAwait(false);
        return hydrated with { Profile = await ReadProfileAsync(labelId, cancellationToken).ConfigureAwait(false) };
    }

    public async ValueTask<Label?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT l.label_id, l.name, l.normalized_name, l.sort_name, l.source_payload_json, l.created_utc, l.updated_utc, l.version
            FROM label l
            JOIN label_external_ref r ON r.label_id = l.label_id
            WHERE r.source = @source AND r.external_id = @externalId
            LIMIT 2
            """;
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@externalId", externalId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var label = ReadLabel(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"Ambiguous label provider reference '{source}/{externalId}'. Review the matching labels.");
        return label;
    }

    public async ValueTask<Label?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT label_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc, version
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
        await WriteLabelAsync(label, createOnly: false, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> TryCreateAsync(Label label, CancellationToken cancellationToken = default)
        => WriteLabelAsync(label, createOnly: true, cancellationToken);

    private async ValueTask<bool> WriteLabelAsync(Label label, bool createOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(label.Id);
        await RejectRetiredAsync(label.Id, cancellationToken).ConfigureAwait(false);
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        var conflictAction = createOnly ? "ON CONFLICT (label_id) DO NOTHING" : """
            ON CONFLICT (label_id) DO UPDATE SET
                name = excluded.name,
                normalized_name = excluded.normalized_name,
                sort_name = excluded.sort_name,
                source_payload_json = excluded.source_payload_json,
                updated_utc = excluded.updated_utc
            """;
        cmd.CommandText = $"""
            INSERT INTO label (label_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc)
            VALUES (@id, @name, @normalizedName, @sortName, @sourcePayloadJson, @createdUtc, @updatedUtc)
            {conflictAction}
            """;
        cmd.Parameters.AddWithValue("@id", label.Id);
        cmd.Parameters.AddWithValue("@name", label.Name ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@normalizedName", label.NormalizedName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sortName", label.SortName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourcePayloadJson", label.SourcePayloadJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdUtc", label.CreatedUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedUtc", label.UpdatedUtc?.ToString("O") ?? (object)DBNull.Value);

        if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            return false;

        foreach (var externalRef in label.ExternalReferences)
            await UpsertExternalRefAsync(label.Id, externalRef, cancellationToken).ConfigureAwait(false);

        foreach (var alias in label.Aliases)
            await UpsertAliasAsync(label.Id, alias, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<LabelIdentity> ResolveIdentityAsync(string labelId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            SELECT label_id FROM label WHERE label_id = @id
            UNION ALL SELECT canonical_id FROM retired_label WHERE retired_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", labelId);
        return new(labelId, (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask RejectRetiredAsync(string labelId, CancellationToken cancellationToken)
    {
        var identity = await ResolveIdentityAsync(labelId, cancellationToken).ConfigureAwait(false);
        if (identity.IsRetired) throw new RetiredLabelException(labelId, identity.CanonicalId!);
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

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM label";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (int)(long)(result ?? 0L);
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
        Version = reader.GetString(7),
    };

    private async ValueTask<Label> PopulateLabelRelatedDataAsync(Label label, CancellationToken cancellationToken)
    {
        var aliases = new List<EntityAlias>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT value, normalized_value, is_primary FROM label_alias WHERE label_id = @id ORDER BY value";
            cmd.Parameters.AddWithValue("@id", label.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                aliases.Add(new EntityAlias
                {
                    Value = reader.GetString(0),
                    NormalizedValue = reader.IsDBNull(1) ? null : reader.GetString(1),
                    IsPrimary = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
                });
            }
        }

        var refs = new List<EntityReference>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT source, external_id, is_primary, last_seen_utc, payload_json FROM label_external_ref WHERE label_id = @id ORDER BY source, external_id";
            cmd.Parameters.AddWithValue("@id", label.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                refs.Add(new EntityReference
                {
                    Source = reader.GetString(0),
                    ExternalId = reader.GetString(1),
                    IsPrimary = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
                    LastSeenUtc = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
                    PayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        return label with
        {
            Aliases = aliases,
            ExternalReferences = refs,
        };
    }
}

internal sealed partial class SqliteArtistRepository : IArtistRepository
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

        var artist = ReadArtist(reader);
        return await PopulateArtistRelatedDataAsync(artist, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Artist?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default)
    {
        if ((await new SqliteCatalogFoundationRepository(_connection, _transaction)
            .FindReferenceOwnersAsync(CatalogEntityKind.Artist, source, externalId, cancellationToken).ConfigureAwait(false)).Count > 1)
            throw new CatalogFoundationConflictException("Multiple Artists own this provider reference; review is required.");
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

        return await GetByIdAsync(reader.GetString(0), cancellationToken).ConfigureAwait(false);
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

        return await GetByIdAsync(reader.GetString(0), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpsertAsync(Artist artist, CancellationToken cancellationToken = default)
        => await SqliteCatalogFoundationRepository.AtomicAsync(_connection, _transaction,
            () => UpsertCoreAsync(artist, cancellationToken), cancellationToken).ConfigureAwait(false);

    private async ValueTask UpsertCoreAsync(Artist artist, CancellationToken cancellationToken)
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

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM artist";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (int)(long)(result ?? 0L);
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

    private async ValueTask<Artist> PopulateArtistRelatedDataAsync(Artist artist, CancellationToken cancellationToken)
    {
        var aliases = new List<EntityAlias>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT value, normalized_value, is_primary FROM artist_alias WHERE artist_id = @id";
            cmd.Parameters.AddWithValue("@id", artist.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                aliases.Add(new EntityAlias
                {
                    Value = reader.GetString(0),
                    NormalizedValue = reader.IsDBNull(1) ? null : reader.GetString(1),
                    IsPrimary = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
                });
            }
        }

        var refs = new List<EntityReference>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT source, external_id, is_primary, last_seen_utc, payload_json FROM artist_external_ref WHERE artist_id = @id";
            cmd.Parameters.AddWithValue("@id", artist.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                refs.Add(new EntityReference
                {
                    Source = reader.GetString(0),
                    ExternalId = reader.GetString(1),
                    IsPrimary = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
                    LastSeenUtc = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
                    PayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        var metadata = (await new SqliteCatalogFoundationRepository(_connection, _transaction)
            .GetMetadataAsync(CatalogEntityKind.Artist, artist.Id, cancellationToken).ConfigureAwait(false))!;
        return artist with
        {
            Version = metadata.Version,
            Kind = metadata.ArtistKind,
            IdentityStatus = metadata.IdentityStatus,
            Aliases = aliases,
            ExternalReferences = refs,
        };
    }
}

internal sealed class SqliteReleaseRepository : IReleaseRepository
{
    public async ValueTask<CatalogReleasePage> QueryAsync(ReleaseQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate();
        const string predicate = """
            (@q='' OR instr(lower(coalesce(r.title,r.name,'')),lower(@q))>0
              OR instr(lower(coalesce(r.catalog_number,'')),lower(@q))>0)
            AND (@label IS NULL OR EXISTS(SELECT 1 FROM release_label_link ll WHERE ll.release_id=r.release_id AND ll.label_id=@label))
            """;
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.Parameters.AddWithValue("@q", query.Search ?? "");
        command.Parameters.AddWithValue("@label", query.LabelId ?? (object)DBNull.Value);
        command.CommandText = $"SELECT count(*) FROM release r WHERE {predicate}";
        var total = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        command.CommandText = $"""
            SELECT r.release_id,coalesce(r.title,r.name),r.catalog_number,r.publish_date,
              (SELECT ll.label_id FROM release_label_link ll WHERE ll.release_id=r.release_id ORDER BY ll.is_primary DESC,ll.label_id LIMIT 1),
              (SELECT l.name FROM release_label_link ll JOIN label l ON l.label_id=ll.label_id WHERE ll.release_id=r.release_id ORDER BY ll.is_primary DESC,ll.label_id LIMIT 1),
              (SELECT count(*) FROM release_recording rr WHERE rr.release_id=r.release_id)
            FROM release r WHERE {predicate}
            ORDER BY coalesce(r.title,r.name,'') COLLATE NOCASE,r.release_id LIMIT @size OFFSET @offset
            """;
        command.Parameters.AddWithValue("@size", query.PageSize);
        command.Parameters.AddWithValue("@offset", ((long)query.Page - 1) * query.PageSize);
        var items = new List<CatalogReleaseSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(new(reader.GetString(0), Text(1), Text(2), Text(3), Text(4), Text(5), reader.GetInt32(6)));
        return new(items, total, query.Page, query.PageSize);
    }
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

        var release = ReadRelease(reader);
        return await PopulateReleaseRelatedDataAsync(release, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Release?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default)
    {
        if ((await new SqliteCatalogFoundationRepository(_connection, _transaction)
            .FindReferenceOwnersAsync(CatalogEntityKind.Release, source, externalId, cancellationToken).ConfigureAwait(false)).Count > 1)
            throw new CatalogFoundationConflictException("Multiple Releases own this provider reference; review is required.");
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

        return await GetByIdAsync(reader.GetString(0), cancellationToken).ConfigureAwait(false);
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

        return await GetByIdAsync(reader.GetString(0), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpsertAsync(Release release, CancellationToken cancellationToken = default)
        => await SqliteCatalogFoundationRepository.AtomicAsync(_connection, _transaction,
            () => UpsertCoreAsync(release, cancellationToken), cancellationToken).ConfigureAwait(false);

    private async ValueTask UpsertCoreAsync(Release release, CancellationToken cancellationToken)
    {
        var labels = new SqliteLabelRepository(_connection, _transaction);
        foreach (var link in release.LabelLinks)
        {
            var identity = await labels.ResolveIdentityAsync(link.LabelId, cancellationToken).ConfigureAwait(false);
            if (identity.IsRetired) throw new RetiredLabelException(link.LabelId, identity.CanonicalId!);
        }
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
            if (credit.Detail is { } detail)
            {
                if (detail.ArtistId != credit.ArtistId || detail.CreditedName != credit.CreditName || detail.Position != credit.Position)
                    throw new CatalogFoundationValidationException("Credit detail disagrees with its legacy projection.");
                await new SqliteCatalogFoundationRepository(_connection, _transaction)
                    .WriteCredit("release_artist_credit", "release_id", release.Id, detail, cancellationToken).ConfigureAwait(false);
                continue;
            }
            await new SqliteCatalogFoundationRepository(_connection, _transaction)
                .RequireLegacyCompatible("release_artist_credit", "release_id", release.Id, "artist_id", credit.ArtistId, cancellationToken).ConfigureAwait(false);
            using var creditCmd = _connection.CreateCommand();
            creditCmd.Transaction = _transaction;
            creditCmd.CommandText = """
                INSERT INTO release_artist_credit (release_id, artist_id, credit_name, position)
                VALUES (@releaseId, @artistId, @creditName, @position)
                ON CONFLICT (release_id, artist_id) WHERE legacy=1 DO UPDATE SET
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

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM release";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (int)(long)(result ?? 0L);
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

    private async ValueTask<Release> PopulateReleaseRelatedDataAsync(Release release, CancellationToken cancellationToken)
    {
        var refs = new List<EntityReference>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT source, external_id, is_primary, last_seen_utc, payload_json FROM release_external_ref WHERE release_id = @id";
            cmd.Parameters.AddWithValue("@id", release.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                refs.Add(new EntityReference
                {
                    Source = reader.GetString(0),
                    ExternalId = reader.GetString(1),
                    IsPrimary = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
                    LastSeenUtc = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
                    PayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        var credits = new List<ReleaseArtistCredit>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT artist_id, credit_name, position,credit_id,role,source,source_path,source_expression,evidence_json,legacy FROM release_artist_credit WHERE release_id = @id ORDER BY COALESCE(position, 999999),credit_id";
            cmd.Parameters.AddWithValue("@id", release.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                credits.Add(new ReleaseArtistCredit
                {
                    ArtistId = reader.GetString(0),
                    CreditName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Position = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    Detail = reader.GetInt32(9) == 1 ? null : new CatalogCredit(reader.GetString(3),reader.GetString(0),
                        reader.IsDBNull(4) ? "unknown" : reader.GetString(4),reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        reader.IsDBNull(1) ? null : reader.GetString(1),reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),reader.IsDBNull(7) ? null : reader.GetString(7),reader.IsDBNull(8) ? null : reader.GetString(8)),
                });
            }
        }

        var labels = new List<ReleaseLabelLink>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT label_id, is_primary, role FROM release_label_link WHERE release_id = @id";
            cmd.Parameters.AddWithValue("@id", release.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                labels.Add(new ReleaseLabelLink
                {
                    LabelId = reader.GetString(0),
                    IsPrimary = !reader.IsDBNull(1) && reader.GetInt32(1) == 1,
                    Role = reader.IsDBNull(2) ? null : reader.GetString(2),
                });
            }
        }

        var metadata = (await new SqliteCatalogFoundationRepository(_connection, _transaction)
            .GetMetadataAsync(CatalogEntityKind.Release, release.Id, cancellationToken).ConfigureAwait(false))!;
        return release with
        {
            Version = metadata.Version, CatalogNumber = metadata.CatalogNumber, Upc = metadata.Upc,
            PublishDate = metadata.PublishDate, NewReleaseDate = metadata.NewReleaseDate,
            ExternalReferences = refs,
            ArtistCredits = credits,
            LabelLinks = labels,
        };
    }
}

internal sealed partial class SqliteRecordingRepository : IRecordingRepository
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

        var recording = ReadRecording(reader);
        return await PopulateRecordingRelatedDataAsync(recording, cancellationToken).ConfigureAwait(false);
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

        return await GetByIdAsync(reader.GetString(0), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Recording?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default)
    {
        if ((await new SqliteCatalogFoundationRepository(_connection, _transaction)
            .FindReferenceOwnersAsync(CatalogEntityKind.Recording, source, externalId, cancellationToken).ConfigureAwait(false)).Count > 1)
            throw new CatalogFoundationConflictException("Multiple Recordings own this provider reference; review is required.");
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

        return await GetByIdAsync(reader.GetString(0), cancellationToken).ConfigureAwait(false);
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

        return await GetByIdAsync(reader.GetString(0), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpsertAsync(Recording recording, CancellationToken cancellationToken = default)
        => await SqliteCatalogFoundationRepository.AtomicAsync(_connection, _transaction,
            () => UpsertCoreAsync(recording, cancellationToken), cancellationToken).ConfigureAwait(false);

    private async ValueTask UpsertCoreAsync(Recording recording, CancellationToken cancellationToken)
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
            if (credit.Detail is { } detail)
            {
                if (detail.ArtistId != credit.ArtistId || detail.CreditedName != credit.CreditName || detail.Position != credit.Position || detail.Role != credit.Role)
                    throw new CatalogFoundationValidationException("Credit detail disagrees with its legacy projection.");
                await new SqliteCatalogFoundationRepository(_connection, _transaction)
                    .WriteCredit("recording_artist_credit", "recording_id", recording.Id, detail, cancellationToken).ConfigureAwait(false);
                continue;
            }
            await new SqliteCatalogFoundationRepository(_connection, _transaction)
                .RequireLegacyCompatible("recording_artist_credit", "recording_id", recording.Id, "artist_id", credit.ArtistId, cancellationToken).ConfigureAwait(false);
            using var creditCmd = _connection.CreateCommand();
            creditCmd.Transaction = _transaction;
            creditCmd.CommandText = """
                INSERT INTO recording_artist_credit (recording_id, artist_id, credit_name, role, position)
                VALUES (@recordingId, @artistId, @creditName, @role, @position)
                ON CONFLICT (recording_id, artist_id) WHERE legacy=1 DO UPDATE SET
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

        foreach (var link in recording.ReleaseLinks)
        {
            if (link.Placement is { } placement)
            {
                if (placement.ReleaseId != link.ReleaseId || placement.RecordingId != recording.Id ||
                    placement.DiscNumber != link.DiscNumber || placement.TrackNumber != link.TrackNumber)
                    throw new CatalogFoundationValidationException("Placement disagrees with its legacy projection.");
                var foundations = new SqliteCatalogFoundationRepository(_connection, _transaction);
                var release = (await foundations.GetMetadataAsync(CatalogEntityKind.Release, placement.ReleaseId, cancellationToken).ConfigureAwait(false))!;
                await foundations.SavePlacementAsync(placement, release.Version, cancellationToken).ConfigureAwait(false);
                continue;
            }
            await new SqliteCatalogFoundationRepository(_connection, _transaction)
                .RequireLegacyCompatible("release_recording", "release_id", link.ReleaseId, "recording_id", recording.Id, cancellationToken).ConfigureAwait(false);
            using var linkCmd = _connection.CreateCommand();
            linkCmd.Transaction = _transaction;
            linkCmd.CommandText = """
                INSERT INTO release_recording (release_id, recording_id, disc_number, track_number,display_order)
                VALUES (@releaseId, @recordingId, @discNumber, @trackNumber,
                    (SELECT coalesce(max(display_order),0)+1 FROM release_recording WHERE release_id=@releaseId))
                ON CONFLICT (release_id, recording_id) WHERE legacy=1 DO UPDATE SET
                    disc_number = excluded.disc_number,
                    track_number = excluded.track_number
                """;
            linkCmd.Parameters.AddWithValue("@releaseId", link.ReleaseId);
            linkCmd.Parameters.AddWithValue("@recordingId", recording.Id);
            linkCmd.Parameters.AddWithValue("@discNumber", link.DiscNumber ?? (object)DBNull.Value);
            linkCmd.Parameters.AddWithValue("@trackNumber", link.TrackNumber ?? (object)DBNull.Value);
            await linkCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var relationship in recording.Relationships)
        {
            using var relCmd = _connection.CreateCommand();
            relCmd.Transaction = _transaction;
            relCmd.CommandText = """
                INSERT INTO recording_relationship (from_recording_id, to_recording_id, relationship_type, source, confidence, notes, created_utc, updated_utc)
                VALUES (@fromRecordingId, @toRecordingId, @relationshipType, @source, @confidence, @notes, @createdUtc, @updatedUtc)
                ON CONFLICT (from_recording_id, to_recording_id, relationship_type) DO UPDATE SET
                    source = excluded.source,
                    confidence = excluded.confidence,
                    notes = excluded.notes,
                    updated_utc = excluded.updated_utc
                """;
            relCmd.Parameters.AddWithValue("@fromRecordingId", recording.Id);
            relCmd.Parameters.AddWithValue("@toRecordingId", relationship.RelatedRecordingId);
            relCmd.Parameters.AddWithValue("@relationshipType", relationship.RelationshipType);
            relCmd.Parameters.AddWithValue("@source", relationship.Source ?? (object)DBNull.Value);
            relCmd.Parameters.AddWithValue("@confidence", relationship.Confidence ?? (object)DBNull.Value);
            relCmd.Parameters.AddWithValue("@notes", relationship.Notes ?? (object)DBNull.Value);
            relCmd.Parameters.AddWithValue("@createdUtc", relationship.CreatedUtc?.ToString("O") ?? (object)DBNull.Value);
            relCmd.Parameters.AddWithValue("@updatedUtc", relationship.UpdatedUtc?.ToString("O") ?? (object)DBNull.Value);
            await relCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM recording";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (int)(long)(result ?? 0L);
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

    private async ValueTask<Recording> PopulateRecordingRelatedDataAsync(Recording recording, CancellationToken cancellationToken)
    {
        var refs = new List<EntityReference>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT source, external_id, is_primary, last_seen_utc, payload_json FROM recording_external_ref WHERE recording_id = @id";
            cmd.Parameters.AddWithValue("@id", recording.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                refs.Add(new EntityReference
                {
                    Source = reader.GetString(0),
                    ExternalId = reader.GetString(1),
                    IsPrimary = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
                    LastSeenUtc = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
                    PayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        var credits = new List<RecordingArtistCredit>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT artist_id, credit_name, role, position,credit_id,source,source_path,source_expression,evidence_json,legacy FROM recording_artist_credit WHERE recording_id = @id ORDER BY COALESCE(position, 999999),credit_id";
            cmd.Parameters.AddWithValue("@id", recording.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                credits.Add(new RecordingArtistCredit
                {
                    ArtistId = reader.GetString(0),
                    CreditName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Role = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Position = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    Detail = reader.GetInt32(9) == 1 ? null : new CatalogCredit(reader.GetString(4),reader.GetString(0),
                        reader.IsDBNull(2) ? "unknown" : reader.GetString(2),reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        reader.IsDBNull(1) ? null : reader.GetString(1),reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),reader.IsDBNull(7) ? null : reader.GetString(7),reader.IsDBNull(8) ? null : reader.GetString(8)),
                });
            }
        }

        var releaseLinks = new List<RecordingReleaseLink>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT release_id, disc_number, track_number,placement_id,legacy FROM release_recording WHERE recording_id = @id";
            cmd.Parameters.AddWithValue("@id", recording.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                releaseLinks.Add(new RecordingReleaseLink
                {
                    ReleaseId = reader.GetString(0),
                    DiscNumber = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    TrackNumber = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    Placement = reader.GetInt32(4) == 1 ? null : new ReleasePlacement(reader.GetString(3),reader.GetString(0),recording.Id,
                        "",1,null,"source",null,null,[],[]),
                });
            }
        }
        var foundations = new SqliteCatalogFoundationRepository(_connection, _transaction);
        foreach (var releaseId in releaseLinks.Where(l => l.Placement is not null).Select(l => l.ReleaseId).Distinct().ToArray())
        {
            var placements = await foundations.GetPlacementsAsync(releaseId, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < releaseLinks.Count; i++)
                if (releaseLinks[i].Placement is { } placement && placement.ReleaseId == releaseId)
                    releaseLinks[i] = releaseLinks[i] with { Placement = placements.Single(p => p.Id == placement.Id) };
        }

        var relationships = new List<RecordingRelationship>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = _transaction;
            cmd.CommandText = "SELECT to_recording_id, relationship_type, source, confidence, notes, created_utc, updated_utc FROM recording_relationship WHERE from_recording_id = @id";
            cmd.Parameters.AddWithValue("@id", recording.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                relationships.Add(new RecordingRelationship
                {
                    RelatedRecordingId = reader.GetString(0),
                    RelationshipType = reader.GetString(1),
                    Source = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Confidence = reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                    Notes = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedUtc = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                    UpdatedUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                });
            }
        }

        var metadata = (await foundations.GetMetadataAsync(CatalogEntityKind.Recording, recording.Id, cancellationToken).ConfigureAwait(false))!;
        return recording with
        {
            Version = metadata.Version, DurationMs = metadata.DurationMs,
            ExternalReferences = refs,
            ArtistCredits = credits,
            ReleaseLinks = releaseLinks,
            Relationships = relationships,
        };
    }
}

internal sealed class SqliteMediaFileRepository : IMediaFileRepository
{
    public async ValueTask<bool> HasConflictingRecordingMatchAsync(string contentHash, string recordingId, CancellationToken cancellationToken = default)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM media_file f JOIN media_file_recording_match m USING(media_file_id)
                WHERE f.content_hash=@hash COLLATE NOCASE AND m.recording_id<>@recordingId
                  AND m.override_state IN (1,3)
            )
            """;
        command.Parameters.AddWithValue("@hash", contentHash);
        command.Parameters.AddWithValue("@recordingId", recordingId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }
    public async ValueTask<IReadOnlyList<MediaFile>> ListByRecordingAsync(string recordingId, CancellationToken cancellationToken = default)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT f.media_file_id,f.path,f.normalized_path,f.content_hash,f.metadata_json,f.fingerprint,
                   f.source_payload_json,f.created_utc,f.updated_utc
            FROM media_file f JOIN media_file_recording_match m USING(media_file_id)
            WHERE m.recording_id=@id AND m.override_state IN (1,3)
            ORDER BY f.media_file_id LIMIT 101
            """;
        command.Parameters.AddWithValue("@id", recordingId);
        var files = new List<MediaFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) files.Add(ReadMediaFile(reader));
        return files;
    }
    public async ValueTask<int> CountByRecordingAsync(string recordingId, CancellationToken cancellationToken = default)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM media_file f JOIN media_file_recording_match m USING(media_file_id)
            WHERE m.recording_id=@id AND m.override_state IN (1,3)
            """;
        command.Parameters.AddWithValue("@id", recordingId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
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

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM media_file";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (int)(long)(result ?? 0L);
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
