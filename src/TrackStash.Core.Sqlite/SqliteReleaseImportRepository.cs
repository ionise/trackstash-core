using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteReleaseImportRepository(SqliteConnection connection, SqliteTransaction transaction) : IReleaseImportRepository
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false };
    private SqliteCommand Command(string sql, params (string, object?)[] args)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private async Task Execute(string sql, CancellationToken ct, params (string, object?)[] args)
    {
        using var command = Command(sql, args); await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    public async ValueTask<string> GetCatalogStampAsync(CancellationToken cancellationToken = default)
    {
        using var command = Command("SELECT version FROM release_import_state WHERE singleton=1");
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
    public async ValueTask<IReadOnlyList<ReleaseImportCandidate>> FindCandidatesAsync(CatalogEntityKind kind, string normalizedName,
        string? isrc = null, CancellationToken cancellationToken = default)
    {
        var table = kind switch { CatalogEntityKind.Artist => "artist", CatalogEntityKind.Release => "release",
            CatalogEntityKind.Recording => "recording", _ => throw new CatalogFoundationValidationException("Invalid entity kind.") };
        var extra = kind == CatalogEntityKind.Recording ? ",mix_name,isrc" : ",NULL,NULL";
        var match = kind == CatalogEntityKind.Recording ? " OR (@isrc IS NOT NULL AND isrc=@isrc)" : "";
        using var command = Command($"SELECT {table}_id,coalesce(name,{(kind == CatalogEntityKind.Artist ? "name" : "title")}){extra} FROM {table} WHERE (@name<>'' AND normalized_name=@name){match} ORDER BY {table}_id LIMIT 101",
            ("@name", normalizedName), ("@isrc", isrc));
        var items = new List<ReleaseImportCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return items;
    }
    public async ValueTask<ReleaseImportBinding?> GetBindingAsync(string source, string externalId, CancellationToken cancellationToken = default)
    {
        using var command = Command("SELECT release_id,source_digest,source_fingerprint FROM release_import_binding WHERE source=@source AND external_id=@external",
            ("@source", source), ("@external", externalId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new(source, externalId, reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }
    public async ValueTask<IReadOnlyList<string>> FindPlacementOwnersAsync(string source, string externalId, CancellationToken cancellationToken = default)
    {
        using var command = Command("SELECT placement_id FROM placement_external_ref WHERE source=@source AND external_id=@external ORDER BY placement_id",
            ("@source", source), ("@external", externalId));
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        return ids;
    }
    public async ValueTask SavePreviewAsync(ReleaseImportPlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.FormatVersion != 1 || !Guid.TryParse(plan.OperationId, out _) || plan.ExpiresUtc <= plan.CreatedUtc)
            throw new CatalogFoundationValidationException("Invalid import plan.");
        if (await GetCatalogStampAsync(cancellationToken).ConfigureAwait(false) != plan.CatalogStamp)
            throw new ReleaseImportException(412, "import_state_changed", "Catalog changed during preview.");
        await SqliteCatalogFoundationRepository.AtomicAsync(connection, transaction, async () =>
        {
            if (plan.BaseOperationId is not null)
            {
                var previous = await GetAsync(plan.BaseOperationId, cancellationToken).ConfigureAwait(false);
                if (previous is null || previous.Outcome is not null || previous.SupersededBy is not null)
                    throw new ReleaseImportException(409, "import_base_invalid", "Base review was applied or superseded. Create a fresh preview.");
            }
            await Execute("INSERT INTO release_import_preview(operation_id,version,plan_json) VALUES(@id,@version,@json)", cancellationToken,
                ("@id", plan.OperationId), ("@version", plan.Version), ("@json", JsonSerializer.Serialize(plan, Json))).ConfigureAwait(false);
            if (plan.BaseOperationId is not null)
                await Execute("INSERT INTO release_import_supersession VALUES(@previous,@next)", cancellationToken,
                    ("@previous", plan.BaseOperationId), ("@next", plan.OperationId)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<StoredReleaseImport?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        using var command = Command("""
            SELECT p.plan_json,o.outcome_json,s.next_operation_id FROM release_import_preview p
            LEFT JOIN release_import_outcome o ON o.operation_id=p.operation_id
            LEFT JOIN release_import_supersession s ON s.previous_operation_id=p.operation_id WHERE p.operation_id=@id
            """, ("@id", operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new(JsonSerializer.Deserialize<ReleaseImportPlan>(reader.GetString(0), Json)!,
            reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<ReleaseImportOutcome>(reader.GetString(1), Json),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }
    public async ValueTask<ReleaseImportOutcome> ApplyAsync(string operationId, string expectedVersion, string reason,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            throw new ReleaseImportException(400, "validation_failed", "A review reason of 1-2000 characters is required.");
        ReleaseImportOutcome? outcome = null;
        await SqliteCatalogFoundationRepository.AtomicAsync(connection, transaction, async () =>
        {
            var stored = await GetAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new ReleaseImportException(404, "import_not_found", "Import review was not found.");
            var plan = stored.Plan;
            if (expectedVersion != plan.Version)
                throw new ReleaseImportException(412, "import_state_changed", "The original review token is required.");
            if (stored.Outcome is not null)
            {
                using var prior = Command("SELECT reason FROM release_import_outcome WHERE operation_id=@id", ("@id", operationId));
                if ((string?)await prior.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) != reason)
                    throw new ReleaseImportException(409, "import_replay_conflict", "Operation already applied with a different reason.");
                outcome = stored.Outcome; return;
            }
            if (stored.SupersededBy is not null) throw new ReleaseImportException(409, "import_superseded", "Review decisions changed. Use the newest immutable preview.");
            if (plan.ExpiresUtc <= now) throw new ReleaseImportException(410, "import_expired", "Review expired. Create a new preview.");
            if (plan.FormatVersion != 1) throw new ReleaseImportException(409, "import_plan_unsupported", "Create a preview using the current plan version.");
            if (plan.Blockers.Count > 0) throw new ReleaseImportException(409, "import_blocked", "Resolve all review blockers before applying.");
            if (await GetCatalogStampAsync(cancellationToken).ConfigureAwait(false) != plan.CatalogStamp)
                throw new ReleaseImportException(412, "import_state_changed", "Catalog or candidate bindings changed. Create a new preview.");
            var foundations = new SqliteCatalogFoundationRepository(connection, transaction);
            if (!plan.NoOp)
            {
                foreach (var artist in plan.Artists)
                    await foundations.SaveEntityAsync(artist.After, artist.Before?.Version, cancellationToken).ConfigureAwait(false);
                await foundations.SaveEntityAsync(plan.ProposedRelease, null, cancellationToken).ConfigureAwait(false);
                foreach (var recording in plan.Recordings)
                    if (recording.Before is null)
                        await foundations.SaveEntityAsync(recording.After, null, cancellationToken).ConfigureAwait(false);
                foreach (var placement in plan.Placements)
                    await foundations.SavePlacementAsync(placement,
                        (await foundations.GetMetadataAsync(CatalogEntityKind.Release, placement.ReleaseId, cancellationToken).ConfigureAwait(false))!.Version,
                        cancellationToken).ConfigureAwait(false);
                await Execute("""
                    INSERT INTO release_import_binding(source,external_id,release_id,source_digest,source_fingerprint,operation_id)
                    VALUES('beatport',@external,@release,@digest,@fingerprint,@operation)
                    """, cancellationToken, ("@external", plan.Source.ReleaseId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("@release", plan.ProposedRelease.Id), ("@digest", plan.Source.BundleDigest), ("@fingerprint", plan.SourceFingerprint), ("@operation", operationId)).ConfigureAwait(false);
                await PrepareDocuments(plan, cancellationToken).ConfigureAwait(false);
            }
            outcome = new(operationId, plan.ProposedRelease.Id, now, plan.NoOp,
                plan.NoOp ? 0 : plan.Artists.Count(a => a.Before is null),
                plan.NoOp ? 0 : plan.Recordings.Count(r => r.Before is null), plan.NoOp ? 0 : plan.Placements.Count, Guid.NewGuid().ToString("N"));
            var afterRelease = await new SqliteReleaseRepository(connection, transaction).GetByIdAsync(outcome.ReleaseId, cancellationToken).ConfigureAwait(false);
            var afterPlacements = await foundations.GetPlacementsAsync(outcome.ReleaseId, cancellationToken).ConfigureAwait(false);
            await Execute("INSERT INTO release_import_outcome(operation_id,reason,outcome_json,audit_json) VALUES(@id,@reason,@outcome,@audit)",
                cancellationToken, ("@id", operationId), ("@reason", reason), ("@outcome", JsonSerializer.Serialize(outcome, Json)),
                ("@audit", JsonSerializer.Serialize(new { Mechanism = "local-api-key", Plan = plan, AfterRelease = afterRelease, AfterPlacements = afterPlacements }, Json))).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return outcome!;
    }

    private async Task PrepareDocuments(ReleaseImportPlan plan, CancellationToken ct)
    {
        var artists = new SqliteArtistRepository(connection, transaction);
        var releases = new SqliteReleaseRepository(connection, transaction);
        var recordings = new SqliteRecordingRepository(connection, transaction);
        async Task<string> ContributorText(IEnumerable<(string ArtistId, string Role)> credits)
        {
            var parts = new List<string>();
            foreach (var credit in credits)
            {
                var artist = (await artists.GetByIdAsync(credit.ArtistId, ct).ConfigureAwait(false))!;
                parts.Add($"{credit.Role}: {artist.Name}");
            }
            return string.Join("\n", parts);
        }
        var artistIds = plan.ProposedRelease.ArtistCredits.Select(c => c.ArtistId)
            .Concat(plan.Placements.SelectMany(p => p.Credits).Select(c => c.ArtistId)).Distinct(StringComparer.Ordinal);
        foreach (var artistId in artistIds)
        {
            var artist = (await artists.GetByIdAsync(artistId, ct).ConfigureAwait(false))!;
            await Document("artist", artist.Id, $"{artist.Name}\nKind: {artist.Kind}\nIdentity: {artist.IdentityStatus}\n" +
                string.Join("\n", artist.Aliases.Select(a => a.Value).Order(StringComparer.Ordinal)), ct).ConfigureAwait(false);
        }
        var release = (await releases.GetByIdAsync(plan.ProposedRelease.Id, ct).ConfigureAwait(false))!;
        var label = (await new SqliteLabelRepository(connection, transaction).GetByIdAsync(plan.LabelId, ct).ConfigureAwait(false))!;
        var releaseCredits = await new SqliteCatalogFoundationRepository(connection, transaction).GetCreditsAsync(CatalogEntityKind.Release, release.Id, ct).ConfigureAwait(false);
        await Document("release", release.Id, $"{release.Title}\nLabel: {label.Name}\nCatalog: {release.CatalogNumber}\nUPC: {release.Upc}\nPublish date: {release.PublishDate}\nNew release date: {release.NewReleaseDate}\n" +
            await ContributorText(releaseCredits.Select(c => (c.ArtistId, c.Role))).ConfigureAwait(false), ct).ConfigureAwait(false);
        foreach (var write in plan.Recordings.DistinctBy(r => r.After.Id))
        {
            var recording = (await recordings.GetByIdAsync(write.After.Id, ct).ConfigureAwait(false))!;
            var credits = await new SqliteCatalogFoundationRepository(connection, transaction).GetCreditsAsync(CatalogEntityKind.Recording, recording.Id, ct).ConfigureAwait(false);
            await Document("recording", recording.Id, $"{recording.Title}\nVersion: {recording.MixName}\nISRC: {recording.Isrc}\nDuration ms: {recording.DurationMs?.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n" +
                await ContributorText(credits.Select(c => (c.ArtistId, c.Role))).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
    }
    private async Task Document(string kind, string id, string text, CancellationToken ct)
    {
        const string template = "catalog-text-v1";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template + "\n" + text))).ToLowerInvariant();
        await Execute("""
            INSERT INTO catalog_embedding_source(entity_type,entity_id,template_version,document_text,desired_hash,status)
            VALUES(@kind,@id,@template,@text,@hash,'pending')
            ON CONFLICT(entity_type,entity_id) DO UPDATE SET template_version=excluded.template_version,
                document_text=excluded.document_text,desired_hash=excluded.desired_hash,
                status=CASE WHEN catalog_embedding_source.generated_hash=excluded.desired_hash THEN 'current' ELSE 'pending' END
            """, ct, ("@kind", kind), ("@id", id), ("@template", template), ("@text", text), ("@hash", hash)).ConfigureAwait(false);
        await Execute("""
            INSERT INTO catalog_embedding_outbox(entity_type,entity_id,revision,enqueued_utc)
            SELECT entity_type,entity_id,lower(hex(randomblob(16))),strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM catalog_embedding_source WHERE entity_type=@kind AND entity_id=@id AND status<>'current'
            ON CONFLICT(entity_type,entity_id) DO UPDATE SET revision=excluded.revision,enqueued_utc=excluded.enqueued_utc
            """, ct, ("@kind", kind), ("@id", id)).ConfigureAwait(false);
    }
}
