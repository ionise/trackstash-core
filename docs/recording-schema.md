# Recording/Track Schema Design

This document captures the intended schema design for canonical recordings, release placements, and extensible source/provider links.

Status: **core tables partially implemented**. The `recording`, `recording_external_ref`, `recording_artist_credit`, `recording_relationship`, and `release_recording` tables are implemented in `src/TrackStash.Core.Sqlite/Migrations.cs` with a simplified column set. Many rich columns (duration_ms, bpm, key, genre, etc.) are not yet present. The embedding tables are replaced by the shared `embedding_document` table. The DDL skeleton at the bottom of this document reflects the original design intent and **does not match the actual migration**.

Key differences from the actual migration:

- Column names differ (`title_norm` in docs vs `normalized_name` in migration); many rich columns are not yet present.
- `ON DELETE CASCADE` described here for `recording_relationship`, `release_recording`, `recording_external_ref`, and `recording_artist_credit` is **not present** in the actual migration.
- `recording_artist_credit.artist_id` is described as nullable with `ON DELETE SET NULL`; the actual migration defines it as `NOT NULL` with no cascade.
- `release_recording` in the docs has `ON DELETE CASCADE` on both FKs; in the actual migration both are bare `REFERENCES` (no cascade), making `release_recording` rows blockers for recording deletion.
- The per-entity embedding tables are replaced by the shared `embedding_document` table.

See [delete-semantics.md](delete-semantics.md) for the delete rules and full reconciliation note.

Related documents:

- [label-schema.md](label-schema.md) for label-domain tables.
- [release-schema.md](release-schema.md) for release-domain tables.
- [artist-schema.md](artist-schema.md) for artist-domain tables.
- [media-matching-schema.md](media-matching-schema.md) for media file fingerprinting and recording matching.
- [schema-conventions.md](schema-conventions.md) for shared ID, normalization, timestamp, and `is_primary` rules.

## Goals

- Separate recording identity from release placement (same recording may appear on multiple releases).
- One canonical Trackstash recording record per audio master.
- Stable Trackstash recording identity using a GUID.
- Unlimited external provider links without adding new columns per provider.
- Support rich artist credits (primary, featured, producer, remixer, co-producer).
- Preserve provider-specific details without polluting canonical schema.

## Canonical Recording Model

### 1) `recording`

Purpose: canonical Trackstash identity for each unique audio recording.

Suggested columns:

- `recording_id TEXT PRIMARY KEY`
  - Trackstash GUID generated in application code.
- `title TEXT NOT NULL`
- `title_norm TEXT NOT NULL`
  - Normalized key for dedupe and matching.
- `mix_name TEXT NULL`
  - Examples: `Original Mix`, `Aquatic Dub`, `Extended Version`.
- `full_title TEXT NULL`
  - Computed display title including mix name and featured artists.
- `isrc TEXT NULL`
  - International Standard Recording Code (globally unique identifier).
- `duration_ms INTEGER NULL`
- `bpm REAL NULL`
- `musical_key_text TEXT NULL`
  - Examples: `A Major`, `G Minor`.
- `key_camelot TEXT NULL`
  - Camelot notation (for DJs/mixing): example `11B`.
- `is_explicit INTEGER NULL`
- `is_available_for_streaming INTEGER NULL`
- `is_available_worldwide INTEGER NULL`
- `genre_name TEXT NULL`
- `sub_genre_name TEXT NULL`
- `image_uri TEXT NULL`
- `image_hash TEXT NULL`
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `UNIQUE(isrc)` (where `isrc` is not null)
- Index on `title_norm`
- Index on `isrc`

### 2) `recording_external_ref`

Purpose: many-to-one mapping from external provider entities to canonical recordings.

Suggested columns:

