# Artist Schema Design (Planned)

This document captures the planned Trackstash schema changes for canonical artists, artist relationships, and extensible source/provider links.

Status: design only. This is not yet applied in `Initialize-TrackstashDatabase`.

Related documents:

- [label-schema.md](label-schema.md) for label-domain tables.
- [release-schema.md](release-schema.md) for release-domain tables.
- [recording-schema.md](recording-schema.md) for recording/track-domain tables.
- [media-matching-schema.md](media-matching-schema.md) for media file fingerprinting and recording matching.
- [schema-conventions.md](schema-conventions.md) for shared ID, normalization, timestamp, and `is_primary` rules.

## Goals

- One canonical Trackstash artist record per real-world artist identity.
- Stable Trackstash artist identity using a GUID.
- Unlimited external provider links without adding new columns per provider.
- Support aliases, group membership, and cross-artist relationships.
- Support role-based artist credits on releases/tracks.

## Canonical Artist Model

### 1) `artist`

Purpose: canonical Trackstash identity for each solo artist, group, band, or project.

Suggested columns:

- `artist_id TEXT PRIMARY KEY`
  - Trackstash GUID generated in application code.
- `display_name TEXT NOT NULL`
- `display_name_norm TEXT NOT NULL`
  - Normalized key for dedupe and matching.
- `sort_name TEXT NULL`
- `artist_kind TEXT NULL`
  - Examples: `person`, `group`, `project`, `orchestra`.
- `country_code TEXT NULL`
- `begin_date TEXT NULL`
- `end_date TEXT NULL`
- `is_active INTEGER NULL`
- `image_uri TEXT NULL`
- `image_hash TEXT NULL`
- `bio TEXT NULL`
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `UNIQUE(display_name_norm, artist_kind)`
- Index on `display_name_norm`
- Index on `artist_kind`

### 2) `artist_external_ref`

Purpose: many-to-one mapping from external provider entities to canonical artists.

Suggested columns:

- `artist_external_ref_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `artist_id TEXT NOT NULL`
- `source TEXT NOT NULL`
  - Examples: `beatport`, `musicbrainz`, `discogs`, and future providers.
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

- `FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE`
- `UNIQUE(source, external_id)`
- `UNIQUE(artist_id, source, external_id)`
- Index on `artist_id`
- Index on `(source, external_id)`

### 3) `artist_alias`

Purpose: alternate names and pseudonyms for robust matching and display.

Suggested columns:

- `artist_alias_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `artist_id TEXT NOT NULL`
- `alias_name TEXT NOT NULL`
- `alias_name_norm TEXT NOT NULL`
- `alias_type TEXT NULL`
  - Examples: `stage-name`, `legal-name`, `transliteration`, `historical`.
- `valid_from TEXT NULL`
- `valid_to TEXT NULL`

Constraints and indexes:

- `FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE`
- `UNIQUE(artist_id, alias_name_norm)`
- Index on `alias_name_norm`

### 4) `artist_relationship`

Purpose: model artist-to-artist graph links (aliases, group membership, collaborations).

Suggested columns:

- `artist_relationship_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `from_artist_id TEXT NOT NULL`
- `to_artist_id TEXT NOT NULL`
- `relationship_type TEXT NOT NULL`
  - Examples: `alias_of`, `member_of`, `has_member`, `collaborator_of`.
- `start_date TEXT NULL`
- `end_date TEXT NULL`
- `source TEXT NULL`
- `confidence REAL NULL`
- `notes TEXT NULL`
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(from_artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE`
- `FOREIGN KEY(to_artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE`
- `UNIQUE(from_artist_id, to_artist_id, relationship_type)`
- Index on `(from_artist_id, relationship_type)`
- Index on `(to_artist_id, relationship_type)`

## Credit Linking Across Domains

Artist identity should be linked through role-based credit tables rather than denormalized text fields.

### 5) `release_artist_credit`

Purpose: preserve ordered artist credits and roles on releases.

Suggested columns:

