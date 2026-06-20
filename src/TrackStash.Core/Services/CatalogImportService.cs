using System.Text;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Services;

// ── Public contract ────────────────────────────────────────────────────────────

public sealed record CatalogImportRequest(
    string CsvPath,
    bool DryRun = false);

public sealed record CatalogImportResult(
    string CsvPath,
    int TotalRows,
    int SucceededRows,
    int FailedRows,
    bool DryRun,
    IReadOnlyList<CatalogImportRowResult> RowResults);

public sealed record CatalogImportRowResult(
    int RowNumber,
    string EntityType,
    string? EntityId,
    string? Action,
    bool Success,
    string? Error);

// ── Service ────────────────────────────────────────────────────────────────────

/// <summary>
/// Parses a CSV file and upserts canonical entities via the storage provider,
/// resolving cross-entity dependencies (label → release → recording) in order.
/// Both trackstash-bootstrap (setup-time seeding) and trackstash-catalog
/// (ongoing operational import) call this service directly.
/// </summary>
public sealed class CatalogImportService
{
    private readonly IStorageProvider _provider;

    public CatalogImportService(IStorageProvider provider)
    {
        _provider = provider;
    }

    public async Task<CatalogImportResult> ImportAsync(
        CatalogImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CsvPath);

        if (!File.Exists(request.CsvPath))
            throw new ArgumentException($"CSV file not found: {request.CsvPath}");

        var rows    = ParseCsv(request.CsvPath);
        var results = new List<CatalogImportRowResult>();

        // session maps: rawName / normalizedName → entityId, built during this run
        var labelMap   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var artistMap  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var releaseMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // process in dependency order regardless of CSV row order
        var orderedTypes = new[] { "label", "artist", "release", "recording" };
        foreach (var entityType in orderedTypes)
        {
            foreach (var row in rows.Where(r => string.Equals(r.Type, entityType, StringComparison.OrdinalIgnoreCase)))
            {
                var result = request.DryRun
                    ? ValidateRowDryRun(row)
                    : entityType switch
                    {
                        "label"     => await ProcessLabelRowAsync(row, labelMap, cancellationToken).ConfigureAwait(false),
                        "artist"    => await ProcessArtistRowAsync(row, artistMap, cancellationToken).ConfigureAwait(false),
                        "release"   => await ProcessReleaseRowAsync(row, labelMap, artistMap, releaseMap, cancellationToken).ConfigureAwait(false),
                        "recording" => await ProcessRecordingRowAsync(row, artistMap, releaseMap, cancellationToken).ConfigureAwait(false),
                        _           => FailRow(row, entityType, $"Unexpected type: {entityType}"),
                    };
                results.Add(result);
            }
        }

