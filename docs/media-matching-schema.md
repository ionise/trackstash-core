# Media File Matching Schema (Planned)

This document captures the planned Trackstash schema additions for matching media files to canonical recordings using multiple matching strategies (fingerprint, metadata heuristics, embeddings, manual override).

Status: design only. This extends the existing `media_file` and `metadata` tables in `Initialize-TrackstashDatabase`.

The separate canonical Catalog now provides an initial **explicit reviewed media-link workflow** using its existing `media_file`, `media_file_recording_match` and schema-13 immutable enrichment review/audit tables. Scanner database path and integer file ID form a source-scoped media identity, not a canonical Recording ID. Verified SHA-256 and metadata/scoped-sidecar evidence support Manual associations; `Score`/`Confidence` remain zero rather than invented certainty. Storage lists accepted/manual copies with a 101-row overflow sentinel and checks *all* accepted/manual associations of identical hashes for conflicting Recordings, case-insensitively. This does not implement the scanner embeddings/automatic strategies planned below, replace existing associations, copy audio or assert ownership. See [Catalog workflow](../../trackstash-catalog/README.md#reviewed-filesystem-media-links).

Related documents:

Catalog also retains a separate `MediaReleaseGroup` metadata association as a Release-scoped schema-13 review plus apply audit. `IEnrichmentRepository.ListAppliedReviewsAsync` lists applied typed application payloads with a 1001-row overflow sentinel; Catalog treats more than 1000 as an explicit limit error. Association membership and Release context are frozen/rechecked, with no new schema, Recording links, placement writes or scanner writes.

- [recording-schema.md](recording-schema.md) for canonical recording identity tables.
- [label-schema.md](label-schema.md) for label-domain tables.
- [release-schema.md](release-schema.md) for release-domain tables.
- [artist-schema.md](artist-schema.md) for artist-domain tables.
- [schema-conventions.md](schema-conventions.md) for shared ID, normalization, timestamp, and `is_primary` rules.

## Goals

- Link each media file to a canonical recording with high confidence.
- Support multiple matching strategies (ISRC exact, AcoustID fingerprint, duration+BPM heuristic, embedding similarity).
- Track match evidence and reasoning for audit and debugging.
- Enable manual user overrides for ambiguous or incorrect matches.
- Rank candidate matches by confidence score.

## Existing Schema

The existing database already provides:

- `media_file` table: file path, content_hash, acoustid_submission_hash, duration_seconds, format, size_bytes, timestamps.
- `metadata` table: per-file artist/title/album/label/release/isrc/barcode/catalog_number/track_number/disc_number/bpm/musical_key/genre/year metadata extracted from tags or file properties.

This schema adds five new tables to connect files to canonical recordings, persist media-file embeddings, and track match decisions.

## Media Metadata Embeddings

### 1) `media_file_embedding_document`

Purpose: stores the canonical text input used to generate embeddings for each media file.

Suggested columns:

- `media_file_embedding_document_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `media_file_id INTEGER NOT NULL`
  - FK to media_file.
- `document_text TEXT NOT NULL`
  - Normalized text input for embedding, for example title + artist + album + label + mix name.
- `document_hash TEXT NOT NULL`
  - Hash of `document_text` so unchanged files are not re-embedded.
- `created_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(media_file_id) REFERENCES media_file(media_file_id) ON DELETE CASCADE`
- `UNIQUE(media_file_id)`
- Index on `document_hash`

### 2) `media_file_embedding`

Purpose: versioned embedding vectors for media-file metadata.

Suggested columns:

- `media_file_embedding_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `media_file_id INTEGER NOT NULL`
- `model_name TEXT NOT NULL`
  - Examples: `minilm-l6-v2`, `minilm-l12-v2`.
- `model_version TEXT NOT NULL`
  - Full semantic version, e.g., `1.0.0`.
- `embedding BLOB NOT NULL`
  - Serialized float32 vector (dimensions * 4 bytes).
- `dimensions INTEGER NOT NULL`
- `created_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(media_file_id) REFERENCES media_file(media_file_id) ON DELETE CASCADE`
- `UNIQUE(media_file_id, model_name, model_version)`
- Index on `media_file_id`
- Index on `(model_name, model_version)`

## Fingerprint-Based Matching

### 3) `media_file_fingerprint`

Purpose: structured storage for multiple audio fingerprints per media file.

Suggested columns:

- `media_file_fingerprint_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `media_file_id INTEGER NOT NULL`
  - FK to media_file.
- `fingerprint_type TEXT NOT NULL`
  - Examples: `acousticid`, `puid`, `chromaprint`, `musicbrainz`.
- `fingerprint_value TEXT NOT NULL`
  - Hex-encoded or base64 fingerprint value.
- `confidence REAL NULL`
  - Confidence of the fingerprint extraction (0-1).
- `created_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(media_file_id) REFERENCES media_file(media_file_id) ON DELETE CASCADE`
- `UNIQUE(media_file_id, fingerprint_type)`
- Index on `media_file_id`
- Index on `fingerprint_type`

### 4) `media_file_recording_match`

Purpose: tracks the best match between a media file and a canonical recording, with confidence, strategy, and evidence.

Suggested columns:

- `media_file_recording_match_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `media_file_id INTEGER NOT NULL`
- `recording_id TEXT NOT NULL`
  - FK to recording.recording_id in canonical recording table.
- `match_score REAL NOT NULL`
  - Confidence score 0-1 (1.0 = certain, 0.0 = not matched).
- `match_strategy TEXT NOT NULL`
  - How the match was determined. Examples: `isrc_exact`, `isrc_beatport_lookup`, `acousticid_exact`, `duration_bpm_heuristic`, `embedding_similarity`, `title_artist_fuzzy`, `manual_override`, `user_correction`.
- `evidence_json TEXT NULL`
  - JSON object with evidence details: `{"isrc_match": true, "duration_diff_ms": 150, "bpm_match": true, "embedding_distance": 0.15, "acousticid_match": true}`.
- `notes TEXT NULL`
  - User notes for manual overrides (e.g., "User confirmed match despite title mismatch").
- `is_user_override INTEGER NOT NULL DEFAULT 0`
  - 1 if this match was set manually by user, 0 if algorithmic.
- `matched_utc TEXT NOT NULL`
- `updated_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(media_file_id) REFERENCES media_file(media_file_id) ON DELETE CASCADE`
- `FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE`
- `UNIQUE(media_file_id, recording_id)`
- Index on `media_file_id`
- Index on `recording_id`
- Index on `match_score DESC` (for finding high-confidence matches)

### 5) `media_file_recording_candidate`

Purpose: optional—stores ranked list of candidate matches for user review/disambiguation.

Suggested columns:

- `media_file_recording_candidate_id INTEGER PRIMARY KEY AUTOINCREMENT`
- `media_file_id INTEGER NOT NULL`
- `recording_id TEXT NOT NULL`
- `rank INTEGER NOT NULL`
  - 1 = best candidate, 2 = second best, etc.
- `match_score REAL NOT NULL`
- `match_strategy TEXT NULL`
- `evidence_json TEXT NULL`
- `created_utc TEXT NOT NULL`

Constraints and indexes:

- `FOREIGN KEY(media_file_id) REFERENCES media_file(media_file_id) ON DELETE CASCADE`
- `FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE`
- `UNIQUE(media_file_id, recording_id)`
- Index on `(media_file_id, rank)`

## Identity Resolution Policy

Agreed recommendations for future resolver/backfill work; these are not guarantees of the current scanner/admission implementation. Use the canonical [recording identity policy](recording-schema.md#recording-identity-policy), including separate identities for remasters.

### Evidence and Precedence

- A resolvable `trackstash_recording_id` tag is an explicit (hard) association, ahead of inferred matches. Validate it and follow known redirects. Unknown IDs, malformed IDs, and strong audio/version contradictions must produce a conflict for review rather than silently accepting or replacing the association.
- Beatport IDs and ISRCs are optional corroborating evidence, not prerequisites. Account for incorrect or reused identifiers and conflicting master/version information.
- Artist and title provide a useful starting point. Mix/version, artist credits, duration, and reliable audio evidence strengthen recording identity.
- Label, catalog number, and album/release metadata strengthen release context and disambiguation, but differences may be legitimate when a recording appears on multiple releases.
- Missing fields mean unknown, not disagreement. More independent, consistent evidence raises confidence; multiple fields copied from one provider are not independent confirmations.
- Normalize spelling and punctuation for comparison, but preserve meaningful distinctions such as Radio Edit, Extended Mix, Instrumental, remix credits, and remaster year. Missing mix metadata does not imply Original Mix.
- Format, bitrate, sample rate, and storage location describe a representation, not a recording identity.

### Audio Evidence

- Whole-file hashes establish identical bytes. A separately defined decoded-audio hash can establish identical decoded samples despite container/tag differences; record the decoding procedure and version.
- Acoustic fingerprints can identify candidates when metadata is missing and compare lossy encodings, but are not proof of identical files or identical masters.
- Evaluate alignment, duration, fingerprint coverage, and degeneracy as well as similarity. Edits and remasters may have very similar fingerprints.
- Associate multiple fingerprint observations with a canonical recording rather than treating one fingerprint as its immutable identity.
- Preserve each observation's source media-file ID, algorithm/version, generation date, duration/coverage, and trust/verification state.
- Promote fingerprints only from trusted file-to-recording associations; do not let an uncertain association seed future automatic matches.
- When audio cannot distinguish plausible original/remaster candidates, require version metadata or reviewed evidence.

### Decisions and Backfill

- Keep accepted links, review-required candidates, and unresolved files distinct. Do not force a link when evidence is insufficient.
- Acceptance should consider both evidence strength and the margin over competing candidates. Calibrate thresholds using reviewed fixtures; do not treat illustrative scores below as established probabilities.
- Persist supporting and contradictory evidence, provenance, resolver version, and manual decisions. Automated reruns must not silently overwrite a reviewed association.
- Avoid chain-merging: A matching B and B matching C does not establish that all three share a recording identity. Check the entire proposed group for contradictions.
- Backfill from existing metadata, reviewed provider resolutions, sidecars, and trusted fingerprints. Do not create one recording per file or blindly convert format-aware recording groups into recordings.
- Start with a read-only candidate report covering proposed recordings, file links, release memberships, confidence/evidence, and conflicts. Pilot approved mappings on a database copy before live migration.
- Generate and persist GUIDs when canonical identities are created; do not regenerate them on repeat imports or metadata changes.
- Keep tag/sidecar writeback separate from backfill approval and database migration. Retired IDs require redirects so existing tags remain resolvable after merges.

## Matching Strategy Definitions

The following scores are historical design examples, not calibrated acceptance thresholds. The evidence and contradiction rules above take precedence.

### ISRC Exact

- **Condition**: File metadata has ISRC, recording has same ISRC.
- **Confidence**: illustrative 0.99; requires corroboration and conflict checks because real-world ISRC metadata can be incorrect or reused.
- **Evidence**: `{"isrc_match": true, "isrc_value": "USRC17607839"}`

### ISRC via Beatport Lookup

- **Condition**: File has no ISRC, but Beatport track with same title/artist has ISRC; recording links to that Beatport track via `recording_external_ref`.
- **Confidence**: illustrative 0.95; provider evidence still needs version/master and contradiction checks.
- **Evidence**: `{"isrc_beatport_lookup": true, "beatport_track_id": 20918793}`

### AcoustID Exact

- **Condition**: File fingerprint and recording fingerprint both via AcoustID, exact match.
- **Confidence**: illustrative 0.90 for audio similarity, not proof of the same master. Apply duration, coverage, and version checks, especially for remasters.
- **Evidence**: `{"acousticid_match": true}`

### Duration + BPM Heuristic

- **Condition**: File duration within ±2 sec of recording, BPM matches (or within ±2 BPM).
- **Confidence**: 0.70–0.85 (medium-high; two metadata fields align).
- **Evidence**: `{"duration_diff_ms": 150, "bpm_match": true, "duration_within_threshold": true}`

### Embedding Similarity

- **Condition**: Recording embedding exists; text similarity between file metadata (title+artist) and recording text is above threshold (cosine distance < 0.3).
- **Confidence**: 0.60–0.75 (medium; semantic similarity but not proof).
- **Evidence**: `{"embedding_distance": 0.25, "model": "minilm-l6-v2"}`

### Title + Artist Fuzzy Match

- **Condition**: Fuzzy string match on title and artist (Levenshtein distance < threshold).
- **Confidence**: 0.50–0.65 (lower; human typos, abbreviations).
- **Evidence**: `{"title_match_ratio": 0.92, "artist_match_ratio": 0.88}`

### Manual Override

- **Condition**: User explicitly set the match via UI or command.
- **Confidence**: 0.99 (user authority).
- **Evidence**: `{"user_id": "david", "reason": "Confirmed listening"}`
- **is_user_override**: 1

## Suggested SQLite DDL Skeleton

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS media_file_embedding_document (
  media_file_embedding_document_id INTEGER PRIMARY KEY AUTOINCREMENT,
  media_file_id INTEGER NOT NULL,
  document_text TEXT NOT NULL,
  document_hash TEXT NOT NULL,
  created_utc TEXT NOT NULL,
  updated_utc TEXT NOT NULL,
  FOREIGN KEY(media_file_id) REFERENCES media_file(media_file_id) ON DELETE CASCADE,
  UNIQUE(media_file_id)
);

CREATE TABLE IF NOT EXISTS media_file_embedding (
  media_file_embedding_id INTEGER PRIMARY KEY AUTOINCREMENT,
  media_file_id INTEGER NOT NULL,
  model_name TEXT NOT NULL,
  model_version TEXT NOT NULL,
  embedding BLOB NOT NULL,
  dimensions INTEGER NOT NULL,
  created_utc TEXT NOT NULL,
  FOREIGN KEY(media_file_id) REFERENCES media_file(media_file_id) ON DELETE CASCADE,
  UNIQUE(media_file_id, model_name, model_version)
);

CREATE TABLE IF NOT EXISTS media_file_fingerprint (
    media_file_fingerprint_id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_file_id INTEGER NOT NULL,
    fingerprint_type TEXT NOT NULL,
    fingerprint_value TEXT NOT NULL,
    confidence REAL NULL,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(media_file_id) REFERENCES media_file(media_file_id) ON DELETE CASCADE,
    UNIQUE(media_file_id, fingerprint_type)
);

CREATE TABLE IF NOT EXISTS media_file_recording_match (
    media_file_recording_match_id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_file_id INTEGER NOT NULL,
    recording_id TEXT NOT NULL,
    match_score REAL NOT NULL,
    match_strategy TEXT NOT NULL,
    evidence_json TEXT NULL,
    notes TEXT NULL,
    is_user_override INTEGER NOT NULL DEFAULT 0,
    matched_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    FOREIGN KEY(media_file_id) REFERENCES media_file(media_file_id) ON DELETE CASCADE,
    FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE,
    UNIQUE(media_file_id, recording_id)
);

CREATE TABLE IF NOT EXISTS media_file_recording_candidate (
    media_file_recording_candidate_id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_file_id INTEGER NOT NULL,
    recording_id TEXT NOT NULL,
    rank INTEGER NOT NULL,
    match_score REAL NOT NULL,
    match_strategy TEXT NULL,
    evidence_json TEXT NULL,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(media_file_id) REFERENCES media_file(media_file_id) ON DELETE CASCADE,
    FOREIGN KEY(recording_id) REFERENCES recording(recording_id) ON DELETE CASCADE,
    UNIQUE(media_file_id, recording_id)
);

CREATE INDEX IF NOT EXISTS ix_media_file_fingerprint_media_file ON media_file_fingerprint(media_file_id);
CREATE INDEX IF NOT EXISTS ix_media_file_fingerprint_type ON media_file_fingerprint(fingerprint_type);
CREATE INDEX IF NOT EXISTS ix_media_file_embedding_document_hash ON media_file_embedding_document(document_hash);
CREATE INDEX IF NOT EXISTS ix_media_file_embedding_media_file ON media_file_embedding(media_file_id);
CREATE INDEX IF NOT EXISTS ix_media_file_embedding_model ON media_file_embedding(model_name, model_version);
CREATE INDEX IF NOT EXISTS ix_media_file_recording_match_media_file ON media_file_recording_match(media_file_id);
CREATE INDEX IF NOT EXISTS ix_media_file_recording_match_recording ON media_file_recording_match(recording_id);
CREATE INDEX IF NOT EXISTS ix_media_file_recording_match_score ON media_file_recording_match(match_score DESC);
CREATE INDEX IF NOT EXISTS ix_media_file_recording_match_strategy ON media_file_recording_match(match_strategy);
CREATE INDEX IF NOT EXISTS ix_media_file_recording_candidate_media_file_rank ON media_file_recording_candidate(media_file_id, rank);
CREATE INDEX IF NOT EXISTS ix_media_file_recording_candidate_recording ON media_file_recording_candidate(recording_id);
```

## Match Algorithm Workflow

Suggested priority order for matching attempts:

1. **ISRC Exact**: If file has ISRC, query `recording_external_ref` where source='beatport' and beatport track has same ISRC.
   - If match found: score 0.99, strategy `isrc_exact`, insert into `media_file_recording_match`.

2. **ISRC via Beatport Lookup**: If file has title+artist but no ISRC, query Beatport API for track matching title+artist, extract ISRC, lookup recording.
   - If match found: score 0.95, strategy `isrc_beatport_lookup`.

3. **AcoustID**: If file has fingerprint, query AcoustID API or local DB.
   - If match found: score 0.90, strategy `acousticid_exact`.

4. **Duration + BPM**: Query recordings within duration ± 2sec and BPM ± 2.
   - If one match: score 0.80.
   - If multiple matches: rank as candidates, score 0.70.

5. **Embedding Similarity**: Generate embedding from file metadata, find recordings with embedding distance < 0.30.
   - Rank by distance, top candidate score 0.70–0.75.

6. **Fuzzy Match**: Fuzzy string match on title+artist.
   - If one match: score 0.65.
   - If multiple: rank as candidates, score 0.50–0.60.

7. **Manual Review**: Store candidates and await user input.

## Migration Plan (Phased)

1. Add `media_file_embedding_document`, `media_file_embedding`, `media_file_fingerprint`, `media_file_recording_match`, and `media_file_recording_candidate` tables.
2. Populate `media_file_fingerprint` from existing `media_file.fingerprint_raw` and `acoustid_submission_hash` fields.
3. Build normalized embedding document text from existing `metadata` rows and insert into `media_file_embedding_document`.
4. Generate MiniLM embeddings and insert rows into `media_file_embedding` with `model_name` and `model_version`.
5. Run ISRC exact matching on existing media files with ISRC in metadata.
6. Run AcoustID matching where fingerprints available.
7. Run duration+BPM heuristic matching for remaining unmatched files.
8. Use embedding similarity for unresolved files and rank candidates.
9. Populate candidate ranking table for user review.
10. Implement manual override UI for user corrections.
11. Link tagged/matched files to canonical recordings for metadata refresh.

## Open Decisions

- Should `media_file_recording_candidate` store all N candidates or only top 5/10?
- Confidence threshold for "auto-accept" matches (default: 0.85)?
- Whether to store user identity for overrides (for audit trail).
- Should embeddings be regenerated on every metadata update or only when `document_hash` changes?
- Support for "reject" matches (user says "this is NOT correct")?
- Fallback behavior if no match found above threshold (e.g., user prompt vs. silent skip).
