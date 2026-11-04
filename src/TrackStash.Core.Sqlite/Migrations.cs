namespace TrackStash.Core.Sqlite;

internal sealed record SchemaMigration(int Version, string Name, string Sql);

internal static class Migrations
{
    public static IReadOnlyList<SchemaMigration> All { get; } =
    [
        new SchemaMigration(1, "initial-schema", InitialSchema),
        new SchemaMigration(2, "embedding-work-item-queue", EmbeddingWorkItemQueue),
        new SchemaMigration(3, "label-aggregate-version", LabelAggregateVersion),
        new SchemaMigration(4, "reviewed-label-merges", ReviewedLabelMerges),
    ];

    private const string ReviewedLabelMerges = """
        CREATE TABLE label_merge_operation (
            operation_id TEXT PRIMARY KEY NOT NULL,
            survivor_id TEXT NOT NULL,
            retired_id TEXT NOT NULL,
            version TEXT NOT NULL,
            expires_utc TEXT NOT NULL,
            state_hash TEXT NOT NULL,
            preview_json TEXT NOT NULL,
            reason TEXT,
            operator_name TEXT,
            principal TEXT,
            outcome_json TEXT
        );
        CREATE TABLE label_merge_audit (
            audit_id TEXT PRIMARY KEY NOT NULL,
            operation_id TEXT NOT NULL UNIQUE REFERENCES label_merge_operation(operation_id),
            survivor_id TEXT NOT NULL,
            retired_id TEXT NOT NULL,
            applied_utc TEXT NOT NULL,
            principal TEXT NOT NULL,
            operator_name TEXT,
            reason TEXT NOT NULL,
            before_json TEXT NOT NULL,
            after_json TEXT NOT NULL
        );
        CREATE TABLE retired_label (
            retired_id TEXT PRIMARY KEY NOT NULL,
            canonical_id TEXT NOT NULL REFERENCES label(label_id),
            retired_utc TEXT NOT NULL,
            audit_id TEXT NOT NULL REFERENCES label_merge_audit(audit_id),
            CHECK (retired_id <> canonical_id)
        );
        CREATE INDEX idx_retired_label_canonical ON retired_label(canonical_id);
        CREATE TRIGGER retired_label_no_delete BEFORE DELETE ON retired_label
        BEGIN SELECT RAISE(ABORT, 'Retired label identities are permanent'); END;
        CREATE TRIGGER retired_label_keep_identity BEFORE UPDATE OF retired_id ON retired_label
        BEGIN SELECT RAISE(ABORT, 'Retired label identities are permanent'); END;
        CREATE TRIGGER retired_label_no_active_insert BEFORE INSERT ON retired_label
        WHEN EXISTS (SELECT 1 FROM label WHERE label_id = NEW.retired_id)
        BEGIN SELECT RAISE(ABORT, 'Retired label ID is still active'); END;
        CREATE TRIGGER retired_label_no_active_update BEFORE UPDATE ON retired_label
        WHEN EXISTS (SELECT 1 FROM label WHERE label_id = NEW.retired_id)
        BEGIN SELECT RAISE(ABORT, 'Retired label ID is still active'); END;
        CREATE TRIGGER label_no_resurrection_insert BEFORE INSERT ON label
        WHEN EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.label_id)
        BEGIN SELECT RAISE(ABORT, 'Label ID is retired; resolve canonical identity before writing'); END;
        CREATE TRIGGER label_no_resurrection_update BEFORE UPDATE OF label_id ON label
        WHEN EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.label_id)
        BEGIN SELECT RAISE(ABORT, 'Label ID is retired; resolve canonical identity before writing'); END;
        CREATE TRIGGER label_preserve_redirects BEFORE DELETE ON label
        WHEN EXISTS (SELECT 1 FROM retired_label WHERE canonical_id = OLD.label_id)
        BEGIN SELECT RAISE(ABORT, 'Label has retired identities; merge rather than delete'); END;
        CREATE TRIGGER label_link_version_insert AFTER INSERT ON release_label_link
        BEGIN UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id = NEW.label_id; END;
        CREATE TRIGGER label_link_version_update AFTER UPDATE ON release_label_link
        BEGIN UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id IN (OLD.label_id, NEW.label_id); END;
        CREATE TRIGGER label_link_version_delete AFTER DELETE ON release_label_link
        BEGIN UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id = OLD.label_id; END;
        CREATE TRIGGER label_merge_audit_no_update BEFORE UPDATE ON label_merge_audit
        BEGIN SELECT RAISE(ABORT, 'Merge audit is append-only'); END;
        CREATE TRIGGER label_merge_audit_no_delete BEFORE DELETE ON label_merge_audit
        BEGIN SELECT RAISE(ABORT, 'Merge audit is append-only'); END;
        CREATE TRIGGER embedding_no_retired_label_insert BEFORE INSERT ON embedding_document
        WHEN lower(NEW.entity_type) = 'label' AND EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.entity_id)
        BEGIN SELECT RAISE(ABORT, 'Cannot embed a retired label'); END;
        CREATE TRIGGER embedding_no_retired_label_update BEFORE UPDATE ON embedding_document
        WHEN lower(NEW.entity_type) = 'label' AND EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.entity_id)
        BEGIN SELECT RAISE(ABORT, 'Cannot embed a retired label'); END;
        CREATE TRIGGER embedding_work_no_retired_label_insert BEFORE INSERT ON embedding_work_item
        WHEN lower(NEW.entity_type) = 'label' AND EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.entity_id)
        BEGIN SELECT RAISE(ABORT, 'Cannot queue a retired label'); END;
        CREATE TRIGGER embedding_work_no_retired_label_update BEFORE UPDATE ON embedding_work_item
        WHEN lower(NEW.entity_type) = 'label' AND EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.entity_id)
        BEGIN SELECT RAISE(ABORT, 'Cannot queue a retired label'); END;
        CREATE TRIGGER label_link_no_retired_insert BEFORE INSERT ON release_label_link
        WHEN EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.label_id)
        BEGIN SELECT RAISE(ABORT, 'Release label ID is retired; resolve canonical identity before writing'); END;
        CREATE TRIGGER label_link_no_retired_update BEFORE UPDATE ON release_label_link
        WHEN EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.label_id)
        BEGIN SELECT RAISE(ABORT, 'Release label ID is retired; resolve canonical identity before writing'); END;
        CREATE TRIGGER label_ref_no_retired_insert BEFORE INSERT ON label_external_ref
        WHEN EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.label_id)
        BEGIN SELECT RAISE(ABORT, 'Reference label ID is retired; resolve canonical identity before writing'); END;
        CREATE TRIGGER label_ref_no_retired_update BEFORE UPDATE ON label_external_ref
        WHEN EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.label_id)
        BEGIN SELECT RAISE(ABORT, 'Reference label ID is retired; resolve canonical identity before writing'); END;
        CREATE TRIGGER label_alias_no_retired_insert BEFORE INSERT ON label_alias
        WHEN EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.label_id)
        BEGIN SELECT RAISE(ABORT, 'Alias label ID is retired; resolve canonical identity before writing'); END;
        CREATE TRIGGER label_alias_no_retired_update BEFORE UPDATE ON label_alias
        WHEN EXISTS (SELECT 1 FROM retired_label WHERE retired_id = NEW.label_id)
        BEGIN SELECT RAISE(ABORT, 'Alias label ID is retired; resolve canonical identity before writing'); END;
        """;

