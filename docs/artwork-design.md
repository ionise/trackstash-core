# Shared Artwork Design Intent

Status: shared assets and label galleries implemented in migration 6; managed Release covers/galleries and provider-neutral explicit attachment implemented in migration 11; Artist portrait/gallery and manual profile implemented in migration 12. Live provider adapters and Recording resolution remain planned.

## Purpose and Current State

Artwork is common catalog infrastructure for labels, artists, releases, and recordings, not a separate upload implementation for each entity. Support individual logos/covers as well as ordered galleries. Labels will be the first consumer; other entity screens need not be implemented at the same time.

Migration 5 originally provided only a label-specific `label_artwork` table. Migration 6 supersedes it with shared `artwork_asset` metadata, foreign-key-backed `label_artwork_link` associations, and permanent `label_artwork_revision` history. Shared asset/content contracts, label uploads, protected originals/thumbnails, galleries, and survivor-first merge transfer are implemented. Migrations 11 and 12 reuse that infrastructure for Release covers and Artist portraits. There is still no Recording artwork resolver.

This document records the broader intent and staged delivery. The concrete choices below supersede open design points for Labels, Releases and Artists; Recording fallback remains design intent, not an implemented capability.

## Implemented Artist Slice (schema 12)

- `artist_artwork_link` and permanent `artist_artwork_revision` reuse the Release
  schema template without changing schema-11 behavior. Ordered `portrait`/`image`
  galleries allow at most 100 entries and one explicit preferred image.
- Upload/replace/reorder/caption/preference/detach, rights/provenance, validation,
  savepoint rollback and failed-upload file cleanup share the existing managed
  implementation. Old assets/history are retained, including detached portraits.
  Protected original/thumbnail serving is unchanged.
- Artist headers use only a preferred `portrait`. Click/touch/keyboard on the
  portrait or placeholder, or the separate management action, opens the manager.
  No Label-logo/Release-cover fallback or remote-image fetching is enabled.
- The Artist-specific manual profile and gallery share one Artist version.
  Permanent profile revisions preserve biography/source, links, Favourite,
  notes/review; changes stale import reviews but do not enqueue image/profile-only
  text work. Private notes are excluded from canonical text. Legacy replacement
  and deletion are blocked with profile/artwork data or permanent history.
- Catalog backups accept schemas 6-13 and retain all managed assets/history.
  Browsing remains available on schema 9-11; profile/artwork requires 12.
  Migration is additive, rehearsed only on temporary catalogs. A live upgrade
  needs separate verified backup, rehearsal and approval.

## Implemented Release Slice (schema 11)

- `release_artwork_link` associates shared assets with ordered `cover`/`image` entries, at most 100 entries and one explicit preference. `release_artwork_revision` retains immutable before/after snapshots and, for provider attachments, provider/candidate/revision identity. Changes invalidate the Release aggregate ETag and outstanding canonical import reviews.
- Release details use only an explicitly preferred `cover` as the header image, never the Label logo or an arbitrary gallery image. A responsive identity header, readable durations, separate credit scopes and collapsed technical IDs keep browsing focused on music.
- **Manage release artwork** uses the same validated upload, replace, gallery-order/caption/preference/detach and authenticated-image implementation as Labels. Static JPEG/PNG, usage rights and existing processing limits still apply. Replacing or detaching retains original assets and history.
- External candidate lookup is deliberate. `IReleaseArtworkProvider` is a server-configured, provider-neutral adapter contract; **no live Beatport, MusicBrainz/Cover Art Archive or Discogs adapter is registered**. Without adapters the manager explicitly reports that none are configured.
- Candidates contain metadata, an HTTPS source URL and opaque release-scoped candidate/revision IDs. The browser does not load remote image URLs or submit arbitrary URLs for attachment. Attachment refreshes the candidate identity, checks the original Release token, requires explicit release-match/rights confirmation, and stores a validated managed copy with immutable provenance. Future adapters must restrict download/redirect destinations and credentials, enforce response limits/timeouts and respect provider terms; candidates are not proof of usage rights.
- Release deletion and legacy YAML replacement remain blocked when associations or permanent artwork history exist. Portable artwork packaging/export and Recording inheritance remain separate work.
- Migration 11 adds tables/triggers without replacing existing entities/assets. Browsing remains available on schema 9/10; artwork routes explicitly require 11. Tests use temporary databases only. Live upgrade requires separate backup, rehearsal and approval; the API never migrates automatically.
- Catalog backups accept schemas 6-13 and include all managed assets, including release covers and replaced/detached history. Restore preserves the archived schema.

