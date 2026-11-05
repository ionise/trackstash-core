using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed partial class SqliteArtistRepository
{
    private const string ArtistCreditsCte = """
        WITH credits AS (
          SELECT c.credit_id,c.artist_id,c.release_id,NULL AS recording_id,NULL AS placement_id,
            c.role,'release' AS scope,c.credit_name,c.position
          FROM release_artist_credit c
          UNION ALL
          SELECT c.credit_id,c.artist_id,p.release_id,p.recording_id,p.placement_id,
            c.role,'placement',c.credit_name,c.position
          FROM placement_artist_credit c JOIN release_recording p ON p.placement_id=c.placement_id
          UNION ALL
          SELECT c.credit_id,c.artist_id,p.release_id,c.recording_id,p.placement_id,
            c.role,'recording',c.credit_name,c.position
          FROM recording_artist_credit c LEFT JOIN release_recording p ON p.recording_id=c.recording_id
        )
        """;
    public async ValueTask<CatalogArtistPage> QueryAsync(ArtistQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate();
        const string predicate = """
            (@q='' OR instr(lower(coalesce(a.name,'')),lower(@q))>0
              OR instr(lower(coalesce(a.sort_name,'')),lower(@q))>0
              OR EXISTS(SELECT 1 FROM artist_alias al WHERE al.artist_id=a.artist_id AND instr(lower(al.value),lower(@q))>0))
            AND ((@label IS NULL AND @role IS NULL) OR EXISTS(
              SELECT 1 FROM credits c WHERE c.artist_id=a.artist_id AND (@role IS NULL OR c.role=@role)
              AND (@label IS NULL OR EXISTS(SELECT 1 FROM release_label_link ll WHERE ll.release_id=c.release_id AND ll.label_id=@label))))
            """;
        using var command = _connection.CreateCommand(); command.Transaction = _transaction;
        command.Parameters.AddWithValue("@q", query.Search ?? "");
        command.Parameters.AddWithValue("@label", query.LabelId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@role", query.Role ?? (object)DBNull.Value);
        command.CommandText = ArtistCreditsCte + $" SELECT count(*) FROM artist a WHERE {predicate}";
        var total = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        command.CommandText = ArtistCreditsCte + $"""
            SELECT a.artist_id,a.name,a.artist_kind,a.identity_status FROM artist a WHERE {predicate}
            ORDER BY coalesce(a.name,'') COLLATE NOCASE,a.artist_id LIMIT @size OFFSET @offset
            """;
        command.Parameters.AddWithValue("@size", query.PageSize);
        command.Parameters.AddWithValue("@offset", ((long)query.Page - 1) * query.PageSize);
        var items = new List<CatalogArtistSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                items.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3), []));
        for (var index = 0; index < items.Count; index++)
        {
            command.CommandText = ArtistCreditsCte + """
                SELECT DISTINCT c.role FROM credits c WHERE c.artist_id=@artist
                AND (@label IS NULL OR EXISTS(SELECT 1 FROM release_label_link ll WHERE ll.release_id=c.release_id AND ll.label_id=@label))
                ORDER BY c.role
                """;
            command.Parameters.AddWithValue("@artist", items[index].Id);
            var roles = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) roles.Add(reader.GetString(0));
            items[index] = items[index] with { Roles = roles };
            command.Parameters.RemoveAt("@artist");
        }
        return new(items, total, query.Page, query.PageSize);
    }
    public async ValueTask<ArtistCreditPage> CreditsAsync(ArtistCreditQuery query, CancellationToken cancellationToken = default)
    {
        new ReleaseQuery(query.Page, query.PageSize).Validate();
        using var command = _connection.CreateCommand(); command.Transaction = _transaction;
        command.Parameters.AddWithValue("@artist", query.ArtistId);
        var predicate = query.Tracks ? "c.artist_id=@artist AND c.scope<>'release'" : "c.artist_id=@artist AND c.release_id IS NOT NULL";
        var source = query.Tracks ? "credits" : """
            (SELECT min(credit_id) AS credit_id,artist_id,release_id,NULL AS recording_id,NULL AS placement_id,
              role,scope,credit_name,min(position) AS position FROM credits GROUP BY artist_id,release_id,role,scope,credit_name)
            """;
        command.CommandText = ArtistCreditsCte + $" SELECT count(*) FROM {source} c WHERE {predicate}";
        var total = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        command.CommandText = ArtistCreditsCte + $"""
            SELECT c.credit_id,c.release_id,coalesce(r.title,r.name),c.recording_id,c.placement_id,
              coalesce(t.title,t.name),t.mix_name,c.role,c.scope,c.credit_name,c.position
            FROM {source} c LEFT JOIN release r ON r.release_id=c.release_id
            LEFT JOIN recording t ON t.recording_id=c.recording_id WHERE {predicate}
            ORDER BY coalesce(r.title,r.name,'') COLLATE NOCASE,c.release_id,c.scope,c.position,c.credit_id,c.placement_id
            LIMIT @size OFFSET @offset
            """;
        command.Parameters.AddWithValue("@size", query.PageSize);
        command.Parameters.AddWithValue("@offset", ((long)query.Page - 1) * query.PageSize);
        var items = new List<ArtistCreditListing>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(new(reader.GetString(0), Text(1), Text(2), Text(3), Text(4), Text(5), Text(6),
                reader.GetString(7), reader.GetString(8), Text(9), reader.IsDBNull(10) ? null : reader.GetInt32(10)));
        return new(items, total, query.Page, query.PageSize);
    }
    public async ValueTask<ArtistAppearancePage> AppearancesAsync(ArtistCreditQuery query, CancellationToken ct = default)
    {
        new ReleaseQuery(query.Page, query.PageSize).Validate();
        using var command = _connection.CreateCommand(); command.Transaction = _transaction;
        command.Parameters.AddWithValue("@artist", query.ArtistId);
        var groups = query.Tracks
            ? "SELECT release_id,recording_id,placement_id FROM credits WHERE artist_id=@artist AND scope<>'release' GROUP BY release_id,recording_id,placement_id"
            : "SELECT release_id,NULL AS recording_id,NULL AS placement_id FROM credits WHERE artist_id=@artist AND release_id IS NOT NULL GROUP BY release_id";
        command.CommandText = ArtistCreditsCte + $" SELECT count(*) FROM ({groups})";
        var total = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
        command.Parameters.AddWithValue("@size", query.PageSize);
        command.Parameters.AddWithValue("@offset", ((long)query.Page - 1) * query.PageSize);
        command.CommandText = ArtistCreditsCte + $"""
            SELECT g.release_id,coalesce(r.title,r.name),g.recording_id,g.placement_id,coalesce(t.title,t.name),t.mix_name
            FROM ({groups}) g LEFT JOIN release r ON r.release_id=g.release_id
            LEFT JOIN recording t ON t.recording_id=g.recording_id
            LEFT JOIN release_recording p ON p.placement_id=g.placement_id
            ORDER BY coalesce(r.title,r.name,'') COLLATE NOCASE,g.release_id,p.display_order,g.placement_id,g.recording_id
            LIMIT @size OFFSET @offset
            """;
        var items = new List<ArtistAppearanceListing>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                items.Add(new(Text(0), Text(1), Text(2), Text(3), Text(4), Text(5), null, [], []));
        }
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='release_artwork_link'";
        var hasArtwork = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        foreach (var index in Enumerable.Range(0, items.Count))
        {
            var item = items[index];
            command.Parameters.AddWithValue("@release", item.ReleaseId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@recording", item.RecordingId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@placement", item.PlacementId ?? (object)DBNull.Value);
            var match = query.Tracks
                ? "c.scope<>'release' AND c.release_id IS @release AND c.recording_id IS @recording AND c.placement_id IS @placement"
                : "c.release_id=@release";
            command.CommandText = ArtistCreditsCte + $"""
                SELECT DISTINCT c.role,c.scope,c.credit_name FROM credits c
                WHERE c.artist_id=@artist AND {match} ORDER BY c.role,c.scope,c.credit_name COLLATE NOCASE,c.credit_name
                """;
            var credits = new List<ArtistAppearanceCredit>();
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    credits.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            command.CommandText = """
                SELECT l.label_id,l.name,ll.is_primary FROM release_label_link ll JOIN label l ON l.label_id=ll.label_id
                WHERE ll.release_id=@release ORDER BY ll.is_primary DESC,l.name COLLATE NOCASE,l.label_id
                """;
            var labels = new List<ArtistAppearanceLabel>();
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    labels.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2) == 1));
            string? cover = null;
            if (hasArtwork)
            {
                command.CommandText = "SELECT asset_id FROM release_artwork_link WHERE release_id=@release AND role='cover' AND is_preferred=1";
                cover = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }
            items[index] = item with { Credits = credits, Labels = labels, CoverAssetId = cover };
            command.Parameters.RemoveAt("@release"); command.Parameters.RemoveAt("@recording"); command.Parameters.RemoveAt("@placement");
        }
        return new(items, total, query.Page, query.PageSize);
    }
}