- `release_artist_credit_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `release_id TEXT NOT NULL`
- `artist_id TEXT NULL`
  - Canonical artist FK when matched.
- `credited_name TEXT NOT NULL`
- `role TEXT NOT NULL`
  - Examples: `primary`, `featured`, `remixer`, `producer`, `composer`.
- `sort_order INTEGER NOT NULL DEFAULT 0`
- `join_phrase TEXT NULL`

Constraints and indexes:

- `FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE`
- `FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE SET NULL`
- `UNIQUE(release_id, credited_name, role, sort_order)`
- Index on `release_id`
- Index on `artist_id`

### 6) `track_artist_credit` (future/when track domain is canonicalized)

Purpose: preserve ordered artist credits and roles on tracks.

Suggested columns:

- `track_artist_credit_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `track_id TEXT NOT NULL`
- `artist_id TEXT NULL`
- `credited_name TEXT NOT NULL`
- `role TEXT NOT NULL`
- `sort_order INTEGER NOT NULL DEFAULT 0`
- `join_phrase TEXT NULL`

Constraints and indexes:

- `FOREIGN KEY(track_id) REFERENCES track(track_id) ON DELETE CASCADE`
- `FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE SET NULL`
- `UNIQUE(track_id, credited_name, role, sort_order)`
- Index on `track_id`
- Index on `artist_id`

## Embedding Support

Text embeddings (e.g., MiniLM) can be generated from artist metadata (name, aliases, bio) for semantic search and artist similarity queries.

### 5) `artist_embedding_document`

Purpose: stores the text corpus to be embedded for an artist.

Suggested columns:

- `artist_embedding_document_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `artist_id TEXT NOT NULL`
- `document_text TEXT NOT NULL`
  - Concatenation of artist name, aliases, bio, origin info.
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE`
- `UNIQUE(artist_id)`
- Index on `artist_id`

### 6) `artist_embedding`

Purpose: versioned embedding vectors for artists.

Suggested columns:

