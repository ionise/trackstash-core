# Future Implementation Prompt: Offline Audio Embeddings

Use this prompt in a future implementation session. Do not treat it as authorization to begin implementation now.

## Role and Task

Act as the implementation agent for the current Trackstash architecture. Design and implement a first production-quality, offline audio-embedding pipeline that enriches local track identities with vectors usable for:

1. Finding recordings that sound similar to a selected recording.
2. Finding tracks that complement a selected playlist or collection, with useful novelty and flow rather than simply returning the nearest neighbors to an average vector.

Before editing, inspect the current repository state, instructions, migrations, storage interfaces, scanner/media-file contracts, embedding queue/worker, similarity and playlist modules, and tests. Treat all current files and user changes as authoritative. Do not assume the legacy Trackstash-lite code is production-ready or that the design below can be copied verbatim.

## Current Context to Revalidate

The earlier `Trackstash-lite/` effort established useful principles and a working prototype direction:

- The old README describes local audio analysis, SQLite persistence, model registration, OpenL3 ONNX embeddings, CLAP audio embeddings/tags, and cosine similarity.
- Its old SQLite design stored vectors as float blobs associated with a track and model. Its similarity service ranked vectors with cosine similarity.
- `Trackstash-lite/Reports/rekordbox-top-200-play-pattern-analysis.md` proposed calculating a CLAP centroid from a frequently played set and ranking underplayed tracks against it. Reuse this as an experiment, not as the final definition of playlist complementarity.
- `Trackstash-lite/docs/modularization-plan.md` assigns inference to an embeddings module, ranking to similarity, recommendations/set flow to suggestions, and playlist workflows to playlists.
- The legacy `OpenL3EmbeddingModel` uses hand-built, fixed-shape preprocessing and truncates or zero-pads output to a configured dimension. Do not reuse that behavior. Verify the chosen model's actual frontend, temporal pooling, supported sample rates, expected tensor shape, output dimensions, and normalization against its authoritative model documentation and test vectors.
- The legacy CLAP client starts a Python process for each track. Avoid per-file model startup for a batch workload unless profiling demonstrates that it is acceptable; prefer a persistent local worker or a well-supported in-process runtime.

The current codebase is different and must remain the target:

- `trackstash-core` separates canonical catalog entities (`recording`) from owned media assets (`media_file`) and links them with reviewed file-to-recording matches.
- The SQLite migration already has a generic `embedding_document` table with model name/version, dimensions, `document_hash`, optional text/source payload, and BLOB vector data.
- Core has generic embedding work items and orchestration, currently shaped around canonical entity changes and a text-embedding-first default. `IEmbeddingRepository` currently supports get-by-entity, upsert, and delete-by-document-hash, but not vector candidate enumeration/search.
- Core TODO explicitly defers audio embeddings. Confirm all of these statements against the live code before relying on them.
- `trackstash-scan/embed-worker/` and current `trackstash-scan` embedding path generate text embeddings with MiniLM. They are not audio embedding implementations and must not be relabeled or reused as audio inference.
- The initial model choice is now CLAP (`laion-clap`, `HTSAT-base`, AudioSet checkpoint). On 2026-10-01, the local 2.35 GB checkpoint `music_audioset_epoch_15_esc_90.14.pt` successfully embedded an AIFF on Apple MPS, producing a finite 512-dimensional vector. The checkpoint SHA-256 is `fae3e9c087f2909c28a09dc31c8dfcdacbc42ba44c70e972b58c1bd1caf6dedd`; use the checkpoint hash as model version, not this path or a hand-maintained version string.
- A standalone service exists at `audio-embed-worker/`. It has a persistent CLAP engine, native FastAPI endpoints for allowlisted shared paths and streamed audio, per-worker disk result cache, optional bearer auth for remote clients, and a 1 GiB default upload limit. The returned vector is L2-normalized.
- `trackstash-scan` now has a separate `media_file_audio_embedding` table and exported `Update-TrackstashAudioEmbedding` cmdlet. It associates the worker vector with the exact scanned `media_file_id`, checks the current file hash against the scan row, skips unchanged embeddings, prefers shared-path processing, and falls back to upload. This is a post-scan enrichment step; admission does not invoke it automatically yet.
- The initial persistence key is the scanned file-byte SHA-256. Retagging changes file bytes and can therefore trigger a fresh embedding; a decoded-PCM audio-content hash remains a follow-up optimization, not a claim of the current implementation.

## Design Principles

### Model and Runtime Decision

Continue with CLAP for the first end-to-end implementation; do not block the vertical slice on a multi-model comparison. Verify current model availability, exact artifact/version, license and redistribution constraints, expected input preprocessing, vector dimensions, inference runtime support on this Mac and remote CPU/GPU workers, memory, throughput, and offline provisioning. Assess OpenL3 or Discogs EffNet only as a separate later experiment if evaluation shows CLAP is insufficient.

