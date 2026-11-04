using System.Text.Json;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed partial class SqliteLabelRepository
{
    private async ValueTask<LabelProfile> ReadProfileAsync(string labelId, CancellationToken ct)
    {
        var profile = new LabelProfile();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = _transaction;
            command.CommandText = "SELECT description,country_code,city,founded_year,lifecycle_status,notes,favourite,review_status FROM label_profile WHERE label_id=@id";
            command.Parameters.AddWithValue("@id", labelId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                profile = new()
                {
                    Description = reader.IsDBNull(0) ? null : reader.GetString(0),
                    CountryCode = reader.IsDBNull(1) ? null : reader.GetString(1),
                    City = reader.IsDBNull(2) ? null : reader.GetString(2),
                    FoundedYear = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    LifecycleStatus = reader.GetString(4),
                    Notes = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Favourite = reader.GetInt32(6) != 0,
                    ReviewStatus = reader.GetString(7),
                };
        }
        var links = new List<LabelWebLink>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = _transaction;
            command.CommandText = "SELECT kind,url,title,source_url FROM label_web_link WHERE label_id=@id ORDER BY url";
            command.Parameters.AddWithValue("@id", labelId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                links.Add(new()
                {
                    Kind = reader.GetString(0), Url = reader.GetString(1),
                    Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                    SourceUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
        }
        return profile with { Links = links.ToArray() };
    }

    internal async ValueTask WriteProfileAsync(string labelId, LabelProfile profile, CancellationToken ct)
    {
        profile.Validate();
        await RejectRetiredAsync(labelId, ct).ConfigureAwait(false);
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            INSERT INTO label_profile(label_id,description,country_code,city,founded_year,lifecycle_status,notes,favourite,review_status)
            VALUES (@id,@description,@country,@city,@year,@status,@notes,@favourite,@review)
            ON CONFLICT(label_id) DO UPDATE SET description=excluded.description,country_code=excluded.country_code,
                city=excluded.city,founded_year=excluded.founded_year,lifecycle_status=excluded.lifecycle_status,
                notes=excluded.notes,favourite=excluded.favourite,review_status=excluded.review_status;
            DELETE FROM label_web_link WHERE label_id=@id;
            """;
        command.Parameters.AddWithValue("@id", labelId);
        command.Parameters.AddWithValue("@description", (object?)profile.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("@country", (object?)profile.CountryCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@city", (object?)profile.City ?? DBNull.Value);
        command.Parameters.AddWithValue("@year", (object?)profile.FoundedYear ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", profile.LifecycleStatus);
        command.Parameters.AddWithValue("@notes", (object?)profile.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("@favourite", profile.Favourite);
        command.Parameters.AddWithValue("@review", profile.ReviewStatus);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        foreach (var link in profile.Links)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = _transaction;
            insert.CommandText = "INSERT INTO label_web_link(label_id,url,kind,title,source_url) VALUES (@id,@url,@kind,@title,@source)";
            insert.Parameters.AddWithValue("@id", labelId);
            insert.Parameters.AddWithValue("@url", link.Url);
            insert.Parameters.AddWithValue("@kind", link.Kind);
            insert.Parameters.AddWithValue("@title", (object?)link.Title ?? DBNull.Value);
            insert.Parameters.AddWithValue("@source", (object?)link.SourceUrl ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async ValueTask RecordProfileRevisionAsync(string labelId, LabelProfile before, LabelProfile after, CancellationToken ct)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            INSERT INTO label_profile_revision(revision_id,label_id,original_label_id,recorded_utc,actor,before_json,after_json)
            VALUES (@revision,@id,@id,@now,'manual',@before,@after)
            """;
        command.Parameters.AddWithValue("@revision", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@id", labelId);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("@before", JsonSerializer.Serialize(before));
        command.Parameters.AddWithValue("@after", JsonSerializer.Serialize(after));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
