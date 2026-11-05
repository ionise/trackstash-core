using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteCatalogFoundationRepository(SqliteConnection connection, SqliteTransaction transaction) : ICatalogFoundationRepository
{
    private static string Table(CatalogEntityKind kind) => kind switch
    {
        CatalogEntityKind.Artist => "artist", CatalogEntityKind.Release => "release",
        CatalogEntityKind.Recording => "recording", _ => throw new CatalogFoundationValidationException("Unknown entity kind."),
    };
    private SqliteCommand Command(string sql, params (string, object?)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static void Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new CatalogFoundationValidationException($"{field} is required.");
    }
    private static void StableId(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new CatalogFoundationValidationException("New credit/placement IDs must be GUIDs.");
    }
    private static void ValidateCredit(CatalogCredit credit)
    {
        StableId(credit.Id); Required(credit.ArtistId, "Artist"); Required(credit.Role, "Role");
        if (credit.Position is <= 0) throw new CatalogFoundationValidationException("Credit position must be positive or unknown.");
        if (credit.EvidenceJson is not null)
        {
            try { using var document = JsonDocument.Parse(credit.EvidenceJson); }
            catch (JsonException) { throw new CatalogFoundationValidationException("Credit evidence must be valid JSON."); }
        }
    }
    private static void Date(string? value)
    {
        if (value is not null && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new CatalogFoundationValidationException("Source release dates must be valid yyyy-MM-dd dates or unknown.");
    }
    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string, object?)[] args)
    {
        using var command = Command(sql, args);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async ValueTask AtomicAsync(SqliteConnection connection, SqliteTransaction transaction, Func<ValueTask> operation, CancellationToken ct)
    {
        var savepoint = "foundation_" + Guid.NewGuid().ToString("N");
        await transaction.SaveAsync(savepoint, ct).ConfigureAwait(false);
        try { await operation().ConfigureAwait(false); await transaction.ReleaseAsync(savepoint, ct).ConfigureAwait(false); }
        catch
        {
            await transaction.RollbackAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
            await transaction.ReleaseAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
    private ValueTask AtomicAsync(Func<ValueTask> operation, CancellationToken ct) => AtomicAsync(connection, transaction, operation, ct);

    public async ValueTask SaveEntityAsync(CanonicalEntity entity, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        var kind = entity switch
        {
            Artist => CatalogEntityKind.Artist, Release => CatalogEntityKind.Release, Recording => CatalogEntityKind.Recording,
            _ => throw new CatalogFoundationValidationException("Only Artist, Release and Recording are supported."),
        };
        var display = entity switch { Release r => r.Title ?? r.Name, Recording r => r.Title ?? r.Name, _ => entity.Name };
        Required(display, "Name/title");
        if (entity is Artist { Relationships.Count: > 0 } || entity is Release { Aliases.Count: > 0 } || entity is Recording { Aliases.Count: > 0 })
            throw new CatalogFoundationValidationException("This foundation does not persist Artist relationships or Release/Recording aliases.");
        await AtomicAsync(async () =>
        {
            var before = await GetMetadataAsync(kind, entity.Id, cancellationToken).ConfigureAwait(false);
            var beforeEntity = before is null ? null : await ReadEntity(kind, entity.Id, cancellationToken).ConfigureAwait(false);
            if (before is null) { StableId(entity.Id); if (expectedVersion is not null) throw new CatalogFoundationStaleException("Entity no longer exists."); }
            else
            {
                if (expectedVersion is null) throw new CatalogFoundationConflictException("Entity already exists.");
                await Expect(kind, entity.Id, expectedVersion, cancellationToken).ConfigureAwait(false);
            }
            switch (entity)
            {
                case Artist artist: await new SqliteArtistRepository(connection, transaction).UpsertAsync(artist, cancellationToken).ConfigureAwait(false); break;
                case Release release: await new SqliteReleaseRepository(connection, transaction).UpsertAsync(release, cancellationToken).ConfigureAwait(false); break;
                case Recording recording: await new SqliteRecordingRepository(connection, transaction).UpsertAsync(recording, cancellationToken).ConfigureAwait(false); break;
            }
            var current = (await GetMetadataAsync(kind, entity.Id, cancellationToken).ConfigureAwait(false))!;
            var proposed = entity switch
            {
                Artist a => current with { ArtistKind = a.Kind, IdentityStatus = a.IdentityStatus },
                Release r => current with { CatalogNumber = r.CatalogNumber, Upc = r.Upc, PublishDate = r.PublishDate, NewReleaseDate = r.NewReleaseDate },
                Recording r => current with { DurationMs = r.DurationMs },
                _ => current,
            };
            await UpdateMetadataAsync(proposed, cancellationToken).ConfigureAwait(false);
            await Audit(Table(kind), entity.Id, "save_entity", beforeEntity,
                await ReadEntity(kind, entity.Id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
    private async ValueTask<CanonicalEntity?> ReadEntity(CatalogEntityKind kind, string id, CancellationToken ct) => kind switch
    {
        CatalogEntityKind.Artist => await new SqliteArtistRepository(connection, transaction).GetByIdAsync(id, ct).ConfigureAwait(false),
        CatalogEntityKind.Release => await new SqliteReleaseRepository(connection, transaction).GetByIdAsync(id, ct).ConfigureAwait(false),
        CatalogEntityKind.Recording => await new SqliteRecordingRepository(connection, transaction).GetByIdAsync(id, ct).ConfigureAwait(false),
        _ => throw new CatalogFoundationValidationException("Unsupported entity kind."),
    };

    public async ValueTask<CatalogEntityMetadata?> GetMetadataAsync(CatalogEntityKind kind, string id, CancellationToken cancellationToken = default)
    {
        var table = Table(kind);
        var extra = kind switch
        {
            CatalogEntityKind.Artist => "artist_kind,identity_status",
            CatalogEntityKind.Release => "catalog_number,upc,publish_date,new_release_date",
            _ => "duration_ms",
        };
        using var command = Command($"SELECT version,{extra} FROM {table} WHERE {table}_id=@id", ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        var result = new CatalogEntityMetadata(id, kind, reader.GetString(0));
        return kind switch
        {
            CatalogEntityKind.Artist => result with { ArtistKind = reader.GetString(1), IdentityStatus = reader.GetString(2) },
            CatalogEntityKind.Release => result with { CatalogNumber = Text(1), Upc = Text(2), PublishDate = Text(3), NewReleaseDate = Text(4) },
            _ => result with { DurationMs = reader.IsDBNull(1) ? null : reader.GetInt64(1) },
        };
    }
    private async ValueTask<CatalogEntityMetadata> Expect(CatalogEntityKind kind, string id, string expected, CancellationToken ct)
    {
        Required(expected, "Expected version");
        var current = await GetMetadataAsync(kind, id, ct).ConfigureAwait(false);
        if (current is null) throw new CatalogFoundationConflictException("Catalog entity does not exist.");
        if (current.Version != expected) throw new CatalogFoundationStaleException("Catalog entity changed since review.");
        return current;
    }
    private async Task Audit(string kind, string id, string action, object? before, object? after, CancellationToken ct) =>
        await ExecuteAsync("""
            INSERT INTO catalog_foundation_revision(entity_type,entity_id,action,before_json,after_json,recorded_utc)
            VALUES(@kind,@id,@action,@before,@after,@utc)
            """, ct, ("@kind", kind), ("@id", id), ("@action", action),
            ("@before", before is null ? null : JsonSerializer.Serialize(before)),
            ("@after", after is null ? null : JsonSerializer.Serialize(after)), ("@utc", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);

    public async ValueTask<CatalogEntityMetadata> UpdateMetadataAsync(CatalogEntityMetadata metadata, CancellationToken cancellationToken = default)
    {
        var table = Table(metadata.Kind);
        if (metadata.ArtistKind is not ("unknown" or "person" or "group" or "project") ||
            metadata.IdentityStatus is not ("unknown" or "accepted" or "unresolved_compound") || metadata.DurationMs is < 0)
            throw new CatalogFoundationValidationException("Invalid identity state/kind or recording duration.");
        Date(metadata.PublishDate); Date(metadata.NewReleaseDate);
        if (metadata.Kind != CatalogEntityKind.Artist && (metadata.ArtistKind != "unknown" || metadata.IdentityStatus != "unknown") ||
            metadata.Kind != CatalogEntityKind.Release && (metadata.CatalogNumber is not null || metadata.Upc is not null || metadata.PublishDate is not null || metadata.NewReleaseDate is not null) ||
            metadata.Kind != CatalogEntityKind.Recording && metadata.DurationMs is not null)
            throw new CatalogFoundationValidationException("Metadata fields do not belong to this entity kind.");
        CatalogEntityMetadata result = metadata;
        await AtomicAsync(async () =>
        {
            var before = await Expect(metadata.Kind, metadata.EntityId, metadata.Version, cancellationToken).ConfigureAwait(false);
            if (before == metadata) { result = before; return; }
            var set = metadata.Kind switch
            {
                CatalogEntityKind.Artist => "artist_kind=@kind,identity_status=@status",
                CatalogEntityKind.Release => "catalog_number=@catalog,upc=@upc,publish_date=@publish,new_release_date=@new",
                _ => "duration_ms=@duration",
            };
            await ExecuteAsync($"UPDATE {table} SET {set} WHERE {table}_id=@id", cancellationToken,
                ("@id", metadata.EntityId), ("@kind", metadata.ArtistKind), ("@status", metadata.IdentityStatus),
                ("@catalog", metadata.CatalogNumber), ("@upc", metadata.Upc), ("@publish", metadata.PublishDate),
                ("@new", metadata.NewReleaseDate), ("@duration", metadata.DurationMs)).ConfigureAwait(false);
            result = (await GetMetadataAsync(metadata.Kind, metadata.EntityId, cancellationToken).ConfigureAwait(false))!;
            await Audit(table, metadata.EntityId, "metadata", before, result, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }
    public async ValueTask<IReadOnlyList<string>> FindReferenceOwnersAsync(CatalogEntityKind kind, string source, string externalId, CancellationToken cancellationToken = default)
    {
        Required(source, "Source"); Required(externalId, "External ID");
        var table = Table(kind);
        using var command = Command($"SELECT {table}_id FROM {table}_external_ref WHERE source=@source AND external_id=@external ORDER BY {table}_id",
            ("@source", source), ("@external", externalId));
        var owners = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) owners.Add(reader.GetString(0));
        return owners;
    }

    public ValueTask<IReadOnlyList<CatalogCredit>> GetCreditsAsync(CatalogEntityKind kind, string id, CancellationToken cancellationToken = default)
    {
        if (kind == CatalogEntityKind.Artist) throw new CatalogFoundationValidationException("Artists do not own contributor credits.");
        var table = Table(kind);
        return ReadCredits(table + "_artist_credit", table + "_id", id, cancellationToken);
    }
    private async ValueTask<IReadOnlyList<CatalogCredit>> ReadCredits(string table, string column, string id, CancellationToken ct)
    {
        using var command = Command($"SELECT credit_id,artist_id,role,position,credit_name,source,source_path,source_expression,evidence_json FROM {table} WHERE {column}=@id ORDER BY role,position,credit_id", ("@id", id));
        var items = new List<CatalogCredit>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        string? Text(int n) => reader.IsDBNull(n) ? null : reader.GetString(n);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            items.Add(new(reader.GetString(0), reader.GetString(1), Text(2) ?? "unknown", reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Text(4), Text(5), Text(6), Text(7), Text(8)));
        return items;
    }
    public async ValueTask SaveCreditAsync(CatalogEntityKind kind, string entityId, string expectedVersion, CatalogCredit credit, CancellationToken cancellationToken = default)
    {
        ValidateCredit(credit);
        if (kind == CatalogEntityKind.Artist) throw new CatalogFoundationValidationException("Artists do not own contributor credits.");
        var table = Table(kind);
        await AtomicAsync(async () =>
        {
            await Expect(kind, entityId, expectedVersion, cancellationToken).ConfigureAwait(false);
            var before = await GetCreditsAsync(kind, entityId, cancellationToken).ConfigureAwait(false);
            await WriteCredit(table + "_artist_credit", table + "_id", entityId, credit, cancellationToken).ConfigureAwait(false);
            await Audit(table, entityId, "credit", before, await GetCreditsAsync(kind, entityId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
    internal async Task WriteCredit(string table, string column, string id, CatalogCredit credit, CancellationToken ct)
    {
        ValidateCredit(credit);
        using (var check = Command($"SELECT {column} FROM {table} WHERE credit_id=@id", ("@id", credit.Id)))
        {
            var owner = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (owner is string other && other != id) throw new CatalogFoundationConflictException("Credit identity belongs to another aggregate.");
        }
        var legacyColumn = table == "placement_artist_credit" ? "" : ",legacy";
        var legacyValue = table == "placement_artist_credit" ? "" : ",0";
        var legacyUpdate = table == "placement_artist_credit" ? "" : ",legacy=0";
        await ExecuteAsync($"""
            INSERT INTO {table}(credit_id,{column},artist_id,role,position,credit_name,source,source_path,source_expression,evidence_json{legacyColumn})
            VALUES(@credit,@id,@artist,@role,@position,@name,@source,@path,@expression,@evidence{legacyValue})
            ON CONFLICT(credit_id) DO UPDATE SET artist_id=excluded.artist_id,role=excluded.role,position=excluded.position,
                credit_name=excluded.credit_name,source=excluded.source,source_path=excluded.source_path,
                source_expression=excluded.source_expression,evidence_json=excluded.evidence_json{legacyUpdate}
            """, ct, ("@credit", credit.Id), ("@id", id), ("@artist", credit.ArtistId), ("@role", credit.Role),
            ("@position", credit.Position), ("@name", credit.CreditedName), ("@source", credit.Source),
            ("@path", credit.SourcePath), ("@expression", credit.SourceExpression), ("@evidence", credit.EvidenceJson)).ConfigureAwait(false);
    }
    internal async Task RequireLegacyCompatible(string table, string ownerColumn, string owner, string targetColumn, string target, CancellationToken ct)
    {
        using var command = Command($"SELECT 1 FROM {table} WHERE {ownerColumn}=@owner AND {targetColumn}=@target AND legacy=0 LIMIT 1",
            ("@owner", owner), ("@target", target));
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            throw new CatalogFoundationConflictException("Legacy write cannot replace or duplicate stable reviewed credits/placements; retain their full detail or use the conditional workflow.");
    }

    public async ValueTask<IReadOnlyList<ReleasePlacement>> GetPlacementsAsync(string releaseId, CancellationToken cancellationToken = default)
    {
        var items = new List<ReleasePlacement>();
        using (var command = Command("SELECT placement_id,recording_id,version,display_order,source_position,order_source,disc_number,track_number FROM release_recording WHERE release_id=@id ORDER BY display_order,placement_id", ("@id", releaseId)))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            int? Number(int n) => reader.IsDBNull(n) ? null : reader.GetInt32(n);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                items.Add(new(reader.GetString(0), releaseId, reader.GetString(1), reader.GetString(2), reader.GetInt32(3), Number(4), reader.GetString(5), Number(6), Number(7), [], []));
        }
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var refs = new List<EntityReference>();
            using var command = Command("SELECT source,external_id,is_primary,last_seen_utc,payload_json FROM placement_external_ref WHERE placement_id=@id ORDER BY source,external_id", ("@id", item.Id));
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    refs.Add(new EntityReference { Source = reader.GetString(0), ExternalId = reader.GetString(1), IsPrimary = reader.GetBoolean(2),
                        LastSeenUtc = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                        PayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4) });
            }
            items[i] = item with { Credits = await ReadCredits("placement_artist_credit", "placement_id", item.Id, cancellationToken).ConfigureAwait(false), ExternalReferences = refs };
        }
        return items;
    }
    public async ValueTask SavePlacementAsync(ReleasePlacement placement, string expectedReleaseVersion, CancellationToken cancellationToken = default)
    {
        StableId(placement.Id);
        if (placement.DisplayOrder <= 0 || placement.SourcePosition is <= 0 || placement.DiscNumber is <= 0 || placement.TrackNumber is <= 0 ||
            placement.OrderSource is not ("source" or "manual" or "verified" or "legacy"))
            throw new CatalogFoundationValidationException("Invalid placement ordering/numbering.");
        foreach (var credit in placement.Credits) ValidateCredit(credit);
        foreach (var reference in placement.ExternalReferences) { Required(reference.Source, "Source"); Required(reference.ExternalId, "External ID"); }
        if (placement.Credits.Select(c => c.Id).Distinct().Count() != placement.Credits.Count ||
            placement.ExternalReferences.Select(r => (r.Source, r.ExternalId)).Distinct().Count() != placement.ExternalReferences.Count)
            throw new CatalogFoundationValidationException("Duplicate credit/reference IDs.");
        await AtomicAsync(async () =>
        {
            await Expect(CatalogEntityKind.Release, placement.ReleaseId, expectedReleaseVersion, cancellationToken).ConfigureAwait(false);
            var before = (await GetPlacementsAsync(placement.ReleaseId, cancellationToken).ConfigureAwait(false)).SingleOrDefault(p => p.Id == placement.Id);
            using (var order = Command("SELECT 1 FROM release_recording WHERE release_id=@release AND display_order=@order AND placement_id<>@id LIMIT 1",
                ("@release", placement.ReleaseId), ("@order", placement.DisplayOrder), ("@id", placement.Id)))
                if (await order.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new CatalogFoundationConflictException("Display position is occupied; use the complete reorder operation.");
            using (var check = Command("SELECT release_id,recording_id FROM release_recording WHERE placement_id=@id", ("@id", placement.Id)))
            {
                await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                    (reader.GetString(0) != placement.ReleaseId || reader.GetString(1) != placement.RecordingId))
                    throw new CatalogFoundationConflictException("Placement endpoints are immutable.");
            }
            if (before is null && placement.Version.Length != 0 || before is not null && before.Version != placement.Version)
                throw new CatalogFoundationStaleException("Placement changed since review.");
            await ExecuteAsync("""
                INSERT INTO release_recording(placement_id,release_id,recording_id,display_order,source_position,order_source,disc_number,track_number,version,legacy)
                VALUES(@id,@release,@recording,@order,@sourcePosition,@orderSource,@disc,@track,lower(hex(randomblob(16))),0)
                ON CONFLICT(placement_id) DO UPDATE SET display_order=excluded.display_order,source_position=excluded.source_position,
                    order_source=excluded.order_source,disc_number=excluded.disc_number,track_number=excluded.track_number,version=excluded.version
                """, cancellationToken, ("@id", placement.Id), ("@release", placement.ReleaseId), ("@recording", placement.RecordingId),
                ("@order", placement.DisplayOrder), ("@sourcePosition", placement.SourcePosition), ("@orderSource", placement.OrderSource),
                ("@disc", placement.DiscNumber), ("@track", placement.TrackNumber)).ConfigureAwait(false);
            foreach (var credit in placement.Credits)
                await WriteCredit("placement_artist_credit", "placement_id", placement.Id, credit, cancellationToken).ConfigureAwait(false);
            foreach (var reference in placement.ExternalReferences)
            {
                using (var check = Command("SELECT placement_id FROM placement_external_ref WHERE source=@source AND external_id=@external",
                    ("@source", reference.Source), ("@external", reference.ExternalId)))
                {
                    if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string owner && owner != placement.Id)
                        throw new CatalogFoundationConflictException("Provider track listing belongs to another placement.");
                }
                await ExecuteAsync("""
                    INSERT INTO placement_external_ref(placement_id,source,external_id,is_primary,last_seen_utc,payload_json)
                    VALUES(@id,@source,@external,@primary,@seen,@payload)
                    ON CONFLICT(source,external_id) DO UPDATE SET is_primary=excluded.is_primary,last_seen_utc=excluded.last_seen_utc,payload_json=excluded.payload_json
                    """, cancellationToken, ("@id", placement.Id), ("@source", reference.Source), ("@external", reference.ExternalId),
                    ("@primary", reference.IsPrimary ? 1 : 0), ("@seen", reference.LastSeenUtc?.ToString("O")), ("@payload", reference.PayloadJson)).ConfigureAwait(false);
            }
            var after = (await GetPlacementsAsync(placement.ReleaseId, cancellationToken).ConfigureAwait(false)).Single(p => p.Id == placement.Id);
            await Audit("placement", placement.Id, "save", before, after, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask ReorderPlacementsAsync(string releaseId, string expectedVersion, IReadOnlyList<string> orderedPlacementIds, CancellationToken cancellationToken = default)
    {
        await AtomicAsync(async () =>
        {
            await Expect(CatalogEntityKind.Release, releaseId, expectedVersion, cancellationToken).ConfigureAwait(false);
            var before = await GetPlacementsAsync(releaseId, cancellationToken).ConfigureAwait(false);
            if (orderedPlacementIds.Count != before.Count || orderedPlacementIds.Distinct().Count() != before.Count ||
                !orderedPlacementIds.ToHashSet(StringComparer.Ordinal).SetEquals(before.Select(p => p.Id)))
                throw new CatalogFoundationValidationException("Reorder must include every current placement exactly once.");
            if (before.Select(p => p.Id).SequenceEqual(orderedPlacementIds)) return;
            for (var i = 0; i < orderedPlacementIds.Count; i++)
                await ExecuteAsync("UPDATE release_recording SET display_order=@order,order_source='manual',version=lower(hex(randomblob(16))) WHERE placement_id=@id",
                    cancellationToken, ("@order", i + 1), ("@id", orderedPlacementIds[i])).ConfigureAwait(false);
            await Audit("release", releaseId, "reorder", before, await GetPlacementsAsync(releaseId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
