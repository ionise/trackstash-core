namespace TrackStash.Core.Sqlite;

internal static partial class Migrations
{
    private static string ArtistProfiles()
    {
        var artwork = EntityArtwork("artist", "portrait");
        return artwork + """

            CREATE TABLE artist_profile(
              artist_id TEXT PRIMARY KEY NOT NULL REFERENCES artist(artist_id),
              profile_json TEXT NOT NULL
            );
            CREATE TABLE artist_profile_revision(
              revision_id TEXT PRIMARY KEY NOT NULL,
              artist_id TEXT NOT NULL REFERENCES artist(artist_id),
              recorded_utc TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL
            );
            CREATE TRIGGER artist_profile_history_no_update BEFORE UPDATE ON artist_profile_revision
            BEGIN SELECT RAISE(ABORT,'Artist profile history is immutable'); END;
            CREATE TRIGGER artist_profile_history_no_delete BEFORE DELETE ON artist_profile_revision
            BEGIN SELECT RAISE(ABORT,'Artist profile history is permanent'); END;
            CREATE TRIGGER artist_profile_insert AFTER INSERT ON artist_profile
            BEGIN UPDATE artist SET version=lower(hex(randomblob(16))) WHERE artist_id=NEW.artist_id; END;
            CREATE TRIGGER artist_profile_update AFTER UPDATE ON artist_profile
            BEGIN UPDATE artist SET version=lower(hex(randomblob(16))) WHERE artist_id IN (OLD.artist_id,NEW.artist_id); END;
            CREATE TRIGGER artist_profile_delete AFTER DELETE ON artist_profile
            BEGIN UPDATE artist SET version=lower(hex(randomblob(16))) WHERE artist_id=OLD.artist_id; END;
            CREATE TRIGGER artist_profile_history_insert AFTER INSERT ON artist_profile_revision
            BEGIN UPDATE artist SET version=lower(hex(randomblob(16))) WHERE artist_id=NEW.artist_id; END;
            """;
    }
}
