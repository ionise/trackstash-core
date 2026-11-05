using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteArtworkRepository(SqliteConnection connection, SqliteTransaction transaction) : IArtworkRepository
{
    public async ValueTask<ReleaseArtworkGallery?> GetReleaseGalleryAsync(string releaseId, CancellationToken ct = default)
    {
        using var version = Command("SELECT version FROM release WHERE release_id=@id", ("id", releaseId));
        if (await version.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string token) return null;
        using var command = Command("SELECT artwork_id,asset_id,role,caption,is_preferred FROM release_artwork_link WHERE release_id=@id ORDER BY position,artwork_id", ("id", releaseId));
        var items = new List<ArtworkEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4)));
        return new(releaseId, token, items);
    }
    public async ValueTask<ReleaseArtworkGalleryResult> ReplaceReleaseGalleryAsync(string releaseId, string expectedVersion,
        IReadOnlyList<ArtworkEntry> items, CancellationToken ct = default, ArtworkSourceIdentity? source = null)
    {
        ArtworkRules.ValidateGallery(items, release: true);
        ArtworkRules.ValidateSource(source);
        ReleaseArtworkGalleryResult result = new(LabelUpdateStatus.NotFound);
        await SqliteCatalogFoundationRepository.AtomicAsync(connection, transaction, async () =>
        {
            var before = await GetReleaseGalleryAsync(releaseId, ct).ConfigureAwait(false);
            if (before is null) return;
            if (before.Version != expectedVersion) { result = new(LabelUpdateStatus.Stale); return; }
            foreach (var item in items)
            {
                using var owner = Command("SELECT release_id FROM release_artwork_link WHERE artwork_id=@id", ("id", item.Id));
                if (await owner.ExecuteScalarAsync(ct).ConfigureAwait(false) is string other && other != releaseId)
                    throw new ArgumentException("Artwork entry belongs to another release.", nameof(items));
            }
            using var delete = Command("DELETE FROM release_artwork_link WHERE release_id=@id", ("id", releaseId));
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                using var insert = Command("INSERT INTO release_artwork_link VALUES(@entry,@id,@asset,@role,@caption,@position,@preferred)",
                    ("entry", item.Id), ("id", releaseId), ("asset", item.AssetId), ("role", item.Role),
                    ("caption", item.Caption), ("position", i), ("preferred", item.IsPreferred));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            using var audit = Command("INSERT INTO release_artwork_revision VALUES(@revision,@id,@now,@before,@after,@source)",
                ("revision", Guid.NewGuid().ToString("N")), ("id", releaseId), ("now", DateTimeOffset.UtcNow.ToString("O")),
                ("before", JsonSerializer.Serialize(before.Items)), ("after", JsonSerializer.Serialize(items)),
                ("source", source is null ? null : JsonSerializer.Serialize(source)));
            await audit.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            result = new(LabelUpdateStatus.Updated, await GetReleaseGalleryAsync(releaseId, ct).ConfigureAwait(false));
        }, ct).ConfigureAwait(false);
        return result;
    }
    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public async ValueTask<ArtworkAsset?> GetAssetAsync(string assetId, CancellationToken ct = default)
    {
        using var cmd = Command("SELECT * FROM artwork_asset WHERE asset_id=@id", ("id", assetId));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
            Text(6), Text(7), Text(8), reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10)));
        string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public async ValueTask AddAssetAsync(ArtworkAsset asset, CancellationToken ct = default)
    {
        ArtworkRules.ValidateAsset(asset);
        using var cmd = Command("""
            INSERT INTO artwork_asset(asset_id,content_hash,media_type,byte_length,width,height,original_filename,source_url,attribution,usage_rights,created_utc)
            VALUES (@id,@hash,@mime,@length,@width,@height,@filename,@source,@attribution,@rights,@created)
            """, ("id", asset.Id), ("hash", asset.ContentHash), ("mime", asset.MediaType), ("length", asset.ByteLength),
            ("width", asset.Width), ("height", asset.Height), ("filename", asset.OriginalFilename), ("source", asset.SourceUrl),
            ("attribution", asset.Attribution), ("rights", asset.UsageRights), ("created", asset.CreatedUtc.ToString("O")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<ArtworkGallery?> GetLabelGalleryAsync(string labelId, CancellationToken ct = default)
    {
        var labels = new SqliteLabelRepository(connection, transaction);
        var identity = await labels.ResolveIdentityAsync(labelId, ct).ConfigureAwait(false);
        if (identity.IsRetired) throw new RetiredLabelException(labelId, identity.CanonicalId!);
        using var version = Command("SELECT version FROM label WHERE label_id=@id", ("id", labelId));
        if (await version.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string token) return null;
        using var cmd = Command("SELECT artwork_id,asset_id,role,caption,is_preferred FROM label_artwork_link WHERE label_id=@id ORDER BY position,artwork_id", ("id", labelId));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<ArtworkEntry>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4)));
        return new(labelId, token, items);
    }

    public async ValueTask<ArtworkGalleryResult> ReplaceLabelGalleryAsync(string labelId, string expectedVersion,
        IReadOnlyList<ArtworkEntry> items, CancellationToken ct = default)
    {
        ArtworkRules.ValidateGallery(items);
        var before = await GetLabelGalleryAsync(labelId, ct).ConfigureAwait(false);
        if (before is null) return new(LabelUpdateStatus.NotFound);
        if (before.Version != expectedVersion) return new(LabelUpdateStatus.Stale);
        using var savepoint = Command("SAVEPOINT artwork_gallery");
        await savepoint.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        try
        {
            using var delete = Command("DELETE FROM label_artwork_link WHERE label_id=@id", ("id", labelId));
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            for (var position = 0; position < items.Count; position++)
            {
                var item = items[position];
                using var insert = Command("""
                    INSERT INTO label_artwork_link VALUES (@entry,@label,@asset,@role,@caption,@position,@preferred)
                    """, ("entry", item.Id), ("label", labelId), ("asset", item.AssetId), ("role", item.Role),
                    ("caption", item.Caption), ("position", position), ("preferred", item.IsPreferred));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            using var revision = Command("""
                INSERT INTO label_artwork_revision VALUES (@revision,@id,@id,@now,@before,@after)
                """, ("revision", Guid.NewGuid().ToString("N")), ("id", labelId), ("now", DateTimeOffset.UtcNow.ToString("O")),
                ("before", JsonSerializer.Serialize(before.Items)), ("after", JsonSerializer.Serialize(items)));
            await revision.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            var gallery = await GetLabelGalleryAsync(labelId, ct).ConfigureAwait(false);
            savepoint.CommandText = "RELEASE artwork_gallery";
            await savepoint.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return new(LabelUpdateStatus.Updated, gallery);
        }
        catch
        {
            savepoint.CommandText = "ROLLBACK TO artwork_gallery; RELEASE artwork_gallery";
            await savepoint.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