        var knownTypeSet = new HashSet<string>(orderedTypes, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => !knownTypeSet.Contains(r.Type ?? "")))
            results.Add(FailRow(row, row.Type ?? "unknown", $"Unknown entity type: '{row.Type}'"));

        return new CatalogImportResult(
            CsvPath: request.CsvPath,
            TotalRows: rows.Count,
            SucceededRows: results.Count(r => r.Success),
            FailedRows: results.Count(r => !r.Success),
            DryRun: request.DryRun,
            RowResults: results.OrderBy(r => r.RowNumber).ToList());
    }

    // ── entity row processors ──────────────────────────────────────────────────

    private async Task<CatalogImportRowResult> ProcessLabelRowAsync(
        CsvRow row, Dictionary<string, string> labelMap, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(row.Name))
                return FailRow(row, "label", "Missing required field: name");

            var now          = DateTimeOffset.UtcNow;
            var normalizedName = EntityNameNormalizer.NormalizeStrict(row.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
                return FailRow(row, "label", "Label name cannot normalize to an empty key.");

            await using var uow = await _provider.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);

            Label? existing = null;
            if (!string.IsNullOrWhiteSpace(row.Source) && !string.IsNullOrWhiteSpace(row.ExternalId))
                existing = await uow.Labels.GetByExternalRefAsync(row.Source!, row.ExternalId!, ct).ConfigureAwait(false);
            existing ??= await uow.Labels.GetByNormalizedNameAsync(normalizedName, ct).ConfigureAwait(false);

            var action   = existing is null ? "Created" : "ReusedByNormalization";
            var labelId  = !string.IsNullOrWhiteSpace(row.Id) ? row.Id! : existing?.Id ?? NewId();
            var refs     = BuildExternalRefs(row.Source, row.ExternalId, now);

            await uow.Labels.UpsertAsync(new Label
            {
                Id             = labelId,
                Name           = existing?.Name ?? row.Name,
                NormalizedName = existing?.NormalizedName ?? normalizedName,
                CreatedUtc     = existing?.CreatedUtc ?? now,
                UpdatedUtc     = now,
                ExternalReferences = refs,
            }, ct).ConfigureAwait(false);
            await uow.CommitAsync(ct).ConfigureAwait(false);

            AddToMap(labelMap, row.Name, normalizedName, labelId);
            return new CatalogImportRowResult(row.RowNumber, "label", labelId, action, true, null);
        }
        catch (Exception ex) { return FailRow(row, "label", ex.Message); }
    }

    private async Task<CatalogImportRowResult> ProcessArtistRowAsync(
        CsvRow row, Dictionary<string, string> artistMap, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(row.Name))
                return FailRow(row, "artist", "Missing required field: name");

            var now            = DateTimeOffset.UtcNow;
            var normalizedName = EntityNameNormalizer.NormalizeStrict(row.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
                return FailRow(row, "artist", "Artist name cannot normalize to an empty key.");

            await using var uow = await _provider.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);

            Artist? existing = null;
            if (!string.IsNullOrWhiteSpace(row.Source) && !string.IsNullOrWhiteSpace(row.ExternalId))
                existing = await uow.Artists.GetByExternalRefAsync(row.Source!, row.ExternalId!, ct).ConfigureAwait(false);
            existing ??= await uow.Artists.GetByNormalizedNameAsync(normalizedName, ct).ConfigureAwait(false);

            var action   = existing is null ? "Created" : "ReusedByNormalization";
            var artistId = !string.IsNullOrWhiteSpace(row.Id) ? row.Id! : existing?.Id ?? NewId();
            var refs     = BuildExternalRefs(row.Source, row.ExternalId, now);

            await uow.Artists.UpsertAsync(new Artist
            {
                Id             = artistId,
                Name           = existing?.Name ?? row.Name,
                NormalizedName = existing?.NormalizedName ?? normalizedName,
                SortName       = existing?.SortName ?? row.SortName,
                CreatedUtc     = existing?.CreatedUtc ?? now,
                UpdatedUtc     = now,
                ExternalReferences = refs,
                Relationships  = existing?.Relationships ?? Array.Empty<EntityRelationship>(),
            }, ct).ConfigureAwait(false);
            await uow.CommitAsync(ct).ConfigureAwait(false);

            AddToMap(artistMap, row.Name, normalizedName, artistId);
            return new CatalogImportRowResult(row.RowNumber, "artist", artistId, action, true, null);
        }
        catch (Exception ex) { return FailRow(row, "artist", ex.Message); }
    }

    private async Task<CatalogImportRowResult> ProcessReleaseRowAsync(
        CsvRow row,
        Dictionary<string, string> labelMap, Dictionary<string, string> artistMap,
        Dictionary<string, string> releaseMap, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(row.Title))
                return FailRow(row, "release", "Missing required field: title");

            var now             = DateTimeOffset.UtcNow;
            var normalizedTitle = EntityNameNormalizer.NormalizeStrict(row.Title);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
                return FailRow(row, "release", "Release title cannot normalize to an empty key.");

            var labelId  = await ResolveLabelIdAsync(row.LabelRef, labelMap, ct).ConfigureAwait(false);
            var artistId = await ResolveArtistIdAsync(row.ArtistRef, artistMap, ct).ConfigureAwait(false);

            await using var uow = await _provider.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);

            Release? existing = null;
            if (!string.IsNullOrWhiteSpace(row.Source) && !string.IsNullOrWhiteSpace(row.ExternalId))
                existing = await uow.Releases.GetByExternalRefAsync(row.Source!, row.ExternalId!, ct).ConfigureAwait(false);
            existing ??= labelId is not null
                ? await uow.Releases.GetByNormalizedTitleAndLabelAsync(normalizedTitle, labelId, ct).ConfigureAwait(false)
                : await uow.Releases.GetByNormalizedTitleAndLabelAsync(normalizedTitle, null, ct).ConfigureAwait(false);

            var action    = existing is null ? "Created" : "ReusedByNormalization";
            var releaseId = !string.IsNullOrWhiteSpace(row.Id) ? row.Id! : existing?.Id ?? NewId();
            var refs      = BuildExternalRefs(row.Source, row.ExternalId, now);

            var labelLinks    = labelId is not null
                ? (IReadOnlyList<ReleaseLabelLink>)[new ReleaseLabelLink { LabelId = labelId, IsPrimary = true, Role = "primary" }]
                : Array.Empty<ReleaseLabelLink>();
            var artistCredits = artistId is not null
                ? (IReadOnlyList<ReleaseArtistCredit>)[new ReleaseArtistCredit { ArtistId = artistId, Position = 0 }]
                : Array.Empty<ReleaseArtistCredit>();

            await uow.Releases.UpsertAsync(new Release
            {
                Id             = releaseId,
                Name           = existing?.Name ?? row.Title,
                NormalizedName = existing?.NormalizedName ?? normalizedTitle,
                Title          = existing?.Title ?? row.Title,
                CreatedUtc     = existing?.CreatedUtc ?? now,
                UpdatedUtc     = now,
                ExternalReferences = refs,
                LabelLinks         = labelLinks,
                ArtistCredits      = artistCredits,
            }, ct).ConfigureAwait(false);
            await uow.CommitAsync(ct).ConfigureAwait(false);

            AddToMap(releaseMap, row.Title, normalizedTitle, releaseId);
            return new CatalogImportRowResult(row.RowNumber, "release", releaseId, action, true, null);
        }
        catch (Exception ex) { return FailRow(row, "release", ex.Message); }
    }

    private async Task<CatalogImportRowResult> ProcessRecordingRowAsync(
        CsvRow row,
        Dictionary<string, string> artistMap, Dictionary<string, string> releaseMap,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(row.Title))
                return FailRow(row, "recording", "Missing required field: title");

            var now            = DateTimeOffset.UtcNow;
            var normalizedTitle = EntityNameNormalizer.NormalizeStrict(row.Title);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
                return FailRow(row, "recording", "Recording title cannot normalize to an empty key.");

            var normalizedMix  = string.IsNullOrWhiteSpace(row.MixName) ? null : EntityNameNormalizer.NormalizeStrict(row.MixName);
            var artistId       = await ResolveArtistIdAsync(row.ArtistRef, artistMap, ct).ConfigureAwait(false);
            var releaseId      = await ResolveReleaseIdAsync(row.ReleaseRef, releaseMap, ct).ConfigureAwait(false);

            await using var uow = await _provider.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);

            Recording? existing = null;
            if (!string.IsNullOrWhiteSpace(row.Source) && !string.IsNullOrWhiteSpace(row.ExternalId))
                existing = await uow.Recordings.GetByExternalRefAsync(row.Source!, row.ExternalId!, ct).ConfigureAwait(false);
            if (existing is null && !string.IsNullOrWhiteSpace(row.Isrc))
                existing = await uow.Recordings.GetByIsrcAsync(row.Isrc!, ct).ConfigureAwait(false);
            existing ??= await uow.Recordings.GetByNormalizedTitleAndMixNameAsync(normalizedTitle, normalizedMix, ct).ConfigureAwait(false);

            var action      = existing is null ? "Created" : "ReusedByNormalization";
            var recordingId = !string.IsNullOrWhiteSpace(row.Id) ? row.Id! : existing?.Id ?? NewId();
            var refs        = BuildExternalRefs(row.Source, row.ExternalId, now);

            var artistCredits = artistId is not null
                ? (IReadOnlyList<RecordingArtistCredit>)[new RecordingArtistCredit
                    {
                        ArtistId = artistId,
                        Role     = string.IsNullOrWhiteSpace(row.ArtistRole) ? "primary" : row.ArtistRole,
                        Position = 0,
                    }]
                : Array.Empty<RecordingArtistCredit>();

            var releaseLinks = releaseId is not null
                ? (IReadOnlyList<RecordingReleaseLink>)[new RecordingReleaseLink
                    {
                        ReleaseId   = releaseId,
                        DiscNumber  = row.DiscNumber,
                        TrackNumber = row.TrackNumber,
                    }]
                : Array.Empty<RecordingReleaseLink>();

            await uow.Recordings.UpsertAsync(new Recording
            {
                Id             = recordingId,
                Name           = existing?.Name ?? row.Title,
                NormalizedName = existing?.NormalizedName ?? normalizedTitle,
                Title          = existing?.Title ?? row.Title,
                MixName        = existing?.MixName ?? normalizedMix,
                Isrc           = existing?.Isrc ?? row.Isrc,
                CreatedUtc     = existing?.CreatedUtc ?? now,
                UpdatedUtc     = now,
                ExternalReferences = refs,
                ArtistCredits      = artistCredits,
                ReleaseLinks       = releaseLinks,
                Relationships      = Array.Empty<RecordingRelationship>(),
            }, ct).ConfigureAwait(false);
            await uow.CommitAsync(ct).ConfigureAwait(false);

            return new CatalogImportRowResult(row.RowNumber, "recording", recordingId, action, true, null);
        }
        catch (Exception ex) { return FailRow(row, "recording", ex.Message); }
    }

    // ── cross-entity reference resolution ─────────────────────────────────────

    private async Task<string?> ResolveLabelIdAsync(
        string? refValue, Dictionary<string, string> sessionMap, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refValue)) return null;
        if (sessionMap.TryGetValue(refValue, out var id)) return id;
        var norm = EntityNameNormalizer.NormalizeStrict(refValue);
        if (!string.IsNullOrWhiteSpace(norm) && sessionMap.TryGetValue(norm, out id)) return id;

        await using var uow = await _provider.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var byId = await uow.Labels.GetByIdAsync(refValue, ct).ConfigureAwait(false);
        if (byId is not null) return byId.Id;
        if (!string.IsNullOrWhiteSpace(norm))
        {
            var byName = await uow.Labels.GetByNormalizedNameAsync(norm, ct).ConfigureAwait(false);
            if (byName is not null) return byName.Id;
        }
        return null;
    }

    private async Task<string?> ResolveArtistIdAsync(
        string? refValue, Dictionary<string, string> sessionMap, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refValue)) return null;
        if (sessionMap.TryGetValue(refValue, out var id)) return id;
        var norm = EntityNameNormalizer.NormalizeStrict(refValue);
        if (!string.IsNullOrWhiteSpace(norm) && sessionMap.TryGetValue(norm, out id)) return id;

        await using var uow = await _provider.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var byId = await uow.Artists.GetByIdAsync(refValue, ct).ConfigureAwait(false);
        if (byId is not null) return byId.Id;
        if (!string.IsNullOrWhiteSpace(norm))
        {
            var byName = await uow.Artists.GetByNormalizedNameAsync(norm, ct).ConfigureAwait(false);
            if (byName is not null) return byName.Id;
        }
        return null;
    }

    private async Task<string?> ResolveReleaseIdAsync(
        string? refValue, Dictionary<string, string> sessionMap, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refValue)) return null;
        if (sessionMap.TryGetValue(refValue, out var id)) return id;
        var norm = EntityNameNormalizer.NormalizeStrict(refValue);
        if (!string.IsNullOrWhiteSpace(norm) && sessionMap.TryGetValue(norm, out id)) return id;

        await using var uow = await _provider.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var byId = await uow.Releases.GetByIdAsync(refValue, ct).ConfigureAwait(false);
        if (byId is not null) return byId.Id;
        if (!string.IsNullOrWhiteSpace(norm))
        {
            var byName = await uow.Releases.GetByNormalizedTitleAndLabelAsync(norm, null, ct).ConfigureAwait(false);
            if (byName is not null) return byName.Id;
        }
        return null;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static void AddToMap(Dictionary<string, string> map, string raw, string? norm, string id)
    {
        map[raw] = id;
        if (!string.IsNullOrWhiteSpace(norm))
            map[norm] = id;
    }

    private static CatalogImportRowResult FailRow(CsvRow row, string entityType, string error) =>
        new(row.RowNumber, entityType, null, null, false, error);

    private static CatalogImportRowResult ValidateRowDryRun(CsvRow row)
    {
        var entityType = row.Type?.ToLowerInvariant() ?? "unknown";
        var missing = entityType switch
        {
            "label"     => string.IsNullOrWhiteSpace(row.Name) ? "name" : null,
            "artist"    => string.IsNullOrWhiteSpace(row.Name) ? "name" : null,
            "release"   => string.IsNullOrWhiteSpace(row.Title) ? "title" : null,
            "recording" => string.IsNullOrWhiteSpace(row.Title) ? "title" : null,
            _ => null,
        };
        return missing is not null
            ? FailRow(row, entityType, $"Missing required field: {missing}")
            : new CatalogImportRowResult(row.RowNumber, entityType, null, "DryRun", true, null);
    }

    private static IReadOnlyList<EntityReference> BuildExternalRefs(string? source, string? externalId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(externalId))
            return Array.Empty<EntityReference>();

        return [new EntityReference { Source = source!, ExternalId = externalId!, IsPrimary = true, LastSeenUtc = now }];
    }

    private static string NewId() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    // ── CSV parsing ────────────────────────────────────────────────────────────

    private static List<CsvRow> ParseCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return [];

        var headers = SplitCsvLine(lines[0]);
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
            col[headers[i].Trim()] = i;

        var rows = new List<CsvRow>();
        for (var lineNum = 1; lineNum < lines.Length; lineNum++)
        {
            var line = lines[lineNum].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            var f = SplitCsvLine(line);
            rows.Add(new CsvRow(lineNum + 1)
            {
                Type        = Field(f, col, "type"),
                Name        = Field(f, col, "name"),
                Title       = Field(f, col, "title"),
                MixName     = Field(f, col, "mix_name"),
                Isrc        = Field(f, col, "isrc"),
                SortName    = Field(f, col, "sort_name"),
                LabelRef    = Field(f, col, "label_ref"),
                ArtistRef   = Field(f, col, "artist_ref"),
                ArtistRole  = Field(f, col, "artist_role"),
                ReleaseRef  = Field(f, col, "release_ref"),
                DiscNumber  = ParseNullableInt(Field(f, col, "disc_number")),
                TrackNumber = ParseNullableInt(Field(f, col, "track_number")),
                Source      = Field(f, col, "source"),
                ExternalId  = Field(f, col, "external_id"),
                Id          = Field(f, col, "id"),
            });
        }
        return rows;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(""); break; }
            if (line[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; }
                        else { i++; break; }
                    }
                    else { sb.Append(line[i++]); }
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++;
            }
            else
            {
                var end = line.IndexOf(',', i);
                if (end < 0) { fields.Add(line[i..].Trim()); break; }
                fields.Add(line[i..end].Trim());
                i = end + 1;
            }
        }
        return fields;
    }

    private static string? Field(List<string> f, Dictionary<string, int> col, string name)
    {
        if (!col.TryGetValue(name, out var idx) || idx >= f.Count) return null;
        var v = f[idx].Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static int? ParseNullableInt(string? s) => int.TryParse(s, out var n) ? n : null;

    private sealed class CsvRow(int rowNumber)
    {
        public int     RowNumber  { get; } = rowNumber;
        public string? Type       { get; init; }
        public string? Name       { get; init; }
        public string? Title      { get; init; }
        public string? MixName    { get; init; }
        public string? Isrc       { get; init; }
        public string? SortName   { get; init; }
        public string? LabelRef   { get; init; }
        public string? ArtistRef  { get; init; }
        public string? ArtistRole { get; init; }
        public string? ReleaseRef { get; init; }
        public int?    DiscNumber  { get; init; }
        public int?    TrackNumber { get; init; }
        public string? Source      { get; init; }
        public string? ExternalId  { get; init; }
        public string? Id          { get; init; }
    }
}
