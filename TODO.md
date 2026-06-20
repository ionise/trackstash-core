# trackstash-core TODO

## Ecosystem boundaries + provider abstraction

**Goal:** Keep scanning local and SQLite-backed for now while making catalog and matching modules storage-provider agnostic.

## Next implementation plan

1. Finalize shared core semantics before more feature wiring.
   - Finish `docs/storage-interface.md` so it matches the implemented contracts.
   - Reconcile the entity schema docs with the actual SQLite migration behavior, especially around delete semantics and cascade expectations.
   - Decide whether missing helper utilities should be introduced as dedicated abstractions or left inline where already simple.

2. Build the core-first delete foundation.
   - Document blocker vs owned-cleanup rules for label, artist, release, and recording.
   - Define dependency-analysis and delete result models in shared core contracts.
   - Implement SQLite-backed dependency analysis, dry-run reporting, and transactional delete orchestration.
   - Add integration coverage for blocked deletes, successful deletes, and asymmetric join-table behavior.

3. Fill the largest schema and operational gaps.
   - Decide whether `media_file_fingerprint` and media-file embedding tables remain required or should be removed from the plan.
   - Add any still-required tables or revise the TODO/docs to match the current architecture.
   - Add core guardrails such as orphan checks, duplicate checks, and audit reporting hooks.

4. Return to higher-level ingestion and matching work once the shared foundation is stable.
   - Revisit Beatport-specific ingestors in core only if they still belong here.
   - Keep embedding and matching pipeline milestones behind the now-stable contracts and schema decisions.

### 1) Document and freeze module boundaries

- [x] Create `trackstash-core` as the umbrella home for shared docs, shared code, and provider abstractions
- [x] Confirm `trackstash-scan` stays scan/extraction only
- [x] Confirm `trackstash-catalog` owns canonical entities and external refs
- [x] Confirm `trackstash-match` owns file-to-recording resolution
- [x] Confirm `trackstash-tag` owns tag writeback
- [x] Confirm `trackstash-organize` and `trackstash-transcode` remain separate utilities

### 2) Introduce a storage abstraction layer

- [x] Define repository interfaces for labels, releases, artists, recordings, media files, and matches
- [x] Define provider adapter contracts for SQLite and future backends
- [ ] Ensure catalog/matching code depends on interfaces rather than direct SQL helpers
- [ ] Finalize the storage interface document in `docs/storage-interface.md`

### 3) Keep SQLite as the development backend

- [ ] Preserve SQLite for `trackstash-scan`
- [x] Use SQLite for catalog/matching during local development
- [x] Add migration points so backend swaps do not require scanner rewrites

### 4) Plan future backend support

- [x] Document relational backend assumptions that can be shared
- [x] Document document-store-specific constraints for a future cloud backend
- [x] Keep Cosmos-style layouts isolated from scanner concerns
- [ ] Evaluate a core-level catalog registry abstraction (logical catalog name -> provider descriptor/connection) so callers can target catalogs without passing raw DB paths

---

## Canonical metadata + matching pipeline

**Goal:** Ingest Beatport labels/releases/artists/recordings into canonical tables, generate embeddings, and match local media files to canonical recordings.

### Catalog vs Library terminology (working definition)

- Catalog: canonical music knowledge (labels/artists/releases/recordings and external references), including items not owned locally.
- Library: owned media-file inventory and extracted metadata for local files.
- Keep these concepts separate in docs, contracts, and command surfaces.
- Catalog workflows should remain independent of local file ownership state.

### Embedding implementation focus (current phase)

- Focus now: text embeddings for Catalog entities.
- Audio embeddings are a later phase and should not block current Catalog text embedding delivery.

### Audio embedding follow-up (future phase)

- Canonical recording audio may be stored in cloud blob/object storage for backup and reproducible processing.
- Audio embedding jobs should reference canonical audio locations (URI + content hash) rather than large inline payloads.
- Re-embed policy should remain deterministic: regenerate on source hash change or model/version change.
- GPU-heavy audio embedding execution is expected to run in cloud workers.
- Worker topology may evolve to router + specialized workers (text vs audio) once multiple modalities are active.

Future exploration note: backup + embedding lifecycle for Library media

- Ingest to hot tier first, compute hashes/metadata, and run embedding jobs while blobs are warm.
- After successful embedding and validation, transition canonical blobs to cold/archive tiers for long-term cost control.
- On re-upload, dedupe by canonical content hash and keep existing blob when binary-equivalent.
- If a re-upload is the same recording but higher quality, treat it as a candidate canonical replacement using a deterministic quality policy.
- Prefer immutable blob keys by content hash plus a separate logical recording-to-canonical-blob mapping so upgrades are reversible and auditable.

