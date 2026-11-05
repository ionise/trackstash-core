using System.Reflection;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;

namespace TrackStash.Core.Tests;

internal static class LegacyCatalogSchema
{
    internal static string CanonicalTablesSql
    {
        get
        {
            var initial = (string)typeof(SqliteStorageProvider).Assembly
                .GetType("TrackStash.Core.Sqlite.Migrations")!.GetField("InitialSchema", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
            var start = initial.IndexOf("CREATE TABLE IF NOT EXISTS artist (", StringComparison.Ordinal);
            var end = initial.IndexOf("CREATE TABLE IF NOT EXISTS media_file (", StringComparison.Ordinal);
            return initial[start..end];
        }
    }

    internal static async Task RevertEmptyFoundationsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Foreign Keys=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM artist)+(SELECT count(*) FROM release)+(SELECT count(*) FROM recording)";
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 0)
            throw new InvalidOperationException("Legacy fixture reset requires empty canonical entities.");
        var triggers = new List<string>();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND (name LIKE 'foundation_%' OR name LIKE 'placement_%' OR name LIKE 'artist_%' OR name LIKE 'release_%' OR name LIKE 'recording_%' OR name LIKE 'import_stamp_%' OR name LIKE 'embedding_invalidate_%')";
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) triggers.Add(reader.GetString(0));
        foreach (var trigger in triggers)
        {
            command.CommandText = $"DROP TRIGGER \"{trigger.Replace("\"", "\"\"")}\"";
            await command.ExecuteNonQueryAsync();
        }
        command.CommandText = """
            DROP TABLE IF EXISTS release_artwork_link; DROP TABLE IF EXISTS release_artwork_revision;
            DROP TABLE release_import_outcome; DROP TABLE release_import_binding; DROP TABLE release_import_supersession; DROP TABLE release_import_preview;
            DROP TABLE release_import_state; DROP TABLE catalog_embedding_outbox; DROP TABLE catalog_embedding_source;
            DROP TABLE placement_external_ref; DROP TABLE placement_artist_credit;
            DROP TABLE release_recording; DROP TABLE release_artist_credit; DROP TABLE recording_artist_credit;
            DROP TABLE catalog_foundation_revision;
            ALTER TABLE artist DROP COLUMN version;
            ALTER TABLE artist DROP COLUMN artist_kind;
            ALTER TABLE artist DROP COLUMN identity_status;
            ALTER TABLE release DROP COLUMN version;
            ALTER TABLE release DROP COLUMN catalog_number;
            ALTER TABLE release DROP COLUMN upc;
            ALTER TABLE release DROP COLUMN publish_date;
            ALTER TABLE release DROP COLUMN new_release_date;
            ALTER TABLE recording DROP COLUMN version;
            ALTER TABLE recording DROP COLUMN duration_ms;
            DELETE FROM schema_migration WHERE version>=9;
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = CanonicalTablesSql;
        await command.ExecuteNonQueryAsync();
    }
}
