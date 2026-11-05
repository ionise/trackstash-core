namespace TrackStash.Core.Sqlite;

internal static partial class Migrations
{
    private const string ReleaseArtwork = """
        CREATE TABLE release_artwork_link(
            artwork_id TEXT PRIMARY KEY NOT NULL,
            release_id TEXT NOT NULL REFERENCES release(release_id),
            asset_id TEXT NOT NULL REFERENCES artwork_asset(asset_id),
            role TEXT NOT NULL CHECK(role IN ('cover','image')),
            caption TEXT, position INTEGER NOT NULL CHECK(position>=0),
            is_preferred INTEGER NOT NULL CHECK(is_preferred IN (0,1)),
            UNIQUE(release_id,position)
        );
        CREATE UNIQUE INDEX idx_release_artwork_preferred ON release_artwork_link(release_id) WHERE is_preferred=1;
        CREATE TABLE release_artwork_revision(
            revision_id TEXT PRIMARY KEY NOT NULL,
            release_id TEXT NOT NULL REFERENCES release(release_id),
            recorded_utc TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL, source_json TEXT
        );
        CREATE TRIGGER release_artwork_revision_no_update BEFORE UPDATE ON release_artwork_revision
        BEGIN SELECT RAISE(ABORT,'Release artwork history is immutable'); END;
        CREATE TRIGGER release_artwork_revision_no_delete BEFORE DELETE ON release_artwork_revision
        BEGIN SELECT RAISE(ABORT,'Release artwork history is permanent'); END;
        CREATE TRIGGER release_artwork_insert AFTER INSERT ON release_artwork_link
        BEGIN UPDATE release SET version=lower(hex(randomblob(16))) WHERE release_id=NEW.release_id; END;
        CREATE TRIGGER release_artwork_update AFTER UPDATE ON release_artwork_link
        BEGIN UPDATE release SET version=lower(hex(randomblob(16))) WHERE release_id IN (OLD.release_id,NEW.release_id); END;
        CREATE TRIGGER release_artwork_delete AFTER DELETE ON release_artwork_link
        BEGIN UPDATE release SET version=lower(hex(randomblob(16))) WHERE release_id=OLD.release_id; END;
        CREATE TRIGGER release_artwork_revision_insert AFTER INSERT ON release_artwork_revision
        BEGIN UPDATE release SET version=lower(hex(randomblob(16))) WHERE release_id=NEW.release_id; END;
        """;
}
