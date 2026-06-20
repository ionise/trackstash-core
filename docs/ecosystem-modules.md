# TrackStash Ecosystem Modules

This document defines the current and proposed module boundaries for TrackStash so each component stays focused on one job and can evolve independently.

Proposed umbrella home:
- `trackstash-core` should eventually host shared architecture docs, storage abstractions, and cross-cutting code that is not specific to scanning, cataloging, matching, or tagging.

## Design Principles

- Keep `trackstash-scan` local and operational: discover files, extract metadata, compute hashes/fingerprints, and persist scan state.
- Keep cataloging and matching separate from scanning: canonical entities, Beatport ingestion, embeddings, and file-to-recording resolution belong in a catalog/matching layer.
- Treat database access as a provider boundary: SQLite is the default development backend, but catalog-facing code should depend on interfaces, not on SQLite APIs directly.
- Allow future backends: another relational database should be a straightforward adapter swap; a document store such as Azure Cosmos DB should be possible via a dedicated adapter, not by leaking storage assumptions into the scanner.

Terminology used in this repo:

- Catalog: canonical music domain data (labels/artists/releases/recordings), including music not owned locally.
- Library: owned media-file data (local file inventory, extracted metadata, fingerprints, and file-level lifecycle state).

## Current Modules

### `psMusicTagger`

Purpose:
- Extract embedded metadata from media files.
- Measure duration and related audio properties.

Used by:
- `trackstash-scan`
- `trackstash-organize`

### `psAcoustID`

Purpose:
- Generate AcoustID-compatible fingerprints.

Used by:
- `trackstash-scan`

### `psBeatPort`

Purpose:
- Query Beatport labels, artists, releases, and tracks.
- Provide cached catalog access and full entity details.

Used by:
- Future catalog ingestion and matching workflows.

### `trackstash-scan`

Purpose:
- Walk filesystems for supported audio media.
- Capture file path, size, timestamps, hash, metadata, duration, and fingerprint.
- Persist scan output and scan checkpoints.

Scope boundary:
- Should remain focused on scan/extraction concerns.
- Should not own canonical catalog logic, embeddings, or Beatport ingestion workflows beyond what is needed to populate scan metadata.

### `trackstash-organize`

Purpose:
- Move or copy files into a target layout.
- Reuse scan metadata to choose destination paths.

Dependency note:
- Depends on `trackstash-scan` as the metadata prerequisite.

### `trackstash-transcode`

Purpose:
- Transcode or normalize media files.

Status:
- Exists as its own module family; should remain separate from scan/catalog concerns.

## Proposed Modules

### `trackstash-core`

Purpose:
- Act as the umbrella home for shared code and shared documentation.
- Host storage abstractions, common domain interfaces, and ecosystem-level architecture notes.

Responsibilities:
- Define reusable contracts for repositories and providers.
- Hold shared utilities that are not scanner-specific.
- Serve as the documentation anchor for the wider TrackStash ecosystem.

Scope boundary:
- Should not own filesystem scanning.
- Should not own Beatport ingestion.
- Should not own tagging or organization logic.

Documentation note:
- The shared design docs were migrated here from `trackstash-scan/docs` so `trackstash-core` is the single source of truth.

### `trackstash-catalog`

Purpose:
- Own canonical music entities: labels, releases, artists, recordings.
- Own provider references, aliases, relationships, and embeddings.

Terminology note:
- `trackstash-catalog` is Catalog scope, not Library scope.
- Catalog data may include recordings that are not present in local owned files.

Responsibilities:
- Ingest and normalize Beatport source data.
- Persist canonical entities and external references.
- Store embedding documents and vectors.
- Expose read APIs for other modules.

Database boundary:
- Must use a storage abstraction rather than hard-coding SQLite queries.

### `trackstash-match`

Purpose:
- Match media files to canonical recordings.

Responsibilities:
- Consume scan output plus catalog data.
- Run deterministic matching strategies first, then embeddings and fuzzy ranking.
- Persist best matches, candidate lists, scores, and evidence.
- Support manual review and overrides.

Boundary note:
- `trackstash-match` is where Library files are linked to Catalog entities.

### `trackstash-tag`

Purpose:
- Write canonical and match-derived identifiers back into the media files.

Responsibilities:
- Update tags such as canonical recording ID, catalog IDs, match score, and source provenance.
- Keep writeback separate from matching so tagging can be rerun independently.

### `trackstash-core` or `trackstash-storage`

Purpose:
- Provide the shared repository / provider abstraction used by catalog and matching workflows.

Responsibilities:
- Define interfaces such as `ILabelStore`, `IReleaseStore`, `IArtistStore`, `IRecordingStore`, `IMediaFileStore`, and `IMatchStore`.
- Provide adapter implementations for SQLite today and other backends later.

## Suggested Module Boundaries

### Scanner Layer

- `trackstash-scan`
- `psMusicTagger`
- `psAcoustID`

### Catalog Layer

- `psBeatPort`
- `trackstash-catalog`
- `trackstash-core` / `trackstash-storage`

### Matching Layer

- `trackstash-match`
- `trackstash-catalog`
- `trackstash-core` / `trackstash-storage`

### Writeback / Organization Layer

- `trackstash-tag`
- `trackstash-organize`
- `trackstash-transcode`

## Storage Strategy

Short term:
- Use SQLite for `trackstash-scan` because it is local, fast, and close to the files.
- Use SQLite for the catalog/matching layer during development.

Medium term:
- Keep the catalog/matching modules behind interface-based repositories so another relational backend can be introduced without rewriting the scanner.

Long term:
- If a cloud database is introduced, treat it as an adapter implementing the shared storage interfaces.
- Keep Cosmos-style document layouts isolated from the scanner so the local scan pipeline does not inherit cloud-specific assumptions.

## Existing vs Planned Catalog

### Existing Today

- `psBeatPort`
- `psMusicTagger`
- `psAcoustID`
- `trackstash-scan`
- `trackstash-organize`
- `trackstash-transcode`

### Still To Build

- `trackstash-core`
- `trackstash-catalog`
- `trackstash-storage`
- `trackstash-match`
- `trackstash-tag`

## Implementation Order

1. Keep `trackstash-scan` as the local source of truth for file inventory and extraction.
2. Introduce `trackstash-core` as the umbrella home for shared docs, shared code, and provider abstractions.
3. Build `trackstash-catalog` on top of that abstraction.
4. Build `trackstash-match` to resolve files to recordings.
5. Build `trackstash-tag` to write IDs and provenance back to files.
6. Keep `trackstash-organize` and `trackstash-transcode` separate and focused on their existing jobs.