Select one primary model for the first end-to-end vertical slice. Add a second model only if a small representative pilot shows a material benefit for the user's electronic-music domain. Never compare or blend vectors across model names, versions, dimensions, or incompatible preprocessing contracts. Document the model decision and reproducible setup. Normal operation must not download weights or call a hosted inference API; model files and runtime dependencies must be provisioned explicitly before an offline run.

### Audio Source and Identity

- A vector describes audio content from a specific local `media_file`; it is not proof of recording identity and must never automatically create or accept a `media_file_recording_match`.
- Preserve provenance from each vector to its source media file, content/audio hash, model name/version, dimensions, preprocessing version, generation time, and any segment-pooling method.
- Track file-byte hashes can change when tags change. Define and document an audio-content invalidation key that is stable across metadata-only tag changes where practical (for example, hash of deterministically decoded PCM), while retaining the existing file `content_hash` for file identity. Do not silently redefine existing hash semantics.
- Keep per-media-file vectors as the auditable source of truth. For recording-level discovery, use reviewed recording links and a clearly defined representative/aggregation policy. Do not average unrelated masters, radio edits, remixes, or alternate versions into one recording vector.
- Decide whether the current generic `embedding_document` can represent this provenance and modality without semantic ambiguity. If not, make the smallest additive migration for an audio-specific record rather than overloading text-document fields. Preserve model/version isolation and integrity constraints.

### Deterministic, Full-Track Inference

