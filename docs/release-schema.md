# Release Schema Design

This document captures the intended schema design for canonical releases and extensible source/provider links.

Status: **core tables partially implemented**. The `release`, `release_external_ref`, `release_artist_credit`, and `release_label_link` tables are implemented in `src/TrackStash.Core.Sqlite/Migrations.cs` with a simplified column set. The `primary_label_id` column on `release` is **not present** in the actual migration; the label association is captured in `release_label_link` only. The embedding tables are replaced by the shared `embedding_document` table. The DDL skeleton at the bottom of this document reflects the original design intent and **does not match the actual migration**.

Key differences from the actual migration:

- Column names differ (`title_norm` in docs vs `normalized_name` in migration); many rich columns (bpm_min, bpm_max, catalog_number, etc.) are not yet present.
- `ON DELETE CASCADE` and `ON DELETE RESTRICT` described here are **not present** in the actual migration.
- `release_artist_credit.artist_id` is described as nullable with `ON DELETE SET NULL`; the actual migration defines it as `NOT NULL` with no cascade.
- The per-entity embedding tables are replaced by the shared `embedding_document` table.

See [delete-semantics.md](delete-semantics.md) for the delete rules and full reconciliation note.

Related documents:

- [label-schema.md](label-schema.md) for label-domain tables.
- [artist-schema.md](artist-schema.md) for artist-domain tables.
- [recording-schema.md](recording-schema.md) for recording/track-domain tables.
- [media-matching-schema.md](media-matching-schema.md) for media file fingerprinting and recording matching.
- [schema-conventions.md](schema-conventions.md) for shared ID, normalization, timestamp, and `is_primary` rules.

## Goals

- One canonical Trackstash release record per real-world release.
- Stable Trackstash release identity using a GUID.
- Unlimited external provider links without adding new columns per provider.
- Preserve provider-specific details without polluting canonical schema.
- Support rich release credits (primary artist, remixer, featured artists, etc.).

## Canonical Release Model

### 1) `release`

Purpose: canonical Trackstash identity for each release (album, single, EP, compilation).

Suggested columns:

- `release_id TEXT PRIMARY KEY`
  - Trackstash GUID generated in application code.
- `title TEXT NOT NULL`
- `title_norm TEXT NOT NULL`
  - Normalized key for dedupe and matching.
- `subtitle TEXT NULL`
- `release_type TEXT NULL`
  - Examples: `album`, `single`, `ep`, `compilation`, `remix-pack`.
- `primary_label_id TEXT NULL`
  - Canonical label FK when known.
- `catalog_number TEXT NULL`
- `barcode_upc TEXT NULL`
- `release_date TEXT NULL`
- `publish_date TEXT NULL`
- `year INTEGER NULL`
- `track_count INTEGER NULL`
- `bpm_min REAL NULL`
- `bpm_max REAL NULL`
- `is_explicit INTEGER NULL`
- `description TEXT NULL`
- `cover_image_uri TEXT NULL`
- `cover_image_hash TEXT NULL`
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(primary_label_id) REFERENCES label(label_id) ON DELETE SET NULL`
- `UNIQUE(title_norm, primary_label_id, catalog_number)`
- Index on `title_norm`
- Index on `primary_label_id`
- Index on `barcode_upc`

### 2) `release_external_ref`

Purpose: many-to-one mapping from external provider entities to canonical releases.

Suggested columns:

- `release_external_ref_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `release_id TEXT NOT NULL`
- `source TEXT NOT NULL`
  - Examples: `beatport`, `musicbrainz`, `discogs`, and future providers.
- `external_id TEXT NOT NULL`
  - Provider-native identifier.
- `external_url TEXT NULL`
- `source_payload_json TEXT NULL`
  - Provider-specific metadata snapshot (price, hype, exclusivity, ranking, etc.).
- `confidence REAL NULL`
  - Optional confidence of the linkage.
- `is_primary INTEGER NOT NULL DEFAULT 0`
- `first_seen_utc TEXT NOT NULL`
- `last_seen_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE`
- `UNIQUE(source, external_id)`
- `UNIQUE(release_id, source, external_id)`
- Index on `release_id`
- Index on `(source, external_id)`

