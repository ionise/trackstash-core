# Label Schema Design

This document captures the intended schema design for canonical labels, label source links, and optional label embedding storage.

Status: **core tables partially implemented**. The `label`, `label_external_ref`, and `label_alias` tables are implemented in `src/TrackStash.Core.Sqlite/Migrations.cs` with a simplified column set. The embedding tables (`label_embedding_document`, `label_embedding`) are not implemented; embeddings use the shared `embedding_document` table instead. The DDL skeleton at the bottom of this document reflects the original design intent and **does not match the actual migration**.

Key differences from the actual migration:

- Column names differ (`canonical_name`/`canonical_name_norm` in docs vs `name`/`normalized_name` in migration).
- `ON DELETE CASCADE` described here is **not present** in the actual migration, which uses bare `REFERENCES` (effectively `NO ACTION`).
- The per-entity embedding tables are replaced by the shared `embedding_document` table.

See [delete-semantics.md](delete-semantics.md) for the delete rules and full reconciliation note.

## Implemented Query and Conditional-update Contracts

[LabelQuery, LabelPage, and LabelUpdate](../src/TrackStash.Core/Storage/LabelQueries.cs) and [ILabelRepository](../src/TrackStash.Core/Storage/Contracts.cs) provide paged candidate queries, create-only insertion, and atomic expected-version updates. Name/provider matches can be ambiguous and are not unique-identity constraints. Single-result provider lookup now rejects ambiguity explicitly; normalized-name lookup remains a legacy first-match operation, not a merge decision.

SQLite migration 3 adds `label.version`, backfills it, and installs triggers for scalar label changes and owned alias/reference changes. Tokens are opaque, persisted, transactionally rolled back, and refreshed on insertion/restore, not derived from timestamps or incremented from caller input. Legacy unconditional writers invalidate read tokens without becoming conditional writers themselves. Child changes do not necessarily update the label's `updated_utc`.

The adapter normalizes actual name/alias text for substring search using the shared normalizer, including aliases with null normalized values. It executes filtered count and page queries in the same unit of work and returns lightweight list items without child hydration. Conditional edits only set supplied fields and server-owned normalization/update time; they preserve provider payloads and identity children. A supplied profile replaces its complete editable metadata, not aliases or provider references.