    private const string LabelAggregateVersion = """
        ALTER TABLE label ADD COLUMN version TEXT NOT NULL DEFAULT '';
        UPDATE label SET version = lower(hex(randomblob(16)));

        CREATE TRIGGER label_version_insert AFTER INSERT ON label
        BEGIN
            UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id = NEW.label_id;
        END;

        CREATE TRIGGER label_version_update
        AFTER UPDATE OF label_id, name, normalized_name, sort_name, source_payload_json, created_utc, updated_utc ON label
        BEGIN
            UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id = NEW.label_id;
        END;

        CREATE TRIGGER label_alias_version_insert AFTER INSERT ON label_alias
        BEGIN
            UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id = NEW.label_id;
        END;
        CREATE TRIGGER label_alias_version_update AFTER UPDATE ON label_alias
        BEGIN
            UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id IN (OLD.label_id, NEW.label_id);
        END;
        CREATE TRIGGER label_alias_version_delete AFTER DELETE ON label_alias
        BEGIN
            UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id = OLD.label_id;
        END;

        CREATE TRIGGER label_ref_version_insert AFTER INSERT ON label_external_ref
        BEGIN
            UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id = NEW.label_id;
        END;
        CREATE TRIGGER label_ref_version_update AFTER UPDATE ON label_external_ref
        BEGIN
            UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id IN (OLD.label_id, NEW.label_id);
        END;
        CREATE TRIGGER label_ref_version_delete AFTER DELETE ON label_external_ref
        BEGIN
            UPDATE label SET version = lower(hex(randomblob(16))) WHERE label_id = OLD.label_id;
        END;
        """;

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