### 3) `release_artist_credit` (recommended)

Purpose: preserve display credits and role-based associations for releases.

Suggested columns:

- `release_artist_credit_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `release_id TEXT NOT NULL`
- `artist_id TEXT NULL`
  - Canonical artist FK when available.
- `credited_name TEXT NOT NULL`
- `role TEXT NOT NULL`
  - Examples: `primary`, `featured`, `remixer`, `producer`, `composer`.
- `sort_order INTEGER NOT NULL DEFAULT 0`

Constraints and indexes:

- `FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE`
- `UNIQUE(release_id, credited_name, role, sort_order)`
- Index on `release_id`
- Index on `artist_id`

### 4) `release_label_link` (optional)

Purpose: support multi-label releases and explicit label roles.

Suggested columns:

- `release_label_link_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `release_id TEXT NOT NULL`
- `label_id TEXT NOT NULL`
- `role TEXT NOT NULL`
  - Examples: `primary`, `imprint`, `publisher`, `distributor`.
- `sort_order INTEGER NOT NULL DEFAULT 0`

Constraints and indexes:

- `FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE`
- `FOREIGN KEY(label_id) REFERENCES label(label_id) ON DELETE RESTRICT`
- `UNIQUE(release_id, label_id, role)`
- Index on `release_id`
- Index on `label_id`

## Beatport Mapping Notes

- Beatport ID and URL should be stored in `release_external_ref` where `source = beatport`.
- Provider-specific fields such as `price`, `exclusive`, `hype`, and `pre_order` should live in `source_payload_json`.
- Canonical fields map into `release`:
  - `Album` -> `title`
  - `Catalog #` -> `catalog_number`
  - `UPC` -> `barcode_upc`
  - `Track Count` -> `track_count`
  - `BPM Range` -> `bpm_min` and `bpm_max`
  - `Year` -> `year`
  - `Published` -> `publish_date`
  - `Released` -> `release_date`
  - `Explicit` -> `is_explicit`
  - `Description` -> `description`
  - `Image` -> `cover_image_uri`

## Embedding Support

Text embeddings (e.g., MiniLM) can be generated from release metadata (name, description, artist names) for semantic search and similarity queries.

### 6) `release_embedding_document`

Purpose: stores the text corpus to be embedded for a release.

Suggested columns:

- `release_embedding_document_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `release_id TEXT NOT NULL`
- `document_text TEXT NOT NULL`
  - Concatenation of release name, artist names, label name, catalog number, description.
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE`
- `UNIQUE(release_id)`
- Index on `release_id`

### 7) `release_embedding`

Purpose: versioned embedding vectors for releases.

Suggested columns:

