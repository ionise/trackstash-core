using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed partial class SqliteRecordingRepository
{
    private const int MaxSearchTerms = 8;

    private static string ArtistNameMatches(string term) => $"""
        EXISTS(SELECT 1 FROM placement_artist_credit c JOIN artist a ON a.artist_id=c.artist_id WHERE c.placement_id=b.placement_id
          AND (instr(lower(coalesce(c.credit_name,'')),{term})>0 OR instr(lower(coalesce(a.name,'')),{term})>0))
        OR EXISTS(SELECT 1 FROM recording_artist_credit c JOIN artist a ON a.artist_id=c.artist_id WHERE c.recording_id=b.recording_id
          AND (instr(lower(coalesce(c.credit_name,'')),{term})>0 OR instr(lower(coalesce(a.name,'')),{term})>0))
        """;

    private const string PrimaryArtistKey = """
        coalesce(
          (SELECT coalesce(c.credit_name,a.name) FROM placement_artist_credit c JOIN artist a ON a.artist_id=c.artist_id
            WHERE c.placement_id=b.placement_id AND c.role='primary' ORDER BY c.position IS NULL,c.position,c.credit_id LIMIT 1),
          (SELECT coalesce(c.credit_name,a.name) FROM recording_artist_credit c JOIN artist a ON a.artist_id=c.artist_id
            WHERE c.recording_id=b.recording_id AND c.role='primary' ORDER BY c.position IS NULL,c.position,c.credit_id LIMIT 1),'')
        """;

    public async ValueTask<TrackListingPage> QueryTracksAsync(TrackQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate();
        using var command = _connection.CreateCommand(); command.Transaction = _transaction;
        object Value(string? value) => value ?? (object)DBNull.Value;
        command.Parameters.AddWithValue("@release", Value(query.ReleaseId));
        command.Parameters.AddWithValue("@recording", Value(query.RecordingId));
        command.Parameters.AddWithValue("@label", Value(query.LabelId));
        command.Parameters.AddWithValue("@artist", Value(query.ArtistId));
        command.Parameters.AddWithValue("@role", Value(query.Role));
        var predicates = new List<string>
        {
            "(@release IS NULL OR b.release_id=@release)",
            "(@recording IS NULL OR b.recording_id=@recording)",
            "(@label IS NULL OR EXISTS(SELECT 1 FROM release_label_link ll WHERE ll.release_id=b.release_id AND ll.label_id=@label))",
            """
            (@artist IS NULL
              OR EXISTS(SELECT 1 FROM placement_artist_credit c WHERE c.placement_id=b.placement_id AND c.artist_id=@artist AND (@role IS NULL OR c.role=@role))
              OR EXISTS(SELECT 1 FROM recording_artist_credit c WHERE c.recording_id=b.recording_id AND c.artist_id=@artist AND (@role IS NULL OR c.role=@role)))
            """,
        };
        // Every search word must match the title, mix, ISRC, release title or a credited Artist name.
        var terms = (query.Search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(MaxSearchTerms).ToArray();
        foreach (var (term, index) in terms.Select((term, index) => (term, index)))
        {
            var name = $"@q{index}";
            command.Parameters.AddWithValue(name, term.ToLowerInvariant());
            predicates.Add($"""
                (instr(lower(coalesce(t.title,t.name,'')),{name})>0 OR instr(lower(coalesce(t.mix_name,'')),{name})>0
                  OR lower(coalesce(t.isrc,''))={name} OR instr(lower(coalesce(r.title,r.name,'')),{name})>0
                  OR {ArtistNameMatches(name)})
                """);
        }
        var sort = query.EffectiveSort;
        var direction = query.EffectiveDescending ? "DESC" : "ASC";
        var source = $"""
            WITH base AS (
              SELECT p.placement_id,p.recording_id,p.release_id,p.display_order,p.source_position,p.order_source,p.disc_number,p.track_number
              FROM release_recording p
              UNION ALL
              SELECT NULL,t.recording_id,NULL,NULL,NULL,NULL,NULL,NULL FROM recording t
              WHERE NOT EXISTS(SELECT 1 FROM release_recording p WHERE p.recording_id=t.recording_id)
            ),
            matched AS (
              SELECT b.*,coalesce(t.title,t.name) AS title,t.mix_name,t.isrc,t.duration_ms,
                coalesce(r.title,r.name) AS release_title,r.publish_date,r.new_release_date,
                coalesce(r.publish_date,r.new_release_date) AS release_date,
                (SELECT count(DISTINCT x.release_id) FROM release_recording x WHERE x.recording_id=b.recording_id) AS release_count,
                {(sort == "artist" ? PrimaryArtistKey : "''")} AS artist_key
              FROM base b JOIN recording t ON t.recording_id=b.recording_id LEFT JOIN release r ON r.release_id=b.release_id
              WHERE {string.Join("\n AND ", predicates)}
            ),
            rows AS (
              {(query.GroupByRecording
                ? """
                  SELECT * FROM (SELECT m.*,row_number() OVER (PARTITION BY m.recording_id
                    ORDER BY m.release_date IS NULL,m.release_date DESC,m.release_id,m.placement_id) AS representative FROM matched m)
                  WHERE representative=1
                  """
                : "SELECT * FROM matched")}
            )
            """;
        var order = sort switch
        {
            "title" => $"title IS NULL,title COLLATE NOCASE {direction},mix_name COLLATE NOCASE {direction},release_date IS NULL,release_date DESC",
            "artist" => $"artist_key='',artist_key COLLATE NOCASE {direction},title COLLATE NOCASE,release_date IS NULL,release_date DESC",
            "release" => $"release_title IS NULL,release_title COLLATE NOCASE {direction},release_id,display_order",
            "order" => $"display_order IS NULL,display_order {direction}",
            _ => $"release_date IS NULL,release_date {direction},release_title COLLATE NOCASE,release_id,display_order,title COLLATE NOCASE",
        };
        command.CommandText = source + " SELECT count(*) FROM rows";
        var total = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        command.Parameters.AddWithValue("@size", query.PageSize);
        command.Parameters.AddWithValue("@offset", ((long)query.Page - 1) * query.PageSize);
        command.CommandText = source + $"""
            SELECT placement_id,recording_id,title,mix_name,isrc,duration_ms,release_id,release_title,publish_date,new_release_date,
              display_order,source_position,order_source,disc_number,track_number,release_count
            FROM rows ORDER BY {order},recording_id,placement_id LIMIT @size OFFSET @offset
            """;
        var items = new List<TrackListing>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        int? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(new(Text(0), reader.GetString(1), Text(2), Text(3), Text(4), reader.IsDBNull(5) ? null : reader.GetInt64(5),
                Text(6), Text(7), Text(8), Text(9), Number(10), Number(11), Text(12), Number(13), Number(14), reader.GetInt32(15)));
        return new(items, total, query.Page, query.PageSize);
    }
}
