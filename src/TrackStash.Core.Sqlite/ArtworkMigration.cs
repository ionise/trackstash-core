namespace TrackStash.Core.Sqlite;

internal static partial class Migrations
{
    private const string SharedArtwork = """
        CREATE TABLE artwork_asset (
            asset_id TEXT PRIMARY KEY NOT NULL,
            content_hash TEXT NOT NULL,
            media_type TEXT NOT NULL,
            byte_length INTEGER NOT NULL CHECK(byte_length >= 0),
            width INTEGER,
            height INTEGER,
            original_filename TEXT,
            source_url TEXT,
            attribution TEXT,
            usage_rights TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            legacy_artwork_id TEXT UNIQUE,
            legacy_content_hash TEXT
        );
        CREATE TABLE label_artwork_link (
            artwork_id TEXT PRIMARY KEY NOT NULL,
            label_id TEXT NOT NULL REFERENCES label(label_id),
            asset_id TEXT NOT NULL REFERENCES artwork_asset(asset_id),
            role TEXT NOT NULL CHECK(role IN ('logo','image')),
            caption TEXT,
            position INTEGER NOT NULL CHECK(position >= 0),
            is_preferred INTEGER NOT NULL CHECK(is_preferred IN (0,1)),
            UNIQUE(label_id,position)
        );
        CREATE UNIQUE INDEX idx_label_artwork_preferred ON label_artwork_link(label_id) WHERE is_preferred=1;
        CREATE TABLE label_artwork_revision (
            revision_id TEXT PRIMARY KEY NOT NULL,
            label_id TEXT NOT NULL REFERENCES label(label_id),
            original_label_id TEXT NOT NULL,
            recorded_utc TEXT NOT NULL,
            before_json TEXT NOT NULL,
            after_json TEXT NOT NULL
        );
        CREATE INDEX idx_label_artwork_revision ON label_artwork_revision(label_id,recorded_utc);
        INSERT INTO artwork_asset(asset_id,content_hash,media_type,byte_length,source_url,attribution,usage_rights,created_utc,legacy_artwork_id,legacy_content_hash)
        SELECT lower(hex(randomblob(16))),content_hash,mime_type,length(content),source_url,attribution,usage_rights,created_utc,artwork_id,content_hash
        FROM label_artwork;
        INSERT INTO label_artwork_link(artwork_id,label_id,asset_id,role,caption,position,is_preferred)
        SELECT old.artwork_id,old.label_id,asset.asset_id,old.role,NULL,
            row_number() OVER (PARTITION BY old.label_id ORDER BY old.created_utc,old.artwork_id)-1,0
        FROM label_artwork old JOIN artwork_asset asset ON asset.legacy_artwork_id=old.artwork_id;
        CREATE TRIGGER artwork_asset_no_delete BEFORE DELETE ON artwork_asset
        BEGIN SELECT RAISE(ABORT,'Artwork assets are retained for references and history'); END;
        CREATE TRIGGER artwork_link_no_retired_insert BEFORE INSERT ON label_artwork_link
        WHEN EXISTS(SELECT 1 FROM retired_label WHERE retired_id=NEW.label_id)
        BEGIN SELECT RAISE(ABORT,'Label artwork identity is retired'); END;
        CREATE TRIGGER artwork_link_no_retired_update BEFORE UPDATE ON label_artwork_link
        WHEN EXISTS(SELECT 1 FROM retired_label WHERE retired_id=NEW.label_id)
        BEGIN SELECT RAISE(ABORT,'Label artwork identity is retired'); END;
        CREATE TRIGGER artwork_revision_no_retired_insert BEFORE INSERT ON label_artwork_revision
        WHEN EXISTS(SELECT 1 FROM retired_label WHERE retired_id=NEW.label_id)
        BEGIN SELECT RAISE(ABORT,'Label artwork identity is retired'); END;
        CREATE TRIGGER artwork_revision_no_retired_update BEFORE UPDATE ON label_artwork_revision
        WHEN EXISTS(SELECT 1 FROM retired_label WHERE retired_id=NEW.label_id)
        BEGIN SELECT RAISE(ABORT,'Label artwork identity is retired'); END;
        CREATE TRIGGER artwork_revision_immutable BEFORE UPDATE OF revision_id,original_label_id,recorded_utc,before_json,after_json ON label_artwork_revision
        BEGIN SELECT RAISE(ABORT,'Artwork revision content is immutable'); END;
        CREATE TRIGGER artwork_revision_no_delete BEFORE DELETE ON label_artwork_revision
        BEGIN SELECT RAISE(ABORT,'Artwork revisions are permanent'); END;
        CREATE TRIGGER artwork_link_version_insert AFTER INSERT ON label_artwork_link
        BEGIN UPDATE label SET version=lower(hex(randomblob(16))) WHERE label_id=NEW.label_id; END;
        CREATE TRIGGER artwork_link_version_update AFTER UPDATE ON label_artwork_link
        BEGIN UPDATE label SET version=lower(hex(randomblob(16))) WHERE label_id IN (OLD.label_id,NEW.label_id); END;
        CREATE TRIGGER artwork_link_version_delete AFTER DELETE ON label_artwork_link
        BEGIN UPDATE label SET version=lower(hex(randomblob(16))) WHERE label_id=OLD.label_id; END;
        CREATE TRIGGER artwork_revision_version_insert AFTER INSERT ON label_artwork_revision
        BEGIN UPDATE label SET version=lower(hex(randomblob(16))) WHERE label_id=NEW.label_id; END;
        CREATE TRIGGER artwork_revision_version_update AFTER UPDATE ON label_artwork_revision
        BEGIN UPDATE label SET version=lower(hex(randomblob(16))) WHERE label_id IN (OLD.label_id,NEW.label_id); END;
        """;
}
