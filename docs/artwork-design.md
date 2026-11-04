# Shared Artwork Design Intent

Status: agreed direction, not implemented. Recorded 2026-11-04.

## Purpose and Current State

Artwork is common catalog infrastructure for labels, artists, releases, and recordings, not a separate upload implementation for each entity. Support individual logos/covers as well as ordered galleries. Labels will be the first consumer; other entity screens need not be implemented at the same time.

Migration 5 currently provides only a label-specific `label_artwork` table containing local content and source/rights metadata. There are no implemented shared image contracts, upload/serving workflows, galleries, or recording artwork resolver. Existing schema sketches and catalog TODOs are not evidence of those capabilities.

This document records intent so implementation can proceed in small, independently verifiable increments. Exact contracts, schema names, HTTP routes, limits, and image-processing dependencies remain to be designed.

## Agreed Model

### Shared assets

Separate stored image assets from their use by a catalog entity:

- Stable asset ID and content hash.
- Local image content, verified media type, dimensions, original filename, and upload time.
- Source URL, attribution, and recorded usage rights; storing a URL or image does not establish permission to reuse it.
- Shared upload validation, authenticated content serving, and thumbnail generation.

One asset may be associated with several entities without copying its content. A content hash can identify identical bytes, but does not establish equivalent provenance or usage rights. Deduplication must not silently collapse distinct rights/source records; the metadata policy must be settled before automatic reuse.

Original uploaded content and derived thumbnails are distinct: originals are authoritative, while thumbnails can be regenerated. Physical storage remains an adapter concern; choose database BLOBs versus managed local files before implementing persistence. Clients must not supply arbitrary filesystem paths.

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

Artwork-aware label merges must review association transfer, duplicate asset links, roles, gallery order, preferred-image conflicts, provenance, and retirement/version handling. Preserve associations or block explicitly; never silently discard artwork. Until a tested transfer policy exists, retain the current conservative merge blocker. Likewise, do not bypass metadata deletion blockers before safe snapshot/restore semantics exist.

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
