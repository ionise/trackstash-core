# trackstash-core TODO

## Ecosystem boundaries + provider abstraction

**Goal:** Keep scanning local and SQLite-backed for now while making catalog and matching modules storage-provider agnostic.

### 1) Document and freeze module boundaries

- [ ] Create `trackstash-core` as the umbrella home for shared docs, shared code, and provider abstractions
- [ ] Confirm `trackstash-scan` stays scan/extraction only
- [ ] Confirm `trackstash-catalog` owns canonical entities and external refs
- [ ] Confirm `trackstash-match` owns file-to-recording resolution
- [ ] Confirm `trackstash-tag` owns tag writeback
- [ ] Confirm `trackstash-organize` and `trackstash-transcode` remain separate utilities

### 2) Introduce a storage abstraction layer

- [ ] Define repository interfaces for labels, releases, artists, recordings, media files, and matches
- [ ] Define provider adapter contracts for SQLite and future backends
- [ ] Ensure catalog/matching code depends on interfaces rather than direct SQL helpers
- [ ] Finalize the storage interface document in `docs/storage-interface.md`

### 3) Keep SQLite as the development backend

- [ ] Preserve SQLite for `trackstash-scan`
- [ ] Use SQLite for catalog/matching during local development
- [ ] Add migration points so backend swaps do not require scanner rewrites

### 4) Plan future backend support

- [ ] Document relational backend assumptions that can be shared
- [ ] Document document-store-specific constraints for a future cloud backend
- [ ] Keep Cosmos-style layouts isolated from scanner concerns

---

## Canonical metadata + matching pipeline

**Goal:** Ingest Beatport labels/releases/artists/recordings into canonical tables, generate embeddings, and match local media files to canonical recordings.

### 1) Finalize schema decisions

- [ ] Freeze normalization rules in `docs/schema-conventions.md` (`*_norm`, punctuation, whitespace, casing)
- [ ] Freeze matching thresholds (auto-accept score, candidate count, embedding distance cutoff)
- [ ] Freeze re-embedding policy (`document_hash` change and/or model version change)

### 2) Implement additive schema migrations

- [ ] Add migration plumbing (version table + ordered migration scripts)
- [ ] Migrate canonical tables: label, release, artist, recording and external ref tables
- [ ] Migrate credit/link tables: release_artist_credit, release_label_link, release_recording, recording_artist_credit, relationship tables
- [ ] Migrate embedding tables for release, artist, recording
- [ ] Migrate media matching tables: media_file_fingerprint, media_file_recording_match, media_file_recording_candidate
- [ ] Migrate media metadata embedding tables: media_file_embedding_document, media_file_embedding
- [ ] Add all planned indexes and FK constraints

### 3) Build shared utility layer

- [ ] Implement canonical GUID generation helper
- [ ] Implement normalization helpers used by all ingestors
- [ ] Implement UTC timestamp helper for all writes
- [ ] Implement JSON payload serializer helper for source payload columns

### 4) Implement Beatport ingestion (idempotent upserts)

- [ ] Labels ingestor: canonical row + label_external_ref + alias + embedding doc seed
- [ ] Artists ingestor: canonical row + artist_external_ref + alias + relationships (where available)
- [ ] Releases ingestor: canonical row + release_external_ref + release_artist_credit + release_label_link
- [ ] Recordings ingestor: canonical row + recording_external_ref + recording_artist_credit + release_recording + recording_relationship
- [ ] Ensure all ingestors are rerunnable (safe upsert behavior)

### 5) Implement embedding jobs

- [ ] Build document text materialization for release/artist/recording/media_file
- [ ] Generate MiniLM embeddings and persist with `model_name`, `model_version`, `dimensions`
- [ ] Skip unchanged rows via `document_hash`
- [ ] Add re-embed command for model upgrades

### 6) Implement media-to-recording matching pipeline

- [ ] Strategy 1: ISRC exact
- [ ] Strategy 2: ISRC via Beatport lookup
- [ ] Strategy 3: AcoustID exact
- [ ] Strategy 4: duration + BPM heuristic
- [ ] Strategy 5: embedding similarity
- [ ] Strategy 6: fuzzy title/artist
- [ ] Persist top match + ranked candidates + evidence JSON + strategy + score
- [ ] Support user override and rejection tracking

### 7) Add quality and operational guardrails

- [ ] Add orphan FK checks and duplicate constraint checks
- [ ] Add ingest audit reports (counts by table/source and change deltas)
- [ ] Add ambiguous match report export for manual review
- [ ] Add commands: full ingest, incremental sync, rebuild embeddings, recompute matches

### 8) Pilot then full rollout

- [ ] Run pilot ingest on a curated sample containing collabs/remixes/compilations edge cases
- [ ] Tune thresholds from pilot outcomes
- [ ] Run full ingest + embedding backfill + matching
- [ ] Validate tag writeback plan (`trackstash_recording_id` required, `trackstash_media_file_id` optional)