Other adapters must preserve these contract semantics and provide equivalent invalidation for all supported write paths; SQLite triggers are an implementation detail. Migrate existing catalogs explicitly using bootstrap before running code that reads label versions. See the [catalog foundation contract](../../trackstash-catalog/docs/api-design.md#implemented-foundation) for validation, sorting, paging, version scope, and application projections.

## Manual Profiles and Future Enrichment Foundation

Migration 5 adds a separate `label_profile` and `label_web_link` collection, exposed by [LabelProfile](../src/TrackStash.Core/Storage/LabelProfiles.cs). It does not rewrite existing label rows or provider identities. Missing profiles read as empty metadata: unknown lifecycle, unreviewed status, not favourite, and no links.

Supported conditional edits cover description, two-letter uppercase country-code format (not ISO membership verification), city, founded year, lifecycle (`unknown`, `active`, `inactive`, `closed`), personal notes, favourite, review status (`unreviewed`, `in_progress`, `reviewed`), and typed HTTP(S) web links. Links are exact-URL unique, at most 100, with optional title and source URL. Website links are not verified identity references: adding a Discogs URL does not populate `label_external_ref`. Existing unconditional imports/upserts deliberately preserve curated profiles; use `LabelUpdate.SetProfile` with the original aggregate token to replace them.

Each successful explicit profile edit records immutable before/after JSON in `label_profile_revision`, with original label ID, time, and actor `manual` (method attribution, not an authenticated individual). Profiles, links, revisions, observations, artwork, and both relationship endpoints invalidate aggregate versions; all new tables reject retired IDs. Safe deletion blocks metadata-bearing labels until complete relational snapshot/restore semantics exist.

Schema-only foundations, with no public writers or GUI management yet:

- `label_observation`: field/value JSON, provider/source identity and URL, retrieval time, attribution, licence, retention deadline, and reviewed decision. Future source providers should submit observations for review rather than silently overwrite canonical metadata.
- `label_artwork`: local uploaded content BLOB, media type/hash, source URL, attribution, and required usage rights. No remote image retrieval, upload, or serving is implemented.
- `label_relationship`: distinct existing label identities, child → parent `imprint_of` or label → distributor `distributed_by`, with source, source URL, and notes. Self-links and duplicate typed edges are rejected. Hierarchy/cycle validation belongs to the future write workflow; dates/territories are not included initially.

Merge previews combine compatible scalar metadata, treat unknown/unreviewed as unset, OR favourites, and union URLs. Conflicting nonempty values, link kinds/title/source, or an over-limit union block apply. Manual history and observations transfer while preserving original IDs; full relational rows are included in merge audits and dependency snapshots. Artwork and relationship dependencies conservatively block merge until their policies are implemented.

Automated enrichment and a separate pluggable-provider module are deferred. Shared embedding storage exists, but label document generation, profile-edit embedding refresh, and semantic label search are not implemented. Personal notes are not an implicit embedding input.

## Implemented Reviewed Label Merges

[Merge and identity-resolution contracts](../src/TrackStash.Core/Storage/LabelMerges.cs) provide pairwise preview/apply and explicit active/retired/missing identity results. The existing external-reference composite key already supports several Beatport IDs per label; merge does not introduce provider-specific columns or unique normalized names.

SQLite migration 4 adds `label_merge_operation`, append-only `label_merge_audit`, and permanent `retired_label` mappings. It extends aggregate version invalidation to release-label links and guards against retired-ID reuse through insert/upsert, restore, child/link writers, and embedding work. Survivor deletion is blocked while retired identities resolve to it. Later merges retarget historical mappings atomically; normal reads can explicitly resolve retired IDs without silently redirecting writes.

Apply runs within an immediate transaction, revalidates the full reviewed dependency digest, and uses a savepoint so a failed apply cannot leave partial transfers even if a caller catches the error and commits. Known embeddings are invalidated and their models queued for forced regeneration; processing embedding jobs and unsupported incoming foreign-key dependencies block merge. The before-state audit snapshots actual relational rows, including payloads, links, derived rows/vectors, flags, and timestamps, rather than relying on deletion tombstones.

All adapters must implement equivalent atomicity, conflict/retirement semantics, and retry guarantees. Schema-version inspection does not create schema tables on an uninitialized database. Migration is explicit and never chooses duplicate identities automatically. See the [catalog merge contract](../../trackstash-catalog/docs/api-design.md#reviewed-label-merge-design) for collision rules, audit access/retention, HTTP status codes, GUI review, import safeguards, and exclusions.

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
- Support parent/imprint/brand relationships between labels without collapsing them into aliases.
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

### 4) `label_relationship` (recommended)

Purpose: model real label-to-label structure such as parent labels, imprints,
sub-brands, regional brands, and distribution relationships.

This is not an alias table. Use `label_alias` when two names refer to the same
canonical label; use `label_relationship` when two distinct labels have a real
business relationship.

Suggested columns:

- `label_relationship_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `parent_label_id TEXT NOT NULL`
- `child_label_id TEXT NOT NULL`
- `relationship_type TEXT NOT NULL`
  - Examples: `parent_of`, `imprint_of`, `brand_of`, `distributed_by`, `regional_brand_of`, `catalog_brand_of`.
- `role TEXT NULL`
  - Optional human-readable role, such as `main label`, `dance imprint`, or `digital brand`.
- `territory TEXT NULL`
  - Optional territory or market scope, for example `worldwide`, `uk`, `us`, `eu`.
- `genre TEXT NULL`
  - Optional genre scope, for example `house`, `drum-and-bass`, `ambient`.
- `effective_from_utc TEXT NULL`
- `effective_to_utc TEXT NULL`
- `source TEXT NULL`
- `confidence REAL NULL`
- `notes TEXT NULL`
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(parent_label_id) REFERENCES label(label_id) ON DELETE RESTRICT`
- `FOREIGN KEY(child_label_id) REFERENCES label(label_id) ON DELETE CASCADE`
- `UNIQUE(parent_label_id, child_label_id, relationship_type)`
- Index on `parent_label_id`
- Index on `child_label_id`
- Index on `(relationship_type, territory)`

Usage guidance:

- `parent_of` is the canonical structural relationship when one label owns or
  oversees another.
- `imprint_of` is the preferred relationship when the child is a branded sublabel.
- `brand_of` is useful when two labels are distinct market-facing brands under
  shared ownership.
- `distributed_by` can capture a label that is represented by another label or
  distributor in a territory or time period.
- Sideways relationships should be explicit and directional; do not infer them
  from aliases or from release links.

Delete guidance:

- Deleting a parent label should be blocked while child labels still reference it.
- Deleting a child label should remove its relationship rows as owned cleanup.
- The delete semantics document should treat this table as an asymmetrical join
  structure once it is implemented.

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