- `release_embedding_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `release_id TEXT NOT NULL`
- `model_name TEXT NOT NULL`
  - Examples: `minilm-l6-v2`, `minilm-l12-v2`, `bge-small-en`, `openai-ada-v2`.
- `model_version TEXT NOT NULL`
  - Full semantic version, e.g., `1.0.0`.
- `embedding BLOB NOT NULL`
  - Serialized float32 vector (dimensions * 4 bytes).
- `dimensions INTEGER NOT NULL`
- `created_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE`
- `UNIQUE(release_id, model_name, model_version)`
- Index on `release_id`
- Index on `(model_name, model_version)`

## Suggested SQLite DDL Skeleton

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS release (
    release_id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    title_norm TEXT NOT NULL,
    subtitle TEXT NULL,
    release_type TEXT NULL,
    primary_label_id TEXT NULL,
    catalog_number TEXT NULL,
    barcode_upc TEXT NULL,
    release_date TEXT NULL,
    publish_date TEXT NULL,
    year INTEGER NULL,
    track_count INTEGER NULL,
    bpm_min REAL NULL,
    bpm_max REAL NULL,
    is_explicit INTEGER NULL,
    description TEXT NULL,
    cover_image_uri TEXT NULL,
    cover_image_hash TEXT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    FOREIGN KEY(primary_label_id) REFERENCES label(label_id) ON DELETE SET NULL,
    UNIQUE(title_norm, primary_label_id, catalog_number)
);

CREATE TABLE IF NOT EXISTS release_external_ref (
    release_external_ref_id INTEGER PRIMARY KEY AUTOINCREMENT,
    release_id TEXT NOT NULL,
    source TEXT NOT NULL,
    external_id TEXT NOT NULL,
    external_url TEXT NULL,
    source_payload_json TEXT NULL,
    confidence REAL NULL,
    is_primary INTEGER NOT NULL DEFAULT 0,
    first_seen_utc TEXT NOT NULL,
    last_seen_utc TEXT NOT NULL,
    FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE,
    UNIQUE(source, external_id),
    UNIQUE(release_id, source, external_id)
);

CREATE TABLE IF NOT EXISTS release_artist_credit (
    release_artist_credit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    release_id TEXT NOT NULL,
    artist_id TEXT NULL,
    credited_name TEXT NOT NULL,
    role TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE,
    UNIQUE(release_id, credited_name, role, sort_order)
);

CREATE TABLE IF NOT EXISTS release_label_link (
    release_label_link_id INTEGER PRIMARY KEY AUTOINCREMENT,
    release_id TEXT NOT NULL,
    label_id TEXT NOT NULL,
    role TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE,
    FOREIGN KEY(label_id) REFERENCES label(label_id) ON DELETE RESTRICT,
    UNIQUE(release_id, label_id, role)
);

CREATE INDEX IF NOT EXISTS ix_release_title_norm ON release(title_norm);
CREATE INDEX IF NOT EXISTS ix_release_primary_label ON release(primary_label_id);
CREATE INDEX IF NOT EXISTS ix_release_barcode_upc ON release(barcode_upc);
CREATE INDEX IF NOT EXISTS ix_release_external_ref_release ON release_external_ref(release_id);
CREATE INDEX IF NOT EXISTS ix_release_external_ref_source_external ON release_external_ref(source, external_id);
CREATE INDEX IF NOT EXISTS ix_release_artist_credit_release ON release_artist_credit(release_id);
CREATE INDEX IF NOT EXISTS ix_release_artist_credit_artist ON release_artist_credit(artist_id);
CREATE INDEX IF NOT EXISTS ix_release_label_link_release ON release_label_link(release_id);
CREATE INDEX IF NOT EXISTS ix_release_label_link_label ON release_label_link(label_id);

CREATE TABLE IF NOT EXISTS release_embedding_document (
    release_embedding_document_id INTEGER PRIMARY KEY AUTOINCREMENT,
    release_id TEXT NOT NULL,
    document_text TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE,
    UNIQUE(release_id)
);

CREATE TABLE IF NOT EXISTS release_embedding (
    release_embedding_id INTEGER PRIMARY KEY AUTOINCREMENT,
    release_id TEXT NOT NULL,
    model_name TEXT NOT NULL,
    model_version TEXT NOT NULL,
    embedding BLOB NOT NULL,
    dimensions INTEGER NOT NULL,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE,
    UNIQUE(release_id, model_name, model_version)
);

CREATE INDEX IF NOT EXISTS ix_release_embedding_document_release ON release_embedding_document(release_id);
CREATE INDEX IF NOT EXISTS ix_release_embedding_release ON release_embedding(release_id);
CREATE INDEX IF NOT EXISTS ix_release_embedding_model ON release_embedding(model_name, model_version);
```

## Migration Plan (Phased)

1. Add `release`, `release_external_ref`, and `release_artist_credit`.
2. Add `release_label_link` only if multi-label support is required.
3. Start ingesting source links into `release_external_ref` (Beatport first).
4. Ingest release credits into `release_artist_credit`.
5. Backfill canonical release rows from existing data where possible.
6. Update ingest/write paths to use `release_id` as canonical identity.

## Open Decisions

- Normalization function for `title_norm`.
- Whether to enforce one `is_primary = 1` row per `(release_id, source)` via trigger.
- Criteria for release dedupe when `catalog_number` and `barcode_upc` are missing.
- Whether release embeddings should be added in a dedicated release-embedding doc.
