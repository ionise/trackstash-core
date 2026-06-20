# Label Schema Design

This document captures the intended schema design for canonical labels, label source links, and optional label embedding storage.

Status: **core tables partially implemented**. The `label`, `label_external_ref`, and `label_alias` tables are implemented in `src/TrackStash.Core.Sqlite/Migrations.cs` with a simplified column set. The embedding tables (`label_embedding_document`, `label_embedding`) are not implemented; embeddings use the shared `embedding_document` table instead. The DDL skeleton at the bottom of this document reflects the original design intent and **does not match the actual migration**.

Key differences from the actual migration:

- Column names differ (`canonical_name`/`canonical_name_norm` in docs vs `name`/`normalized_name` in migration).
- `ON DELETE CASCADE` described here is **not present** in the actual migration, which uses bare `REFERENCES` (effectively `NO ACTION`).
- The per-entity embedding tables are replaced by the shared `embedding_document` table.

See [delete-semantics.md](delete-semantics.md) for the delete rules and full reconciliation note.

Related documents:

- [release-schema.md](release-schema.md) for release-domain tables.
- [artist-schema.md](artist-schema.md) for artist-domain tables.
- [recording-schema.md](recording-schema.md) for recording/track-domain tables.
- [media-matching-schema.md](media-matching-schema.md) for media file fingerprinting and recording matching.
- [schema-conventions.md](schema-conventions.md) for shared ID, normalization, timestamp, and `is_primary` rules.

## Goals

- One canonical Trackstash label record per real-world label.
- Stable Trackstash label identity using a GUID.
- Unlimited external provider links without adding new columns per provider.
- Keep core relational data separate from embedding/ML derived data.
- Allow re-embedding and multi-model embeddings without data loss.

## Canonical Label Model

### 1) `label`

Purpose: canonical Trackstash identity for each label/publisher.

Suggested columns:

- `label_id TEXT PRIMARY KEY`
  - Trackstash GUID generated in application code (for example, `New-Guid`).
- `canonical_name TEXT NOT NULL`
- `canonical_name_norm TEXT NOT NULL`
  - Normalized key for dedupe and matching.
- `logo_uri TEXT NULL`
- `logo_hash TEXT NULL`
- `country_code TEXT NULL`
- `founded_year INTEGER NULL`
- `dissolved_year INTEGER NULL`
- `status TEXT NULL`
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `UNIQUE(canonical_name_norm)`
- Index on `canonical_name` for search.

### 2) `label_external_ref`

Purpose: many-to-one mapping from external provider entities to canonical labels.

Suggested columns:

- `label_external_ref_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `label_id TEXT NOT NULL`
- `source TEXT NOT NULL`
  - Examples: `beatport`, `musicbrainz`, `discogs`, plus future providers.
- `external_id TEXT NOT NULL`
  - Provider-native identifier.
- `external_url TEXT NULL`
- `source_payload_json TEXT NULL`
  - Optional provider-specific metadata snapshot.
- `confidence REAL NULL`
  - Optional confidence of the linkage.
- `is_primary INTEGER NOT NULL DEFAULT 0`
- `first_seen_utc TEXT NOT NULL`
- `last_seen_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(label_id) REFERENCES label(label_id) ON DELETE CASCADE`
- `UNIQUE(source, external_id)`
- `UNIQUE(label_id, source, external_id)`
- Index on `label_id`
- Index on `(source, external_id)`

### 3) `label_alias` (optional but recommended)

Purpose: alternate names and historical names for robust matching.

Suggested columns:

- `label_alias_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `label_id TEXT NOT NULL`
- `alias_name TEXT NOT NULL`
- `alias_name_norm TEXT NOT NULL`
- `alias_type TEXT NULL`
  - Examples: `alternate`, `historical`, `localized`, `short`.

Constraints and indexes:

- `FOREIGN KEY(label_id) REFERENCES label(label_id) ON DELETE CASCADE`
- `UNIQUE(label_id, alias_name_norm)`
- Index on `alias_name_norm`

## Linking From Existing Track Metadata

Current table `metadata` stores `label` as text.

Planned transition:

- Add `metadata.label_id TEXT NULL`.
- Add `FOREIGN KEY(label_id) REFERENCES label(label_id)`.
- Keep `metadata.label` during migration for backward compatibility.

Recommendation:

- Read path: prefer `metadata.label_id` joined to `label`; fall back to `metadata.label` when null.
- Write path: populate both during transition, then eventually make `metadata.label` optional/legacy.

## Embedding Storage (Label Domain)

Embeddings should be stored in separate tables so model/version changes do not impact canonical schema.

### 4) `label_embedding_document`

Purpose: versioned text snapshots that were embedded.

Suggested columns:

- `label_embedding_document_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `label_id TEXT NOT NULL`
- `document_type TEXT NOT NULL`
  - Examples: `profile`, `provider_summary`, `alias_rollup`.
- `text_content TEXT NOT NULL`
- `text_hash TEXT NOT NULL`
  - Hash of text content for change detection.
- `created_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(label_id) REFERENCES label(label_id) ON DELETE CASCADE`
- `UNIQUE(label_id, document_type, text_hash)`
- Index on `label_id`

### 5) `label_embedding`

Purpose: one or more vectors per document, keyed by model/version.

Suggested columns:

- `label_embedding_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `label_embedding_document_id INTEGER NOT NULL`
- `model_name TEXT NOT NULL`
  - Example: `all-MiniLM-L6-v2`.