### 1) Finalize schema decisions

- [ ] Freeze normalization rules in `docs/schema-conventions.md` (`*_norm`, punctuation, whitespace, casing)
- [ ] Freeze matching thresholds (auto-accept score, candidate count, embedding distance cutoff)
- [x] Freeze re-embedding policy (`document_hash` change and/or model version change)

### 2) Implement additive schema migrations

- [x] Add migration plumbing (version table + ordered migration scripts)
- [x] Migrate canonical tables: label, release, artist, recording and external ref tables
- [x] Migrate credit/link tables: release_artist_credit, release_label_link, release_recording, recording_artist_credit, relationship tables
- [x] Migrate embedding tables for release, artist, recording
- [ ] Migrate media matching tables: media_file_fingerprint, media_file_recording_match, media_file_recording_candidate
- [ ] Migrate media metadata embedding tables: media_file_embedding_document, media_file_embedding
- [ ] Add all planned indexes and FK constraints

### 3) Build shared utility layer

- [ ] Implement canonical GUID generation helper
- [x] Implement normalization helpers used by all ingestors
- [ ] Implement UTC timestamp helper for all writes
- [ ] Implement JSON payload serializer helper for source payload columns

### 4) Implement Beatport ingestion (idempotent upserts)

- [ ] Labels ingestor: canonical row + label_external_ref + alias + embedding doc seed
- [ ] Artists ingestor: canonical row + artist_external_ref + alias + relationships (where available)
- [ ] Releases ingestor: canonical row + release_external_ref + release_artist_credit + release_label_link
- [ ] Recordings ingestor: canonical row + recording_external_ref + recording_artist_credit + release_recording + recording_relationship
- [ ] Ensure all ingestors are rerunnable (safe upsert behavior)

### 5) Implement embedding jobs

- [ ] Build document text materialization for release/artist/recording (Catalog-first scope)
- [ ] Generate text embeddings and persist with `model_name`, `model_version`, `dimensions`
- [ ] Skip unchanged rows via `document_hash`
- [ ] Add re-embed command for model upgrades
- [ ] Add optional cloud audio embedding pipeline (Library/media scope) using canonical blob/object references

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

### 8) Define entity deletion semantics

- [x] Document canonical delete rules for label, artist, release, and recording in shared core docs
- [x] Reconcile delete behavior described in schema docs with the current SQLite migration behavior
- [x] Classify dependencies as blocking references vs entity-owned cleanup rows
- [x] Decide join-table asymmetry rules, including release-side cleanup vs recording-side blockers for `release_recording`
- [x] Define shared result models for delete dependency analysis and dry-run reporting
- [x] Define shared core contracts for dependency analysis and transactional entity deletion
- [x] Implement SQLite-backed dependency analysis for label, artist, release, and recording deletes
- [x] Implement SQLite-backed transactional delete orchestration for safe owned-row cleanup
- [x] Add integration tests for blocked deletes, dry-run analysis, and successful deletes

### 9) Add recycle-bin retention for deletes

- [x] Define a core recycle-bin requirement for canonical entity deletes before implementation begins
- [x] Decide which deleted rows are retained as tombstones versus fully purged later
- [x] Define restore prerequisites and limits for labels, artists, releases, and recordings
- [x] Add shared result models for recycle-bin capture and restore planning
- [x] Define shared core contracts for delete journaling, tombstone lookup, and restore orchestration
- [x] Add entity_tombstone table to SQLite migration with proper indexes
- [x] Implement tombstone capture and lookups in SqliteEntityDeleteService
- [x] Implement restore analysis and validation in SqliteEntityDeleteService
- [x] Implement restore data hydration (deserialize JSON back to canonical entities and owned rows)
- [x] Add integration tests for delete with tombstone capture and restore safety checks

### 10) Pilot then full rollout

- [ ] Run pilot ingest on a curated sample containing collabs/remixes/compilations edge cases
- [ ] Tune thresholds from pilot outcomes
- [ ] Run full ingest + embedding backfill + matching
- [ ] Validate tag writeback plan (`trackstash_recording_id` required, `trackstash_media_file_id` optional)

## Future ideas (parking lot)

- Consider adding a lightweight core catalog registry interface for multi-catalog routing: callers provide `catalogName`, core resolves provider/backend/connection details.
- Keep this optional and config-driven so single-catalog local workflows remain simple.
- Add explicit Library module docs and commands as media scanning expands, keeping Catalog and Library responsibilities separate.
