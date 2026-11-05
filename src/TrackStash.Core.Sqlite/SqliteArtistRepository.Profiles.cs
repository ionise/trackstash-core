using System.Text.Json;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed partial class SqliteArtistRepository
{
    public async ValueTask<ArtistProfileSnapshot?> GetProfileAsync(string id, CancellationToken ct = default)
    {
        using var command = _connection.CreateCommand(); command.Transaction = _transaction;
        command.CommandText = "SELECT a.version,p.profile_json FROM artist a LEFT JOIN artist_profile p ON p.artist_id=a.artist_id WHERE a.artist_id=@id";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var profile = reader.IsDBNull(1) ? new ArtistProfile() : JsonSerializer.Deserialize<ArtistProfile>(reader.GetString(1))
            ?? throw new InvalidDataException("Artist profile JSON is invalid.");
        profile.Validate();
        return new(id, reader.GetString(0), profile);
    }
    public async ValueTask<ArtistProfileResult> SaveProfileAsync(string id, string version, ArtistProfile profile, CancellationToken ct = default)
    {
        profile.Validate();
        ArtistProfileResult result = new(LabelUpdateStatus.NotFound);
        await SqliteCatalogFoundationRepository.AtomicAsync(_connection, _transaction, async () =>
        {
            var before = await GetProfileAsync(id, ct).ConfigureAwait(false);
            if (before is null) return;
            if (before.Version != version) { result = new(LabelUpdateStatus.Stale); return; }
            if (JsonSerializer.Serialize(before.Profile) == JsonSerializer.Serialize(profile)) { result = new(LabelUpdateStatus.Updated, before); return; }
            using var command = _connection.CreateCommand(); command.Transaction = _transaction;
            command.Parameters.AddWithValue("@id", id); command.Parameters.AddWithValue("@after", JsonSerializer.Serialize(profile));
            command.CommandText = "INSERT INTO artist_profile VALUES(@id,@after) ON CONFLICT(artist_id) DO UPDATE SET profile_json=@after";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            command.Parameters.AddWithValue("@revision", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@before", JsonSerializer.Serialize(before.Profile));
            command.CommandText = "INSERT INTO artist_profile_revision VALUES(@revision,@id,@now,@before,@after)";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            result = new(LabelUpdateStatus.Updated, await GetProfileAsync(id, ct).ConfigureAwait(false));
        }, ct).ConfigureAwait(false);
        return result;
    }
}
