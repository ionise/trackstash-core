namespace TrackStash.Core.Sqlite;

internal static partial class Migrations
{
    private static string ReleaseArtwork => EntityArtwork("release", "cover");
    private static string EntityArtwork(string entity, string role) => $$"""
        CREATE TABLE {{entity}}_artwork_link(
            artwork_id TEXT PRIMARY KEY NOT NULL,
            {{entity}}_id TEXT NOT NULL REFERENCES {{entity}}({{entity}}_id),
            asset_id TEXT NOT NULL REFERENCES artwork_asset(asset_id),
            role TEXT NOT NULL CHECK(role IN ('{{role}}','image')),
            caption TEXT, position INTEGER NOT NULL CHECK(position>=0),
            is_preferred INTEGER NOT NULL CHECK(is_preferred IN (0,1)),
            UNIQUE({{entity}}_id,position)
        );
        CREATE UNIQUE INDEX idx_{{entity}}_artwork_preferred ON {{entity}}_artwork_link({{entity}}_id) WHERE is_preferred=1;
        CREATE TABLE {{entity}}_artwork_revision(
            revision_id TEXT PRIMARY KEY NOT NULL,
            {{entity}}_id TEXT NOT NULL REFERENCES {{entity}}({{entity}}_id),
            recorded_utc TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL, source_json TEXT
        );
        CREATE TRIGGER {{entity}}_artwork_revision_no_update BEFORE UPDATE ON {{entity}}_artwork_revision
        BEGIN SELECT RAISE(ABORT,'{{(entity == "release" ? "Release" : "Artist")}} artwork history is immutable'); END;
        CREATE TRIGGER {{entity}}_artwork_revision_no_delete BEFORE DELETE ON {{entity}}_artwork_revision
        BEGIN SELECT RAISE(ABORT,'{{(entity == "release" ? "Release" : "Artist")}} artwork history is permanent'); END;
        CREATE TRIGGER {{entity}}_artwork_insert AFTER INSERT ON {{entity}}_artwork_link
        BEGIN UPDATE {{entity}} SET version=lower(hex(randomblob(16))) WHERE {{entity}}_id=NEW.{{entity}}_id; END;
        CREATE TRIGGER {{entity}}_artwork_update AFTER UPDATE ON {{entity}}_artwork_link
        BEGIN UPDATE {{entity}} SET version=lower(hex(randomblob(16))) WHERE {{entity}}_id IN (OLD.{{entity}}_id,NEW.{{entity}}_id); END;
        CREATE TRIGGER {{entity}}_artwork_delete AFTER DELETE ON {{entity}}_artwork_link
        BEGIN UPDATE {{entity}} SET version=lower(hex(randomblob(16))) WHERE {{entity}}_id=OLD.{{entity}}_id; END;
        CREATE TRIGGER {{entity}}_artwork_revision_insert AFTER INSERT ON {{entity}}_artwork_revision
        BEGIN UPDATE {{entity}} SET version=lower(hex(randomblob(16))) WHERE {{entity}}_id=NEW.{{entity}}_id; END;
        """;
}
