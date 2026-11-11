using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

/// <summary>Reads Beatport corpus/cache observations without migrations, network access or source writes.</summary>
public sealed class SqlitePurchaseReleaseSource : IPurchaseReleaseSource
{
    private const int MaxPayloadBytes = 4 * 1024 * 1024;
    private const int MaxTracks = 2000;
    private readonly string _connectionString;
    private readonly string _databasePath;

    public SqlitePurchaseReleaseSource(string databasePath)
    {
        if (!Path.IsPathFullyQualified(databasePath))
            throw new ArgumentException("Source database path must be absolute.", nameof(databasePath));
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private,
            Pooling = false, DefaultTimeout = 10,
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(ct).ConfigureAwait(false); return connection; }
        catch (SqliteException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new PurchaseSourceUnavailableException("The configured purchase source could not be opened.", ex);
        }
        catch (OperationCanceledException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask<string> GetSnapshotStampAsync(CancellationToken cancellationToken = default)
    {
        static string Part(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "absent";
        }
        if (!File.Exists(_databasePath)) throw new PurchaseSourceUnavailableException("The configured purchase source could not be opened.");
        return ValueTask.FromResult($"{Part(_databasePath)}|{Part(_databasePath + "-wal")}");
    }

    public async ValueTask<PurchaseSourcePage<PurchaseReleaseCandidate>> QueryAsync(PurchaseReleaseQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            const string where = "FROM bpc_release r WHERE r.in_purchase_corpus = 1 AND r.beatport_release_id > 0 AND (@q = '' OR instr(lower(r.title), lower(@q)) > 0 OR CAST(r.beatport_release_id AS TEXT) = @q)" +
                " AND r.beatport_release_id NOT IN (SELECT value FROM json_each(@excluded))" +
                " AND (@included IS NULL OR r.beatport_release_id IN (SELECT value FROM json_each(@included)))";
            command.Parameters.AddWithValue("@q", query.Search?.Trim() ?? "");
            command.Parameters.AddWithValue("@excluded", JsonSerializer.Serialize(query.ExcludedReleaseIds ?? []));
            command.Parameters.AddWithValue("@included", query.IncludedReleaseIds is null ? DBNull.Value : JsonSerializer.Serialize(query.IncludedReleaseIds));
            command.CommandText = "SELECT count(*) " + where;
            var broker = await HasBrokerEntitiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var total = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            command.CommandText = "SELECT r.beatport_release_id, r.title, EXISTS(SELECT 1 FROM beatport_release_cache c WHERE c.beatport_release_id = r.beatport_release_id)" +
                (broker ? " OR EXISTS(SELECT 1 FROM beatport_broker_entity e WHERE e.entity_type = 'release' AND e.entity_id = r.beatport_release_id) " : " ") +
                where + " ORDER BY r.title COLLATE NOCASE, r.beatport_release_id LIMIT @take OFFSET @skip";
            command.Parameters.AddWithValue("@take", query.PageSize);
            command.Parameters.AddWithValue("@skip", ((long)query.Page - 1) * query.PageSize);
            var items = new List<PurchaseReleaseCandidate>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                items.Add(new(reader.GetInt64(0), reader.GetString(1), "corpus_flag", reader.GetBoolean(2)));
            return new(items, total, query.Page, query.PageSize);
        }
        catch (SqliteException ex) { throw new PurchaseSourceUnavailableException("The purchase corpus schema could not be read.", ex); }
    }

