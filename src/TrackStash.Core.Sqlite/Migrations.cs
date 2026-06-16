namespace TrackStash.Core.Sqlite;

internal sealed record SchemaMigration(int Version, string Name, string Sql);

internal static class Migrations
{
    public static IReadOnlyList<SchemaMigration> All { get; } =
    [
        new SchemaMigration(1, "initial-schema", InitialSchema),
    ];

    private const string InitialSchema = """
        CREATE TABLE IF NOT EXISTS label (
            label_id            TEXT NOT NULL PRIMARY KEY,
            name                TEXT,
            normalized_name     TEXT,
            sort_name           TEXT,
            source_payload_json TEXT,
            created_utc         TEXT,
            updated_utc         TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_label_normalized_name ON label (normalized_name);

        CREATE TABLE IF NOT EXISTS label_external_ref (
            label_id       TEXT NOT NULL REFERENCES label(label_id),
            source         TEXT NOT NULL,
            external_id    TEXT NOT NULL,
            is_primary     INTEGER NOT NULL DEFAULT 0,
            last_seen_utc  TEXT,
            payload_json   TEXT,
            PRIMARY KEY (label_id, source, external_id)
        );

        CREATE INDEX IF NOT EXISTS idx_label_external_ref_source ON label_external_ref (source, external_id);

        CREATE TABLE IF NOT EXISTS label_alias (
            label_id         TEXT    NOT NULL REFERENCES label(label_id),
            value            TEXT    NOT NULL,
            normalized_value TEXT,
            is_primary       INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (label_id, value)
        );

        CREATE TABLE IF NOT EXISTS artist (
            artist_id           TEXT NOT NULL PRIMARY KEY,
            name                TEXT,
            normalized_name     TEXT,
            sort_name           TEXT,
            source_payload_json TEXT,
            created_utc         TEXT,
            updated_utc         TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_artist_normalized_name ON artist (normalized_name);

        CREATE TABLE IF NOT EXISTS artist_external_ref (
            artist_id      TEXT NOT NULL REFERENCES artist(artist_id),
            source         TEXT NOT NULL,
            external_id    TEXT NOT NULL,
            is_primary     INTEGER NOT NULL DEFAULT 0,
            last_seen_utc  TEXT,
            payload_json   TEXT,
            PRIMARY KEY (artist_id, source, external_id)
        );

        CREATE INDEX IF NOT EXISTS idx_artist_external_ref_source ON artist_external_ref (source, external_id);

        CREATE TABLE IF NOT EXISTS artist_alias (
            artist_id        TEXT    NOT NULL REFERENCES artist(artist_id),
            value            TEXT    NOT NULL,
            normalized_value TEXT,
            is_primary       INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (artist_id, value)
        );

        CREATE TABLE IF NOT EXISTS release (
            release_id          TEXT NOT NULL PRIMARY KEY,
            name                TEXT,
            normalized_name     TEXT,
            title               TEXT,
            source_payload_json TEXT,
            created_utc         TEXT,
            updated_utc         TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_release_normalized_name ON release (normalized_name);

        CREATE TABLE IF NOT EXISTS release_external_ref (
            release_id     TEXT NOT NULL REFERENCES release(release_id),
            source         TEXT NOT NULL,
            external_id    TEXT NOT NULL,
            is_primary     INTEGER NOT NULL DEFAULT 0,
            last_seen_utc  TEXT,
            payload_json   TEXT,
            PRIMARY KEY (release_id, source, external_id)
        );

        CREATE INDEX IF NOT EXISTS idx_release_external_ref_source ON release_external_ref (source, external_id);

        CREATE TABLE IF NOT EXISTS release_artist_credit (
            release_id   TEXT NOT NULL REFERENCES release(release_id),
            artist_id    TEXT NOT NULL REFERENCES artist(artist_id),
            credit_name  TEXT,
            position     INTEGER,
            PRIMARY KEY (release_id, artist_id)
        );

        CREATE TABLE IF NOT EXISTS release_label_link (
            release_id TEXT    NOT NULL REFERENCES release(release_id),
            label_id   TEXT    NOT NULL REFERENCES label(label_id),
            is_primary INTEGER NOT NULL DEFAULT 0,
            role       TEXT,
            PRIMARY KEY (release_id, label_id)
        );

        CREATE TABLE IF NOT EXISTS recording (
            recording_id        TEXT NOT NULL PRIMARY KEY,
            name                TEXT,
            normalized_name     TEXT,
            title               TEXT,
            mix_name            TEXT,
            isrc                TEXT,
            source_payload_json TEXT,
            created_utc         TEXT,
            updated_utc         TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_recording_normalized_name ON recording (normalized_name);
        CREATE INDEX IF NOT EXISTS idx_recording_isrc ON recording (isrc);

        CREATE TABLE IF NOT EXISTS recording_external_ref (
            recording_id   TEXT NOT NULL REFERENCES recording(recording_id),
            source         TEXT NOT NULL,
            external_id    TEXT NOT NULL,
            is_primary     INTEGER NOT NULL DEFAULT 0,
            last_seen_utc  TEXT,
            payload_json   TEXT,
            PRIMARY KEY (recording_id, source, external_id)
        );

        CREATE INDEX IF NOT EXISTS idx_recording_external_ref_source ON recording_external_ref (source, external_id);

        CREATE TABLE IF NOT EXISTS recording_artist_credit (
            recording_id TEXT NOT NULL REFERENCES recording(recording_id),
            artist_id    TEXT NOT NULL REFERENCES artist(artist_id),
            credit_name  TEXT,
            role         TEXT,
            position     INTEGER,
            PRIMARY KEY (recording_id, artist_id)
        );

        CREATE TABLE IF NOT EXISTS release_recording (
            release_id   TEXT    NOT NULL REFERENCES release(release_id),
            recording_id TEXT    NOT NULL REFERENCES recording(recording_id),
            disc_number  INTEGER,
            track_number INTEGER,
            PRIMARY KEY (release_id, recording_id)
        );

        CREATE TABLE IF NOT EXISTS media_file (
            media_file_id       TEXT NOT NULL PRIMARY KEY,
            path                TEXT NOT NULL,
            normalized_path     TEXT,
            content_hash        TEXT,
            metadata_json       TEXT,
            fingerprint         TEXT,
            source_payload_json TEXT,
            created_utc         TEXT,
            updated_utc         TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_media_file_path ON media_file (path);
        CREATE INDEX IF NOT EXISTS idx_media_file_content_hash ON media_file (content_hash);

        CREATE TABLE IF NOT EXISTS media_file_recording_match (
            media_file_id  TEXT    NOT NULL PRIMARY KEY REFERENCES media_file(media_file_id),
            recording_id   TEXT    NOT NULL REFERENCES recording(recording_id),
            override_state INTEGER NOT NULL DEFAULT 0,
            score          REAL    NOT NULL DEFAULT 0,
            confidence     REAL    NOT NULL DEFAULT 0,
            evidence_json  TEXT,
            created_utc    TEXT,
            updated_utc    TEXT
        );

        CREATE TABLE IF NOT EXISTS media_file_recording_candidate (
            media_file_id  TEXT    NOT NULL REFERENCES media_file(media_file_id),
            recording_id   TEXT    NOT NULL REFERENCES recording(recording_id),
            rank           INTEGER NOT NULL,
            score          REAL    NOT NULL DEFAULT 0,
            confidence     REAL    NOT NULL DEFAULT 0,
            evidence_json  TEXT,
            PRIMARY KEY (media_file_id, recording_id)
        );

        CREATE TABLE IF NOT EXISTS embedding_document (
            entity_id           TEXT    NOT NULL,
            entity_type         TEXT    NOT NULL,
            model_name          TEXT    NOT NULL,
            model_version       TEXT    NOT NULL,
            dimensions          INTEGER NOT NULL,
            document_hash       TEXT    NOT NULL,
            document_text       TEXT,
            vector_data         BLOB,
            source_payload_json TEXT,
            created_utc         TEXT,
            updated_utc         TEXT,
            PRIMARY KEY (entity_id, model_name, model_version)
        );

        CREATE INDEX IF NOT EXISTS idx_embedding_document_hash ON embedding_document (document_hash);
        """;
}