## Implemented Label Slice

- [Shared contracts](../src/TrackStash.Core/Storage/Artwork.cs): immutable asset metadata and ordered label associations. Label roles are `logo` and `image`; at most 100 entries and one preferred image per gallery. No implicit preferred selection or content-hash deduplication is performed.
- [Managed content store](../src/TrackStash.Core/Storage/ManagedArtworkStore.cs): originals and JPEG thumbnails are files outside the catalog database and Git working trees. The default root is `<LocalApplicationData>/TrackStash/artwork-data` (using .NET's platform-specific user data directory). Override it with `TRACKSTASH_ARTWORK_ROOT`; the API additionally accepts `CatalogApi:ArtworkRoot`, which takes precedence. Paths must be absolute, and roots within Git repositories are rejected. Only server-generated asset IDs form storage paths; original filenames are metadata.
- Set the same root for migration and the API. Bootstrap uses `TRACKSTASH_ARTWORK_ROOT` (or the default); the provider descriptor also supports an explicit root. Changing configuration does not relocate existing files. Missing files produce explicit `409 artwork_unavailable`, not a successful empty image.
- Upload policy: static JPEG/PNG only, maximum 10 MiB, 25,000,000 pixels, and 16,384 pixels per side. SkiaSharp 3.119.4 verifies complete decoding; APNG is explicitly rejected. Thumbnails fit within 512 pixels, apply EXIF orientation, flatten transparency onto white, and omit source metadata. Originals retain their exact bytes and embedded metadata. No fetching, automatic rights validation, or image editing occurs.
- Usage-rights text is mandatory (up to 2,000 characters); source URL (HTTP(S), 2,048), attribution (1,000), and filename (255) are optional metadata. Captions (1,000), roles, ordering, and preference belong to associations. Provenance is immutable: replace an upload to correct it, retaining the old asset and history.
- Upload/replace validates and writes a new asset directory before committing the gallery transaction. Ordinary failures remove only that upload's uncommitted files. A process/power failure can leave unreferenced directories; no automatic garbage collection or permanent asset deletion is exposed. SQL asset metadata is immutable and retained for history. Database and managed files together form the catalog's artwork state.
- Gallery GET and mutations use the label aggregate ETag. Upload appends, or atomically replaces an existing entry in its original position when `replaceEntryId` is supplied. JSON full-gallery replacement edits/reorders/detaches existing entries without changing asset identity. Both paths record permanent before/after association history; revisions retain original label IDs on merge. Conditional scalar/profile changes and artwork changes invalidate one another's stale tokens.
- The label Show page offers **Manage artwork**. Uploads save immediately; draft gallery edits save separately. Images load through authenticated fetch into temporary browser blob URLs, revoked on unmount. No keys appear in URLs, public static assets, or browser storage. Content is served with no-store/nosniff headers. The original-content route remains protected, including for detached assets retained in history.
- Merges preserve the survivor's gallery order, append every retired entry in order, and keep the survivor's preferred image. If the survivor has no preference, the retired preference carries forward. Even identical assets remain separate associations when they have separate entry IDs; no provenance is silently collapsed. A combined gallery over 100 entries blocks apply. Previews display the result; full association/asset metadata and revision snapshots join the dependency digest/audit. Artwork edits invalidate prior reviews. Assets/bytes are not copied or deleted on merge.
- Deletion remains blocked for labels with artwork associations or history until complete safe-delete/restore semantics exist. No automatic orphan/content pruning is implemented.

### Upgrade and Backup Boundary

Migration 6 exports all legacy BLOBs into managed files, preserves original association IDs and provenance/timestamps, and removes `label_artwork` only after successful export. The new asset records a verified SHA-256 hash and retains the original hash in `legacy_content_hash`, with `legacy_artwork_id` linking the identity mapping. Legacy dimensions remain unknown; a thumbnail is derived on read after validation. Unsupported legacy bytes are preserved but explicitly rejected for serving, not silently discarded. Failed upgrades roll back database changes and clean up their exported files.

**A database-only backup is no longer a complete artwork backup.** The shared native backup service now snapshots SQLite and packages all snapshot-registered assets (including retained history assets and existing thumbnails) in a timestamped ZIP with a versioned checksum manifest. It verifies before publication, retains the newest 10 verified backups per catalog, and supports verification and restore into a new directory. CLI and authenticated GUI/API use that same service; downloads export the saved archive. See [catalog backup operations](../../trackstash-catalog/docs/catalog-backups.md) for configuration, concurrency, retention and restore boundaries. No new schema migration is required.

Do not copy a live SQLite database as an ordinary file or delete retained assets to save space. If taking a manual backup instead, stop writers, take a consistent database backup and preserve the configured artwork root together. Restore the matching database and files before restarting with the corresponding root configuration. Permanent artwork GC must coordinate with backup snapshots before it can be introduced.

## Agreed Model

### Shared assets

Separate stored image assets from their use by a catalog entity:

- Stable asset ID and content hash.
- Local image content, verified media type, dimensions, original filename, and upload time.
- Source URL, attribution, and recorded usage rights; storing a URL or image does not establish permission to reuse it.
- Shared upload validation, authenticated content serving, and thumbnail generation.

One asset may be associated with several entities without copying its content. A content hash can identify identical bytes, but does not establish equivalent provenance or usage rights. Deduplication must not silently collapse distinct rights/source records; the metadata policy must be settled before automatic reuse.

Original uploaded content and derived thumbnails are distinct: originals are authoritative, while thumbnails can be regenerated. Managed local files were selected for the label slice; physical storage remains behind the shared content-store interface. Clients must not supply arbitrary filesystem paths.

### Entity associations

Use entity-specific, foreign-key-backed associations to shared assets, with common contracts and behavior. Avoid an unconstrained polymorphic `entity_type/entity_id` ownership table.

Associations carry:

- Role, constrained to valid roles for the entity: for example label logo/image, artist portrait/gallery image, release front/back cover or gallery image, recording-specific image.
- Explicit gallery ordering.
- Preferred image selection for summary/detail views.
- Optional caption and, where needed, recording release context.

Exact role values, ordering representation, preferred-image uniqueness, and placement of contextual metadata are design work for the first increment. An entity's role/caption/order/preference must not be stored as global properties of an asset shared by other entities.

### Recording resolution

A recording is an abstract track/master and may occur on several releases. There is not necessarily one parent release.

Resolve display artwork in this order:

1. Applicable explicit recording artwork.
2. Preferred cover from the release being viewed or played, when that release is associated with the recording.
3. If no release context is supplied, artwork from an explicitly selected preferred release.
4. Otherwise return no artwork with an explicit missing/ambiguous resolution result.

Do not silently choose the first release returned by storage. Supplied invalid/unrelated release context should be reported explicitly, not silently substituted. Selection of a preferred release is a separate explicit decision; its persistence and management are not implemented.

Inherited artwork is resolved on read, not copied into recording associations. Responses should distinguish explicit artwork from inherited artwork and identify the originating release/association. Changing release artwork must affect subsequent resolved views without rewriting recording metadata. Cache/version semantics must account for these dependencies.

Resolution selects display artwork; it does not automatically combine every related release gallery into a recording gallery. Downloads, exports, or future tag-writeback callers must supply or deliberately resolve release context rather than make their own first-release fallback.

## Ownership and Operational Rules

- **Core:** asset/association/resolution contracts, shared invariants, adapter persistence, migrations, and dependency/version behavior.
- **Catalog application:** reviewed upload/attach/replace/remove/reorder/preference workflows and transaction orchestration.
- **HTTP API:** authenticated upload and content routes, bounded request handling, explicit errors, concurrency, and public DTOs.
- **Admin UI:** reusable upload/gallery components and entity-specific presentation; label integration first.
- **Bootstrap:** explicit migration execution. No startup migration or automatic live conversion by the UI.
- **Library:** media tags and filesystem writeback remain separate; catalog artwork changes do not modify owned media.

Local uploads are the initial input. Remote image fetching, source scraping, enrichment providers, image recognition, and embeddings are not included. Do not introduce a separate service or runtime plugin framework merely to share artwork handling.

Before upload is exposed:

- Select supported formats, encoded-byte/pixel/dimension limits, thumbnail sizes, and a maintained decoder.
- Verify actual image content rather than trusting extension or client Content-Type; reject malformed/unsupported/oversized content explicitly.
- Define handling of SVG/active content, animation, orientation, and embedded metadata. Do not serve arbitrary uploaded active content as images.
- Keep original filenames as metadata, never trusted storage paths.
- Authenticate image/thumbnail access as well as mutation. Choose browser-compatible protected image loading without credentials in URLs or public asset exemptions.
- Use bounded upload handling separate from the existing 16 KiB label JSON policy; do not casually enlarge all request limits.
- Preserve existing bearer/origin policy and expected-version semantics; stale edits must not overwrite associations or preference changes.
- Make partial upload/thumbnail/storage failures explicit and recoverable without success-shaped responses or broken associations.

## Removal, Merges, and History

Removing artwork from an entity means detaching its association, not deleting a shared asset used elsewhere. Replacement should attach a validated asset and update associations atomically, retaining any required audit/provenance.

Asset deletion needs reference checks, history/retention rules, and deliberate orphan cleanup. Do not cascade deletion of one entity into shared images. Whether an audit retains content or only metadata/references must be decided before irreversible deletion is offered.

Artwork-aware label merges must review association transfer, duplicate asset links, roles, gallery order, preferred-image conflicts, provenance, and retirement/version handling. The label slice implements the survivor-first stacking policy described above; unrelated unsupported dependencies still block. Future entity mergers must define their own policy, not silently discard artwork. Do not bypass metadata deletion blockers before safe snapshot/restore semantics exist.

## Migration Direction

Supersede migration 5's label-specific storage using a **new migration**, never by editing an already-applied migration.

The migration must handle populated `label_artwork`, not just today's empty live table: preserve image bytes/hashes, IDs or traceable mappings, roles, source/attribution/rights, and timestamps. Define treatment of invalid legacy image content without silent loss. Preserve existing labels, provider references, merge audits, retired mappings, and version guarantees; install equivalent retirement/dependency guards for the new associations.

Decide whether the old table is removed or retained temporarily, with one authoritative write path and no drifting duplicate stores. Test upgrade from populated version-5 fixtures, rollback on failure, integrity, foreign keys, and all pre-existing data. Live backup/migration remains a separately approved operation.

## Incremental Delivery and Acceptance Gates

### A. Contracts and persistence design

Specify asset/provenance boundaries, storage strategy, role/preference/order invariants, release context and preferred-release representation, missing/ambiguous results, authorization, processing limits, audit/retention, and concurrency. Check existing entity/import/delete contracts before adding new abstractions.

Gate: documented contracts and examples cover shared assets, galleries, contextual fallback, conflicting rights, and safe removal. No upload endpoint or migration is implied by this design-only step.

### B. Shared storage and migration

Implement core asset and association contracts and adapter persistence; labels first, with an explicit extension path for artists/releases/recordings. Add the new forward migration and safe dependency/retirement/version rules.

Gate: tests prove populated-v5 preservation, shared-reference integrity, transaction rollback, deterministic ordering/preference, and no retired-ID resurrection. Existing label profiles/merges still work or conservatively block unsupported artwork.

### C. Label artwork vertical slice

Implement shared validated upload/thumbnail/authenticated serving services, catalog orchestration, HTTP contracts, and reusable label upload/gallery UI. Support source/rights entry, preview, attach/replace/detach, ordering, and preferred selection. Integrate merge transfer only once its conflict/audit policy is tested; otherwise show a clear blocker.

Gate: API/browser tests cover valid and invalid uploads, authorization, stale writes, processing failures, shared-asset detach, gallery persistence, and merge/deletion safeguards. A label logo is visible after reload without making catalog images public. A focused layout improvement may accompany this slice, not a full admin redesign.

### D. Additional entity consumers and recording fallback

Reuse the infrastructure for artists and releases as their catalog workflows become available. Add recording override/context associations and the explicit resolver, then integrate detail/playback callers as applicable.

Gate: one recording on an original release, compilation, and reissue resolves the correct contextual cover; explicit overrides win; no-context ambiguity never picks an arbitrary cover. Release artwork changes invalidate resolved caches correctly. Entity galleries remain distinct.

### E. CLI and portable artifact workflows

Extend YAML/import/export and dry-run/apply/get through shared contracts only after those contracts exist. Decide how binary content is packaged/referenced portably; database-local asset IDs alone are not a portable backup. Report inherited artwork separately from explicit associations.

Gate: artifact round trips retain content and provenance without path traversal, unrequested remote fetching, or copying inherited covers into recording metadata. CLI/catalog handlers remain provider-independent.

Each increment can be reviewed and committed separately. Automated enrichment, full multi-entity admin expansion, generalized image editing, bulk ingestion, and media writeback remain outside the initial label artwork slice.