- `model_version TEXT NOT NULL`
- `dimension INTEGER NOT NULL`
- `vector_json TEXT NULL`
  - JSON array option for simple portability.
- `vector_blob BLOB NULL`
  - Optional float32 packed bytes for compact storage/performance.
- `normalized INTEGER NOT NULL DEFAULT 0`
- `created_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(label_embedding_document_id) REFERENCES label_embedding_document(label_embedding_document_id) ON DELETE CASCADE`
- `UNIQUE(label_embedding_document_id, model_name, model_version)`
- Index on `(model_name, model_version)`

Notes:

- Keep either `vector_json` or `vector_blob` as the canonical storage format for consistency.
- If nearest-neighbor search is needed later, this table can be mirrored to a vector index engine.

## Suggested SQLite DDL Skeleton

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS label (
    label_id TEXT PRIMARY KEY,
    canonical_name TEXT NOT NULL,
    canonical_name_norm TEXT NOT NULL,
    logo_uri TEXT NULL,
    logo_hash TEXT NULL,
    country_code TEXT NULL,
    founded_year INTEGER NULL,
    dissolved_year INTEGER NULL,
    status TEXT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    UNIQUE(canonical_name_norm)
);

CREATE TABLE IF NOT EXISTS label_external_ref (
    label_external_ref_id INTEGER PRIMARY KEY AUTOINCREMENT,
    label_id TEXT NOT NULL,
    source TEXT NOT NULL,
    external_id TEXT NOT NULL,
    external_url TEXT NULL,
    source_payload_json TEXT NULL,
    confidence REAL NULL,
    is_primary INTEGER NOT NULL DEFAULT 0,
    first_seen_utc TEXT NOT NULL,
    last_seen_utc TEXT NOT NULL,
    FOREIGN KEY(label_id) REFERENCES label(label_id) ON DELETE CASCADE,
    UNIQUE(source, external_id),
    UNIQUE(label_id, source, external_id)
);

CREATE TABLE IF NOT EXISTS label_alias (
    label_alias_id INTEGER PRIMARY KEY AUTOINCREMENT,
    label_id TEXT NOT NULL,
    alias_name TEXT NOT NULL,
    alias_name_norm TEXT NOT NULL,
    alias_type TEXT NULL,
    FOREIGN KEY(label_id) REFERENCES label(label_id) ON DELETE CASCADE,
    UNIQUE(label_id, alias_name_norm)
);

ALTER TABLE metadata ADD COLUMN label_id TEXT NULL;

CREATE TABLE IF NOT EXISTS label_embedding_document (
    label_embedding_document_id INTEGER PRIMARY KEY AUTOINCREMENT,
    label_id TEXT NOT NULL,
    document_type TEXT NOT NULL,
    text_content TEXT NOT NULL,
    text_hash TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(label_id) REFERENCES label(label_id) ON DELETE CASCADE,
    UNIQUE(label_id, document_type, text_hash)
);

CREATE TABLE IF NOT EXISTS label_embedding (
    label_embedding_id INTEGER PRIMARY KEY AUTOINCREMENT,
    label_embedding_document_id INTEGER NOT NULL,
    model_name TEXT NOT NULL,
    model_version TEXT NOT NULL,
    dimension INTEGER NOT NULL,
    vector_json TEXT NULL,
    vector_blob BLOB NULL,
    normalized INTEGER NOT NULL DEFAULT 0,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(label_embedding_document_id) REFERENCES label_embedding_document(label_embedding_document_id) ON DELETE CASCADE,
    UNIQUE(label_embedding_document_id, model_name, model_version)
);

CREATE INDEX IF NOT EXISTS ix_label_name_norm ON label(canonical_name_norm);
CREATE INDEX IF NOT EXISTS ix_label_external_ref_label ON label_external_ref(label_id);
CREATE INDEX IF NOT EXISTS ix_label_external_ref_source_external ON label_external_ref(source, external_id);
CREATE INDEX IF NOT EXISTS ix_label_alias_name_norm ON label_alias(alias_name_norm);
CREATE INDEX IF NOT EXISTS ix_metadata_label_id ON metadata(label_id);
CREATE INDEX IF NOT EXISTS ix_label_embedding_doc_label ON label_embedding_document(label_id);
CREATE INDEX IF NOT EXISTS ix_label_embedding_model ON label_embedding(model_name, model_version);
```

## Migration Plan (Phased)

1. Add `label`, `label_external_ref`, and `label_alias`.
2. Add `metadata.label_id` and backfill from distinct non-empty `metadata.label` values.
3. Populate `metadata.label_id` by normalized-name matching.
4. Start ingesting provider links into `label_external_ref` (Beatport first).
5. Add label embedding pipeline output to `label_embedding_document` and `label_embedding`.
6. Update read/write paths to prefer canonical `label_id`.
7. Keep `metadata.label` as legacy until all consumers migrate.

## Open Decisions