    public async ValueTask<PurchaseReleaseInspection?> InspectAsync(long releaseId, PurchaseReleaseQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate();
        if (releaseId <= 0 || query.Search is not null)
            throw new PurchaseSourceValidationException("Use a positive release ID and pagination only for inspection.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM bpc_release WHERE beatport_release_id = @id AND in_purchase_corpus = 1";
            command.Parameters.AddWithValue("@id", releaseId);
            var inCorpus = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
            if (!inCorpus && !query.AllowCachedRelease) return null;
            command.CommandText = "SELECT release_json, fetched_utc FROM beatport_release_cache WHERE beatport_release_id = @id";
            string? payload = null, fetched = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    payload = reader.GetString(0); fetched = reader.GetString(1);
                }
            }
            var broker = await HasBrokerEntitiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (broker)
            {
                command.CommandText = "SELECT payload_json, fetched_utc FROM beatport_broker_entity WHERE entity_type = 'release' AND entity_id = @id";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    (payload, fetched) = Newer((payload, fetched), (reader.GetString(0), reader.GetString(1)));
            }
            if (!inCorpus && payload is null) return null;
            var inspection = await InspectCoreAsync(releaseId, payload, fetched, query,
                (ids, ct) => LoadTracksAsync(connection, transaction, ids, broker, ct), cancellationToken).ConfigureAwait(false);
            return inCorpus ? inspection : inspection with { SourceKind = "catalog_cache" };
        }
        catch (SqliteException ex) { throw new PurchaseSourceUnavailableException("The Beatport cache schema could not be read.", ex); }
    }

    public async ValueTask<IReadOnlyList<PurchaseReleaseInspection>> InspectAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            var broker = await HasBrokerEntitiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var releases = new List<(long Id, string? Payload, string? Fetched)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = broker
                    ? """
                      SELECT r.beatport_release_id, c.release_json, c.fetched_utc, e.payload_json, e.fetched_utc FROM bpc_release r
                      LEFT JOIN beatport_release_cache c ON c.beatport_release_id = r.beatport_release_id
                      LEFT JOIN beatport_broker_entity e ON e.entity_type = 'release' AND e.entity_id = r.beatport_release_id
                      WHERE r.in_purchase_corpus = 1 ORDER BY r.beatport_release_id
                      """
                    : """
                      SELECT r.beatport_release_id, c.release_json, c.fetched_utc, NULL, NULL FROM bpc_release r
                      LEFT JOIN beatport_release_cache c ON c.beatport_release_id = r.beatport_release_id
                      WHERE r.in_purchase_corpus = 1 ORDER BY r.beatport_release_id
                      """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                static string? Get(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var (payload, fetched) = Newer((Get(reader, 1), Get(reader, 2)), (Get(reader, 3), Get(reader, 4)));
                    releases.Add((reader.GetInt64(0), payload, fetched));
                }
            }
            // One lookup-table scan for every manifest track; each release then applies the same per-release rules and limits.
            var all = await LoadTracksAsync(connection, transaction, null, broker, cancellationToken).ConfigureAwait(false);
            Task<Dictionary<long, List<TrackObservation>>> Slice(long[] ids, CancellationToken _)
            {
                var slice = new Dictionary<long, List<TrackObservation>>();
                long bytes = 0;
                foreach (var id in ids)
                    if (all.TryGetValue(id, out var list))
                    {
                        if (list.Count > 100) throw new PurchaseSourceUnavailableException("Too many conflicting observations for one track.");
                        bytes += list.Sum(o => (long)Encoding.UTF8.GetByteCount(o.Json));
                        if (bytes > 32 * 1024 * 1024) throw new PurchaseSourceUnavailableException("Release inspection exceeds the supported total payload size.");
                        slice[id] = list;
                    }
                return Task.FromResult(slice);
            }
            var results = new List<PurchaseReleaseInspection>(releases.Count);
            foreach (var (id, payload, fetched) in releases)
                results.Add(await InspectCoreAsync(id, payload, fetched, new PurchaseReleaseQuery(1, 1), Slice, cancellationToken).ConfigureAwait(false));
            return results;
        }
        catch (SqliteException ex) { throw new PurchaseSourceUnavailableException("The Beatport cache schema could not be read.", ex); }
    }

    private static async Task<PurchaseReleaseInspection> InspectCoreAsync(long releaseId, string? payload, string? fetched, PurchaseReleaseQuery query,
        Func<long[], CancellationToken, Task<Dictionary<long, List<TrackObservation>>>> loadTracks, CancellationToken cancellationToken)
    {
        {
            var diagnostics = new List<ReleaseSourceDiagnostic>
            {
                new("corpus_not_receipt", "warning", "Derived corpus flag is an entry-point hint, not receipt or media ownership proof."),
                new("numbering_unverified", "warning", "Source sequence is display order only; official numbering is unknown."),
                new("identity_review_required", "warning", "Source completeness does not certify canonical Artist or Recording identity."),
            };
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Hash(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                digest.AppendData(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
                digest.AppendData(bytes);
            }
            Hash("beatport-cache-inspection-v1"); Hash(releaseId.ToString(CultureInfo.InvariantCulture));
            Hash(payload ?? "missing"); Hash(fetched ?? "unknown");
            using var release = Parse(payload, diagnostics, "release");
            var root = release?.RootElement ?? default;
            string? title = null, catalog = null, upc = null, publish = null, newRelease = null;
            SourceReleaseLabel? label = null;
            var credits = new List<SourceArtistCredit>();
            var manifest = new List<long?>();
            int? declared = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (Id(root) != releaseId) Block(diagnostics, "release_identity_mismatch", "Cached release ID disagrees with the selected candidate.");
                title = Text(root, "name");
                if (string.IsNullOrWhiteSpace(title)) Block(diagnostics, "release_title_missing", "Release title is missing.");
                label = Label(root);
                if (label is null) Block(diagnostics, "label_missing", "Release label identity/name is missing or invalid.");
                catalog = Text(root, "catalog_number"); upc = Text(root, "upc");
                publish = Text(root, "publish_date"); newRelease = Text(root, "new_release_date");
                credits = Credits(root, diagnostics);
                declared = Integer(root, "track_count") is { } count && count is >= 0 and <= int.MaxValue ? (int)count : null;
                if (declared is null) Block(diagnostics, "track_count_missing", "Declared track count is missing or invalid.");
                if (!root.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
                    Block(diagnostics, "manifest_missing", "Release track manifest is missing or invalid.");
                else if (tracks.GetArrayLength() > MaxTracks)
                    Block(diagnostics, "manifest_too_large", $"Release exceeds the inspection limit of {MaxTracks} tracks.");
                else
                {
                    foreach (var track in tracks.EnumerateArray())
                    {
                        var id = TrackUrlId(track);
                        manifest.Add(id);
                        if (id is null) Block(diagnostics, "invalid_track_reference", "Manifest item is not an approved Beatport track URL.", manifest.Count);
                    }
                    if (manifest.Count == 0) Block(diagnostics, "manifest_empty", "Release track manifest is empty.");
                    if (declared != manifest.Count) Block(diagnostics, "track_count_mismatch", "Declared track count disagrees with the manifest.");
                    if (manifest.OfType<long>().Distinct().Count() != manifest.Count(id => id is not null))
                        Block(diagnostics, "duplicate_track_reference", "Release manifest contains duplicate provider track IDs.");
                }
            }
            var cached = await loadTracks(manifest.OfType<long>().Distinct().ToArray(), cancellationToken).ConfigureAwait(false);
            var results = new List<SourceReleaseTrack>();
            foreach (var id in manifest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var position = results.Count + 1;
                if (id is null || !cached.TryGetValue(id.Value, out var observations))
                {
                    Block(diagnostics, "track_payload_missing", "A manifest item has no successful full cached track payload.", position);
                    results.Add(new(position, id, "missing", null, null, null, null, [], null));
                    continue;
                }
                // All observations enter the digest; ordering is deterministic across repeated reads/pages.
                foreach (var observation in observations) { Hash(observation.Json); Hash(observation.Timestamp); }
                var chosen = observations[0];
                var before = diagnostics.Count(d => d.Severity == "blocker");
                if (observations.Select(o => o.Json).Distinct(StringComparer.Ordinal).Skip(1).Any())
                    Block(diagnostics, "conflicting_track_payloads", "Multiple cached payloads disagree for this track ID; refresh or review the source.", position);
                using var track = Parse(chosen.Json, diagnostics, "track", position);
                var item = track?.RootElement ?? default;
                var trackCredits = new List<SourceArtistCredit>();
                if (item.ValueKind == JsonValueKind.Object)
                {
                    if (Id(item) != id || !item.TryGetProperty("release", out var parent) || Id(parent) != releaseId)
                        Block(diagnostics, "track_release_mismatch", "Track/release identity disagrees with the release manifest.", position);
                    else if (Label(parent)?.ProviderId != label?.ProviderId)
                        Block(diagnostics, "track_label_mismatch", "Track label identity disagrees with the release label.", position);
                    if (string.IsNullOrWhiteSpace(Text(item, "name")))
                        Block(diagnostics, "track_title_missing", "Track title is missing.", position);
                    trackCredits = Credits(item, diagnostics, position);
                }
                var status = diagnostics.Count(d => d.Severity == "blocker") == before ? "cached" : "invalid";
                results.Add(new(position, id, status, Text(item, "name"), Text(item, "mix_name"), Text(item, "isrc"),
                    Integer(item, "length_ms"), trackCredits, Timestamp(chosen.Timestamp, diagnostics, position)));
            }
            var retrieved = Timestamp(fetched, diagnostics);
            var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            var page = results.Skip((int)Math.Min(((long)query.Page - 1) * query.PageSize, results.Count)).Take(query.PageSize).ToArray();
            return new(releaseId, "corpus_flag", title, label, catalog, upc, publish, newRelease, retrieved, hash,
                !diagnostics.Any(d => d.Severity == "blocker"), declared, manifest.Count, results.Count(t => t.Status == "cached"),
                credits, new(page, results.Count, query.Page, query.PageSize), diagnostics);
        }
    }

    private sealed record TrackObservation(string Json, string Timestamp);

    private static async Task<bool> HasBrokerEntitiesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'beatport_broker_entity'";
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    /// <summary>Picks the more recently fetched of the psBeatPort cache and broker payloads (broker wins ties).</summary>
    private static (string? Payload, string? Fetched) Newer((string? Payload, string? Fetched) cache, (string? Payload, string? Fetched) broker)
    {
        if (broker.Payload is null) return cache;
        if (cache.Payload is null) return broker;
        static DateTimeOffset At(string? value) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue;
        return At(broker.Fetched) >= At(cache.Fetched) ? broker : cache;
    }

    private static async Task<Dictionary<long, List<TrackObservation>>> LoadTracksAsync(SqliteConnection connection,
        SqliteTransaction transaction, long[]? ids, bool broker, CancellationToken ct)
    {
        var result = new Dictionary<long, List<TrackObservation>>();
        long payloadBytes = 0;
        if (ids is { Length: 0 }) return result;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT json_extract(j.value, '$.id'), j.value, s.searched_utc
            FROM beatport_isrc_lookup s,
                 json_each(CASE WHEN json_valid(s.tracks_json) THEN
                     CASE WHEN json_type(s.tracks_json) = 'array' THEN s.tracks_json ELSE '[]' END
                     ELSE '[]' END) j
            WHERE s.lookup_status = 'Success'
              AND json_type(CASE WHEN j.type = 'object' THEN j.value ELSE '{}' END, '$.id') = 'integer'
              AND (@ids IS NULL OR json_extract(CASE WHEN j.type = 'object' THEN j.value ELSE '{}' END, '$.id') IN (SELECT value FROM json_each(@ids)))
            ORDER BY s.normalized_isrc, j.key
            """;
        command.Parameters.AddWithValue("@ids", ids is null ? DBNull.Value : JsonSerializer.Serialize(ids));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var id = reader.GetInt64(0);
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            // The batch scan defers these per-release limits to its slice, matching single-release inspection.
            if (ids is not null && list.Count >= 100) throw new PurchaseSourceUnavailableException("Too many conflicting observations for one track.");
            var json = reader.GetString(1);
            payloadBytes += Encoding.UTF8.GetByteCount(json);
            if (ids is not null && payloadBytes > 32 * 1024 * 1024)
                throw new PurchaseSourceUnavailableException("Release inspection exceeds the supported total payload size.");
            list.Add(new(json, reader.GetString(2)));
        }
        if (!broker) return result;
        await reader.DisposeAsync().ConfigureAwait(false);
        // A track fetched directly by ID through the broker is authoritative and supersedes ISRC-search observations,
        // which also resolves conflicting_track_payloads once the track has been refreshed.
        command.CommandText = """
            SELECT entity_id, payload_json, fetched_utc FROM beatport_broker_entity
            WHERE entity_type = 'track' AND (@ids IS NULL OR entity_id IN (SELECT value FROM json_each(@ids)))
            ORDER BY entity_id
            """;
        await using var entities = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await entities.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var json = entities.GetString(1);
            payloadBytes += Encoding.UTF8.GetByteCount(json);
            if (ids is not null && payloadBytes > 32 * 1024 * 1024)
                throw new PurchaseSourceUnavailableException("Release inspection exceeds the supported total payload size.");
            result[entities.GetInt64(0)] = [new(json, entities.GetString(2))];
        }
        return result;
    }

    private static JsonDocument? Parse(string? payload, List<ReleaseSourceDiagnostic> diagnostics, string scope, int? position = null)
    {
        if (payload is null || Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            Block(diagnostics, scope + "_payload_unavailable", "Cached payload is missing or exceeds the supported size.", position);
            return null;
        }
        try
        {
            var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            Block(diagnostics, scope + "_payload_invalid", "Cached payload is not a full object.", position);
        }
        catch (JsonException) { Block(diagnostics, scope + "_payload_invalid", "Cached payload is malformed JSON.", position); }
        return null;
    }

    private static List<SourceArtistCredit> Credits(JsonElement root, List<ReleaseSourceDiagnostic> diagnostics, int? position = null)
    {
        var result = new List<SourceArtistCredit>();
        foreach (var (field, role) in new[] { ("artists", "primary"), ("remixers", "remixer") })
        {
            if (!root.TryGetProperty(field, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                Block(diagnostics, "credit_array_missing", $"Structured {field} array is missing or invalid.", position);
                continue;
            }
            var order = 0;
            foreach (var artist in array.EnumerateArray())
            {
                order++;
                if (Id(artist) is not { } id || string.IsNullOrWhiteSpace(Text(artist, "name")))
                { Block(diagnostics, "credit_identity_invalid", "Artist credit has no valid provider ID/name.", position); continue; }
                var name = Text(artist, "name")!;
                result.Add(new(id, name, role, order));
                if (name.Contains(',') || name.Contains(" & ", StringComparison.Ordinal))
                    diagnostics.Add(new("compound_name_review", "warning", "Credit punctuation may describe a genuine act or an unresolved compound; never split automatically.", position));
            }
        }
        return result;
    }

    private static SourceReleaseLabel? Label(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("label", out var label) &&
        Id(label) is { } id && !string.IsNullOrWhiteSpace(Text(label, "name")) ? new(id, Text(label, "name")!) : null;
    private static long? Id(JsonElement root) => Integer(root, "id") is { } id && id > 0 ? id : null;
    private static long? Integer(JsonElement root, string field) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    private static string? Text(JsonElement root, string field) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? TrackUrlId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.String || !Uri.TryCreate(item.GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "api.beatport.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4 && parts[0] == "v4" && parts[1] == "catalog" && parts[2] == "tracks" &&
            long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }
    private static DateTimeOffset? Timestamp(string? value, List<ReleaseSourceDiagnostic> diagnostics, int? position = null)
    {
        if (value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            return timestamp;
        diagnostics.Add(new("retrieval_time_unknown", "warning", "Cache retrieval timestamp is missing or invalid.", position));
        return null;
    }
    private static void Block(List<ReleaseSourceDiagnostic> diagnostics, string code, string message, int? position = null) =>
        diagnostics.Add(new(code, "blocker", message, position));
}