        CREATE TABLE IF NOT EXISTS recording_relationship (
            from_recording_id TEXT NOT NULL REFERENCES recording(recording_id),
            to_recording_id   TEXT NOT NULL REFERENCES recording(recording_id),
            relationship_type TEXT NOT NULL,
            source            TEXT,
            confidence        REAL,
            notes             TEXT,
            created_utc       TEXT,
            updated_utc       TEXT,
            PRIMARY KEY (from_recording_id, to_recording_id, relationship_type)
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

        CREATE TABLE IF NOT EXISTS entity_tombstone (
            entity_type      TEXT    NOT NULL,
            entity_id        TEXT    NOT NULL,
            entity_data_json TEXT    NOT NULL,
            deleted_by       TEXT,
            delete_reason    TEXT,
            deleted_utc      TEXT    NOT NULL,
            purge_after_utc  TEXT,
            is_purged        INTEGER NOT NULL DEFAULT 0,
            created_utc      TEXT,
            updated_utc      TEXT,
            PRIMARY KEY (entity_type, entity_id)
        );

        CREATE INDEX IF NOT EXISTS idx_entity_tombstone_type ON entity_tombstone (entity_type);
        CREATE INDEX IF NOT EXISTS idx_entity_tombstone_deleted_utc ON entity_tombstone (deleted_utc);
        CREATE INDEX IF NOT EXISTS idx_entity_tombstone_purge_after_utc ON entity_tombstone (purge_after_utc);
        CREATE INDEX IF NOT EXISTS idx_entity_tombstone_is_purged ON entity_tombstone (is_purged);
        """;

    private const string EmbeddingWorkItemQueue = """
        CREATE TABLE IF NOT EXISTS embedding_work_item (
            work_item_id       TEXT    NOT NULL PRIMARY KEY,
            entity_id          TEXT    NOT NULL,
            entity_type        TEXT    NOT NULL,
            model_name         TEXT    NOT NULL,
            model_version      TEXT    NOT NULL,
            operation          TEXT    NOT NULL,
            force_regenerate   INTEGER NOT NULL DEFAULT 0,
            status             TEXT    NOT NULL,
            attempt_count      INTEGER NOT NULL DEFAULT 0,
            enqueued_utc       TEXT    NOT NULL,
            next_attempt_utc   TEXT,
            leased_until_utc   TEXT,
            last_error         TEXT,
            updated_utc        TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_embedding_work_item_status_next_attempt ON embedding_work_item (status, next_attempt_utc);
        CREATE INDEX IF NOT EXISTS idx_embedding_work_item_entity_model ON embedding_work_item (entity_id, entity_type, model_name, model_version);

        CREATE UNIQUE INDEX IF NOT EXISTS idx_embedding_work_item_active_unique
            ON embedding_work_item (entity_id, entity_type, model_name, model_version)
            WHERE status IN ('Pending', 'Processing');
        """;
}