- `recording_external_ref_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `recording_id TEXT NOT NULL`
- `source TEXT NOT NULL`
  - Examples: `beatport`, `musicbrainz`, `discogs`, and future providers.
- `external_id TEXT NOT NULL`
  - Provider-native identifier.
- `external_url TEXT NULL`
- `source_payload_json TEXT NULL`
  - Provider-specific metadata (sample URLs, hype status, exclusive periods, price, sale type, etc.).
- `confidence REAL NULL`
  - Optional confidence of the linkage.
- `is_primary INTEGER NOT NULL DEFAULT 0`
- `first_seen_utc TEXT NOT NULL`
- `last_seen_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE`
- `UNIQUE(source, external_id)`
- `UNIQUE(recording_id, source, external_id)`
- Index on `recording_id`
- Index on `(source, external_id)`

### 3) `release_recording`

Purpose: join table modeling a recording's placement on a release.

Suggested columns:

- `release_recording_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `release_id TEXT NOT NULL`
- `recording_id TEXT NOT NULL`
- `disc_number INTEGER NULL`
- `track_number INTEGER NULL`
- `display_title TEXT NULL`
  - Title as displayed on this specific release (may differ from canonical recording title).
- `mix_name TEXT NULL`
  - Snapshot of mix name for this release placement.
- `catalog_number_snapshot TEXT NULL`
  - Provider-specific catalog number for this version.
- `publish_date_snapshot TEXT NULL`
  - Snapshot of publish date at time of placement.
- `is_primary_version INTEGER NOT NULL DEFAULT 0`
  - Set to 1 if this is the primary/canonical release version.

Constraints and indexes:

- `FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE`
- `FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE`
- `UNIQUE(release_id, disc_number, track_number)`
- `UNIQUE(release_id, recording_id, disc_number, track_number)`
- Index on `release_id`
- Index on `recording_id`

### 4) `recording_artist_credit`

Purpose: preserve ordered artist credits and roles on recordings.

Suggested columns:

- `recording_artist_credit_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `recording_id TEXT NOT NULL`
- `artist_id TEXT NULL`
  - Canonical artist FK when matched.
- `credited_name TEXT NOT NULL`
- `role TEXT NOT NULL`
  - Examples: `primary`, `featured`, `producer`, `remixer`, `co-producer`, `composer`, `arranger`.
- `sort_order INTEGER NOT NULL DEFAULT 0`
- `join_phrase TEXT NULL`
  - Examples: `feat.`, `&`, `vs.`, `with`.

Constraints and indexes:

- `FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE`
- `FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE SET NULL`
- `UNIQUE(recording_id, credited_name, role, sort_order)`
- Index on `recording_id`
- Index on `artist_id`

### 5) `recording_relationship`

Purpose: model recording-to-recording graph links (remixes, edits, reworks, dubs).

Suggested columns:

- `recording_relationship_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `from_recording_id TEXT NOT NULL`
- `to_recording_id TEXT NOT NULL`
- `relationship_type TEXT NOT NULL`
  - Examples: `remix_of`, `edit_of`, `rework_of`, `dub_of`, `mashup_of`.