- Support a declared set of local formats through established decoders (prefer the repository's existing FFmpeg/decoder patterns); unsupported/corrupt input must produce a recorded per-file error, not a fabricated vector.
- Specify deterministic channel handling, sample rate, resampling, amplitude treatment, segmentation/windowing, overlap, and aggregation for long tracks. Analyze the full track or a principled set of windows across its duration; do not silently use only the opening seconds.
- Validate finite values, expected dimension, and any model-required vector normalization. Reject invalid output; never pad/truncate to make the dimension appear valid.
- Make inference batchable and bounded in concurrency. It must be resumable and idempotent, skip work only when both the source audio key and exact model/preprocessing versions match, and support explicit force regeneration.
- Keep embedding generation out of latency-sensitive canonical write paths. Use a local offline batch command/worker that integrates with the existing queue contracts only if those contracts can faithfully represent media-file audio work and its provenance.

### Remote Worker Transport and Bandwidth

- Support workers running natively on Apple Silicon as well as CPU/GPU hosts elsewhere; REST is a transport option, not a requirement to containerize inference.
- Minimize bytes sent to remote workers. Prefer a read-only shared media location when it is reliable and suitably close to the worker. Otherwise stream the original stored audio file once and cache it on the worker by audio-content hash; subsequent jobs for that hash should send only a job descriptor and reuse the cached source.
- Do not upload decoded WAV/PCM or a full spectrogram by default. Those representations may be larger than the original compressed file. Decode and build model input on the worker. Compare transfer sizes and model-quality impact before adding optional temporary lossy transcoding or selected-window transfer.
- Job descriptors should include media identity, audio-content hash, model/version, preprocessing version, and an approved source reference. Workers must verify the content hash before inference and return only the vector plus provenance/status, not audio.
- Stream large uploads with bounded resource use and resumable/retry-safe semantics. Never accept arbitrary filesystem paths from untrusted clients; constrain source references to configured roots or accept authenticated uploads.

### Storage and Retrieval

- Store vectors in a representation consistent with existing core conventions (likely little-endian float32 BLOBs); implement and test serialization round-trips, dimension validation, and finite-value checks.
- Extend storage contracts with model-scoped candidate/vector enumeration or a provider-agnostic similarity-query operation. Do not put SQLite-specific SQL in higher-level similarity or playlist modules.
- First implement a correct bounded brute-force cosine search over the local corpus. Add an approximate-nearest-neighbor index only if measured corpus size and latency require it.
- Candidate queries must filter to compatible model/version/preprocessing, exclude the query itself, handle duplicate media instances and alternate editions intentionally, and return recording/media identity plus score and evidence.
- Treat cosine similarity as a ranking signal, not calibrated probability or match confidence.

## Product Behavior

Implement and evaluate two separate query modes:

1. **Similar sounding:** rank by compatible audio-vector similarity, optionally filter/boost using existing BPM, key/Camelot, genre, and reviewed identity data. Clearly expose the audio similarity score and supporting metadata; do not present the score as certainty.
2. **Playlist complement:** create a playlist profile from its member tracks, generate candidates that fit the requested context while avoiding redundant copies and excessive similarity to tracks already present, and balance cohesion with novelty/diversity. A centroid may be one baseline, but compare it with alternatives such as per-track maximum similarity, medoid/cluster profiles, or an explicit diversity-aware reranker. Keep compatibility/flow features (BPM, key, energy, genre) as separately interpretable signals rather than hiding them inside an unvalidated vector blend. Provide reasons for each recommendation.

Respect existing user taste and listening/playcount data only where that data is already available and the user has requested its use. Do not modify Rekordbox or playlists during the first implementation unless explicitly requested.

## Suggested Delivery Stages

1. **Repository and model decision:** CLAP is selected and has passed a one-track MPS smoke test. Verify current architecture, data volumes, available media formats, actual database paths, licensing/artifacts, and offline runtime. Extend the pilot to a representative read-only sample of the user's electronic music; report coverage, failures, speed, dimensions, and qualitative nearest neighbors.
2. **Contracts and schema:** the scan database's additive `media_file_audio_embedding` table now stores model/checkpoint version, source file hash, dimensions, JSON vector, device, and generation time with cascade ownership by `media_file_id`. Continue by deciding whether core-level provider-neutral access is needed for future similarity features.
3. **Offline generation:** a post-scan cmdlet now calls the local/remote worker, validates its response, stores vectors, skips unchanged rows, and guards against stale scan hashes. Continue with large-batch throughput, resumability/audit output, and measuring network bytes per unique audio hash. Keep source audio read-only.
4. **Similar-track retrieval:** add model-scoped cosine retrieval, identity-aware result grouping, unit/integration tests, and an evaluation export for manual review.
5. **Playlist complement:** implement a separate baseline and diversity-aware reranker with explainable components; evaluate on existing Rekordbox playlist/history data only as a read-only analysis.
6. **Operational audit:** independently verify vector counts, dimensions, source hashes, processing/model provenance, rerun idempotence, failure recovery, zero invalid vectors, and no media/tag/catalog mutations.

## Evaluation Requirements

Build a small manually reviewed evaluation set from the user's actual collection before making broad recommendations. Include seed tracks with known similar tracks, contrasting tracks, alternate editions/remixes, duplicate formats, and playlists where a useful complement is not simply the closest sounding item. Compare at least:

- Audio-only cosine ranking.
- Audio ranking plus existing metadata/features.
- Playlist-centroid baseline versus diversity-aware ranking.

Report top-k precision or pairwise human relevance judgments, diversity/redundancy, coverage, runtime, and failure cases. Thresholds must be empirically measured; do not invent confidence probabilities. The system must remain useful when BPM/key/genre fields are absent.

## Tests and Acceptance Criteria

- Model identity/version, dimensions, preprocessing version, and source audio key are persisted and enforced.
- Re-running an unchanged batch makes no new model calls or vector writes; changed audio key, model version, or preprocessing version regenerates predictably.
- Metadata-only tag changes do not cause audio re-embedding when decoded audio is unchanged.
- Model output shape is checked against the real model contract; invalid, NaN, infinite, empty, or wrong-dimension outputs fail visibly.
- Long-track windowing and pooling are deterministic and cover the full duration.
- Vectors round-trip through storage without dimension/value corruption; cosine tests cover identical, orthogonal, zero/invalid, and dimension-mismatch inputs.
- Similarity retrieval never mixes models/versions, excludes the seed, and handles duplicate media files/editions according to an explicit policy.
- Playlist ranking has separate cohesion and diversity behavior, no duplicate suggestions, and explainable ranking components.
- Offline inference works with network access disabled after dependencies/weights have been provisioned.
- Remote-worker jobs transfer the original audio at most once per worker/content hash (unless cache eviction is recorded); result payloads contain no audio, and the pipeline does not transmit a larger WAV/spectrogram by default.
- No audio bytes are written into the Catalog; no automatic identity match, tag write, file move/copy, cloud upload, LLM description, or Rekordbox mutation is introduced.
- Existing catalog, scanner, and matching tests remain green; add focused tests at the ownership boundaries that changed.

## Scope Boundaries

Do not build a frontend, cloud/GPU service, hosted API, audio-generation feature, genre classifier, automatic match acceptance, or playlist writer in the initial slice. Do not refactor unrelated schemas or modules. Do not implement until this prompt is explicitly invoked in a future task.

## Expected Deliverables in the Future Session

- A short model/runtime decision record and a measured pilot report.
- Additive data/storage contracts and migration, if needed.
- Offline embedding batch/worker and similarity retrieval with focused tests.
- A separate playlist-complement ranking baseline and evaluation report, or a clearly justified staged deferral.
- A concise audit of what was generated and verification results.