- `artist_embedding_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `artist_id TEXT NOT NULL`
- `model_name TEXT NOT NULL`
  - Examples: `minilm-l6-v2`, `minilm-l12-v2`, `bge-small-en`, `openai-ada-v2`.
- `model_version TEXT NOT NULL`
  - Full semantic version, e.g., `1.0.0`.
- `embedding BLOB NOT NULL`
  - Serialized float32 vector (dimensions * 4 bytes).
- `dimensions INTEGER NOT NULL`
- `created_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE`
- `UNIQUE(artist_id, model_name, model_version)`
- Index on `artist_id`
- Index on `(model_name, model_version)`

## Suggested SQLite DDL Skeleton

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS artist (
    artist_id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    display_name_norm TEXT NOT NULL,
    sort_name TEXT NULL,
    artist_kind TEXT NULL,
    country_code TEXT NULL,
    begin_date TEXT NULL,
    end_date TEXT NULL,
    is_active INTEGER NULL,
    image_uri TEXT NULL,
    image_hash TEXT NULL,
    bio TEXT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    UNIQUE(display_name_norm, artist_kind)
);

CREATE TABLE IF NOT EXISTS artist_external_ref (
    artist_external_ref_id INTEGER PRIMARY KEY AUTOINCREMENT,
    artist_id TEXT NOT NULL,
    source TEXT NOT NULL,
    external_id TEXT NOT NULL,
    external_url TEXT NULL,
    source_payload_json TEXT NULL,
    confidence REAL NULL,
    is_primary INTEGER NOT NULL DEFAULT 0,
    first_seen_utc TEXT NOT NULL,
    last_seen_utc TEXT NOT NULL,
    FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE,
    UNIQUE(source, external_id),
    UNIQUE(artist_id, source, external_id)
);

CREATE TABLE IF NOT EXISTS artist_alias (
    artist_alias_id INTEGER PRIMARY KEY AUTOINCREMENT,
    artist_id TEXT NOT NULL,
    alias_name TEXT NOT NULL,
    alias_name_norm TEXT NOT NULL,
    alias_type TEXT NULL,
    valid_from TEXT NULL,
    valid_to TEXT NULL,
    FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE,
    UNIQUE(artist_id, alias_name_norm)
);

CREATE TABLE IF NOT EXISTS artist_relationship (
    artist_relationship_id INTEGER PRIMARY KEY AUTOINCREMENT,
    from_artist_id TEXT NOT NULL,
    to_artist_id TEXT NOT NULL,
    relationship_type TEXT NOT NULL,
    start_date TEXT NULL,
    end_date TEXT NULL,
    source TEXT NULL,
    confidence REAL NULL,
    notes TEXT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    FOREIGN KEY(from_artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE,
    FOREIGN KEY(to_artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE,
    UNIQUE(from_artist_id, to_artist_id, relationship_type)
);

CREATE TABLE IF NOT EXISTS release_artist_credit (
    release_artist_credit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    release_id TEXT NOT NULL,
    artist_id TEXT NULL,
    credited_name TEXT NOT NULL,
    role TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    join_phrase TEXT NULL,
    FOREIGN KEY(release_id) REFERENCES release(release_id) ON DELETE CASCADE,
    FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE SET NULL,
    UNIQUE(release_id, credited_name, role, sort_order)
);

CREATE INDEX IF NOT EXISTS ix_artist_display_name_norm ON artist(display_name_norm);
CREATE INDEX IF NOT EXISTS ix_artist_kind ON artist(artist_kind);
CREATE INDEX IF NOT EXISTS ix_artist_external_ref_artist ON artist_external_ref(artist_id);
CREATE INDEX IF NOT EXISTS ix_artist_external_ref_source_external ON artist_external_ref(source, external_id);
CREATE INDEX IF NOT EXISTS ix_artist_alias_name_norm ON artist_alias(alias_name_norm);
CREATE INDEX IF NOT EXISTS ix_artist_relationship_from_type ON artist_relationship(from_artist_id, relationship_type);
CREATE INDEX IF NOT EXISTS ix_artist_relationship_to_type ON artist_relationship(to_artist_id, relationship_type);
CREATE INDEX IF NOT EXISTS ix_release_artist_credit_release ON release_artist_credit(release_id);
CREATE INDEX IF NOT EXISTS ix_release_artist_credit_artist ON release_artist_credit(artist_id);

CREATE TABLE IF NOT EXISTS artist_embedding_document (
    artist_embedding_document_id INTEGER PRIMARY KEY AUTOINCREMENT,
    artist_id TEXT NOT NULL,
    document_text TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE,
    UNIQUE(artist_id)
);

CREATE TABLE IF NOT EXISTS artist_embedding (
    artist_embedding_id INTEGER PRIMARY KEY AUTOINCREMENT,
    artist_id TEXT NOT NULL,
    model_name TEXT NOT NULL,
    model_version TEXT NOT NULL,
    embedding BLOB NOT NULL,
    dimensions INTEGER NOT NULL,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(artist_id) REFERENCES artist(artist_id) ON DELETE CASCADE,
    UNIQUE(artist_id, model_name, model_version)
);

CREATE INDEX IF NOT EXISTS ix_artist_embedding_document_artist ON artist_embedding_document(artist_id);
CREATE INDEX IF NOT EXISTS ix_artist_embedding_artist ON artist_embedding(artist_id);
CREATE INDEX IF NOT EXISTS ix_artist_embedding_model ON artist_embedding(model_name, model_version);
```

## Migration Plan (Phased)

1. Add `artist`, `artist_external_ref`, `artist_alias`, and `artist_relationship`.
2. Add `release_artist_credit` if not already present.
3. Ingest external provider artist identities into `artist_external_ref` (Beatport first).
4. Backfill canonical artists from existing release/track credit text.
5. Populate `release_artist_credit.artist_id` where canonical matches are available.
6. Add `track_artist_credit` when canonical track schema is introduced.

## Open Decisions