- `source TEXT NULL`
- `confidence REAL NULL`
- `notes TEXT NULL`
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(from_recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE`
- `FOREIGN KEY(to_recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE`
- `UNIQUE(from_recording_id, to_recording_id, relationship_type)`
- Index on `(from_recording_id, relationship_type)`
- Index on `(to_recording_id, relationship_type)`

## Beatport Mapping Notes

Core recording fields:

- `name` -> `title`
- `mix_name` -> `mix_name`
- `isrc` -> `isrc`
- `length_ms` -> `duration_ms`
- `bpm` -> `bpm`
- `key.name` -> `musical_key_text`
- `key.camelot_letter` + `key.camelot_number` -> `key_camelot`
- `is_explicit` -> `is_explicit`
- `is_available_for_streaming` -> `is_available_for_streaming`
- `available_worldwide` -> `is_available_worldwide`
- `genre.name` -> `genre_name`
- `image.uri` -> `image_uri`

External link mapping:

- Beatport ID and URL should be stored in `recording_external_ref` where `source = beatport`.
- Provider-specific fields such as `price`, `exclusive`, `hype`, `sample_url`, `sale_type` go in `source_payload_json`.

Artist credit mapping:

- `artists` array maps to `recording_artist_credit` with role = `primary`.
- `remixers` array maps to `recording_artist_credit` with role = `remixer` on a **separate recording** via `recording_relationship.remix_of`.
- Featured artists in the artists array should be identified by role label when available; otherwise infer from credited name patterns.

Release placement:

- Track placement details (`number`, `catalog_number`, `publish_date`) snapshot into `release_recording`.

## Embedding Support

Text embeddings (e.g., MiniLM) can be generated from recording metadata (title, mix name, artist names, genre) for semantic search and remix/remix-of discovery.

### 6) `recording_embedding_document`

Purpose: stores the text corpus to be embedded for a recording.

Suggested columns:

- `recording_embedding_document_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `recording_id TEXT NOT NULL`
- `document_text TEXT NOT NULL`
  - Concatenation of recording title, mix name, artist names, genre, sub_genre.
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE`
- `UNIQUE(recording_id)`
- Index on `recording_id`

### 7) `recording_embedding`

Purpose: versioned embedding vectors for recordings.

Suggested columns:

- `recording_embedding_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `recording_id TEXT NOT NULL`
- `model_name TEXT NOT NULL`
  - Examples: `minilm-l6-v2`, `minilm-l12-v2`, `bge-small-en`, `openai-ada-v2`.
- `model_version TEXT NOT NULL`
  - Full semantic version, e.g., `1.0.0`.
- `embedding BLOB NOT NULL`
  - Serialized float32 vector (dimensions * 4 bytes).
- `dimensions INTEGER NOT NULL`
- `created_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE`
- `UNIQUE(recording_id, model_name, model_version)`
- Index on `recording_id`
- Index on `(model_name, model_version)`

## Suggested SQLite DDL Skeleton

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS recording (
    recording_id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    title_norm TEXT NOT NULL,
    mix_name TEXT NULL,
    full_title TEXT NULL,
    isrc TEXT NULL,
    duration_ms INTEGER NULL,
    bpm REAL NULL,
    musical_key_text TEXT NULL,
    key_camelot TEXT NULL,
    is_explicit INTEGER NULL,
    is_available_for_streaming INTEGER NULL,
    is_available_worldwide INTEGER NULL,
    genre_name TEXT NULL,
    sub_genre_name TEXT NULL,
    image_uri TEXT NULL,
    image_hash TEXT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    UNIQUE(isrc)
);

CREATE TABLE IF NOT EXISTS recording_external_ref (
    recording_external_ref_id INTEGER PRIMARY KEY AUTOINCREMENT,
    recording_id TEXT NOT NULL,
    source TEXT NOT NULL,
    external_id TEXT NOT NULL,
    external_url TEXT NULL,
    source_payload_json TEXT NULL,
    confidence REAL NULL,
    is_primary INTEGER NOT NULL DEFAULT 0,
    first_seen_utc TEXT NOT NULL,
    last_seen_utc TEXT NOT NULL,
    FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE,
    UNIQUE(source, external_id),
    UNIQUE(recording_id, source, external_id)
);

CREATE TABLE IF NOT EXISTS release_recording (
    release_recording_id INTEGER PRIMARY KEY AUTOINCREMENT,
    release_id TEXT NOT NULL,
    recording_id TEXT NOT NULL,
    disc_number INTEGER NULL,
    track_number INTEGER NULL,
    display_title TEXT NULL,
    mix_name TEXT NULL,
    catalog_number_snapshot TEXT NULL,
    publish_date_snapshot TEXT NULL,
    is_primary_version INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE,
    FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE,
    UNIQUE(release_id, disc_number, track_number),
    UNIQUE(release_id, recording_id, disc_number, track_number)
);

CREATE TABLE IF NOT EXISTS recording_artist_credit (
    recording_artist_credit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    recording_id TEXT NOT NULL,
    artist_id TEXT NULL,
    credited_name TEXT NOT NULL,
    role TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    join_phrase TEXT NULL,
    FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE,
    FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE SET NULL,
    UNIQUE(recording_id, credited_name, role, sort_order)
);

CREATE TABLE IF NOT EXISTS recording_relationship (
    recording_relationship_id INTEGER PRIMARY KEY AUTOINCREMENT,
    from_recording_id TEXT NOT NULL,
    to_recording_id TEXT NOT NULL,
    relationship_type TEXT NOT NULL,
    source TEXT NULL,
    confidence REAL NULL,
    notes TEXT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    FOREIGN KEY(from_recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE,
    FOREIGN KEY(to_recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE,
    UNIQUE(from_recording_id, to_recording_id, relationship_type)
);

CREATE INDEX IF NOT EXISTS ix_recording_title_norm ON recording(title_norm);
CREATE INDEX IF NOT EXISTS ix_recording_isrc ON recording(isrc);
CREATE INDEX IF NOT EXISTS ix_recording_external_ref_recording ON recording_external_ref(recording_id);
CREATE INDEX IF NOT EXISTS ix_recording_external_ref_source_external ON recording_external_ref(source, external_id);
CREATE INDEX IF NOT EXISTS ix_release_recording_release ON release_recording(release_id);
CREATE INDEX IF NOT EXISTS ix_release_recording_recording ON release_recording(recording_id);
CREATE INDEX IF NOT EXISTS ix_recording_artist_credit_recording ON recording_artist_credit(recording_id);
CREATE INDEX IF NOT EXISTS ix_recording_artist_credit_artist ON recording_artist_credit(artist_id);
CREATE INDEX IF NOT EXISTS ix_recording_relationship_from_type ON recording_relationship(from_recording_id, relationship_type);
CREATE INDEX IF NOT EXISTS ix_recording_relationship_to_type ON recording_relationship(to_recording_id, relationship_type);

CREATE TABLE IF NOT EXISTS recording_embedding_document (
    recording_embedding_document_id INTEGER PRIMARY KEY AUTOINCREMENT,
    recording_id TEXT NOT NULL,
    document_text TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE,
    UNIQUE(recording_id)
);

CREATE TABLE IF NOT EXISTS recording_embedding (
    recording_embedding_id INTEGER PRIMARY KEY AUTOINCREMENT,
    recording_id TEXT NOT NULL,
    model_name TEXT NOT NULL,
    model_version TEXT NOT NULL,
    embedding BLOB NOT NULL,
    dimensions INTEGER NOT NULL,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE,
    UNIQUE(recording_id, model_name, model_version)
);

CREATE INDEX IF NOT EXISTS ix_recording_embedding_document_recording ON recording_embedding_document(recording_id);
CREATE INDEX IF NOT EXISTS ix_recording_embedding_recording ON recording_embedding(recording_id);
CREATE INDEX IF NOT EXISTS ix_recording_embedding_model ON recording_embedding(model_name, model_version);
```

## Migration Plan (Phased)

1. Add `recording`, `recording_external_ref`, `release_recording`, `recording_artist_credit`, and `recording_relationship`.
2. Ingest external provider recording identities into `recording_external_ref` (Beatport first).
3. Backfill canonical recordings from existing track/media metadata when possible.
4. Populate `release_recording` by linking canonical recordings to releases.
5. Populate `recording_artist_credit` from Beatport artist arrays and role inference.
6. Add recording relationships (remixes, edits) from provider data.
7. Link canonical `recording_id` from existing `metadata` table where available.

## Open Decisions

- Normalization function for `title_norm`.
- Whether to enforce one `is_primary = 1` row per `(recording_id, source)` via trigger.
- Criteria for recording dedupe when ISRC is missing.
- How to infer featured artist role from Beatport name patterns (e.g., "feat.").
- Retention policy for old recordings no longer in use.
