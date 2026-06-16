# Storage Interface Design (Planned)

This document defines the storage boundary for TrackStash. The goal is to keep the domain layer provider-agnostic while allowing SQLite today and other providers, including Azure-hosted options, later.

Status: design only.

Related documents:

- [ecosystem-modules.md](ecosystem-modules.md) for module boundaries.
- [schema-conventions.md](schema-conventions.md) for shared ID and normalization rules.
- [label-schema.md](label-schema.md), [release-schema.md](release-schema.md), [artist-schema.md](artist-schema.md), [recording-schema.md](recording-schema.md), and [media-matching-schema.md](media-matching-schema.md) for entity tables and relationships.

## Design Goals

- Keep the storage contract independent from SQLite-specific APIs.
- Support a local SQLite provider first.
- Allow a later hosted relational provider, such as Azure SQL, without changing callers.
- Allow a document-oriented provider only if it can satisfy the same repository contract.
- Keep transaction boundaries explicit.
- Keep read and write concerns separate where that improves clarity.

## Storage Boundary

The storage layer should sit between:

- `trackstash-core` domain contracts
- downstream feature modules such as `trackstash-catalog`, `trackstash-match`, and future writeback modules
- provider implementations such as SQLite or an Azure-hosted database

The storage layer should own:

- persistence logic
- retrieval logic
- transactions
- provider-specific mapping
- migration execution

The storage layer should NOT own:

- filesystem scanning
- tag parsing
- audio hashing or fingerprinting
- Beatport API calls
- tagging writeback logic
- business rules that belong in the domain layer

Any future remote sync adapter should also stay outside this core boundary. It should translate between a remote transport and the same storage-facing repository contract, rather than becoming part of the domain storage layer itself.

## Recommended Shape

Use repository interfaces for domain objects and a small unit-of-work boundary for grouped writes.

Suggested top-level pieces:

- `IStorageProvider` or `ITrackstashStoreFactory`
- `IUnitOfWork` or `ITrackstashTransaction`
- domain repositories:
  - `ILabelRepository`
  - `IArtistRepository`
  - `IReleaseRepository`
  - `IRecordingRepository`
  - `IMediaFileRepository`
  - `IMatchRepository`
  - `IEmbeddingRepository`
- migration runner contract

## Core Concepts

### Canonical Entity Identity

Canonical entities use GUID text IDs.

Examples:

- `label_id`
- `artist_id`
- `release_id`
- `recording_id`
- `media_file_id` for scan-local file rows

The storage layer should treat those IDs as opaque strings.

### External References

External refs map provider-native entities to canonical entities.

The storage interface should support:

- look up by `(source, external_id)`
- save a new external ref
- update the `last_seen_utc` timestamp when seen again
- mark or replace a primary reference

### Embeddings

Embeddings are derived data and should be written separately from canonical entity rows.

Embedding timing policy:

- Canonical upserts must not depend on successful embedding generation.
- Default behavior is deferred embedding generation after canonical write commit.
- Embeddings should be generated or regenerated when embedding document hash changes.
- Synchronous embedding generation may be used as an explicit opt-in mode for small interactive workflows.
- Retry and backfill workflows should be supported so pending or failed embeddings can be repaired later.

The storage contract should support:

- saving embedding source documents
- saving model/version-specific vectors
- querying by entity ID and model metadata
- replacing an embedding when the source document hash changes

### Matches

Matches are also derived data.

The storage contract should support:

- saving best matches
- saving ranked candidate matches
- storing evidence and confidence
- storing user override state

## Repository Responsibilities

### `ILabelRepository`

Owns canonical label persistence.

Typical operations:

- create or update a label
- fetch by label ID
- fetch by external ref
- fetch by normalized name
- manage label aliases
- manage label embeddings

### `IArtistRepository`

Owns canonical artist persistence.

Typical operations:

- create or update an artist
- fetch by artist ID
- fetch by external ref
- fetch by normalized name
- manage aliases and relationships
- manage artist embeddings

### `IReleaseRepository`

Owns canonical release persistence.

Typical operations:

- create or update a release
- fetch by release ID
- fetch by external ref
- fetch by normalized title / label combination
- manage release credits and label links
- manage release embeddings

### `IRecordingRepository`

Owns canonical recording persistence.

Typical operations:

- create or update a recording
- fetch by recording ID
- fetch by ISRC
- fetch by external ref
- fetch by normalized title / mix name patterns
- manage recording artist credits and relationships
- manage recording embeddings

### `IMediaFileRepository`

Owns scan-side file persistence.

Typical operations:

- create or update media file rows
- fetch by content hash
- fetch by path
- store extracted metadata
- store fingerprints
- store media-file embeddings
- fetch candidate rows for matching

### `IMatchRepository`

Owns file-to-recording match persistence.

Typical operations:

- save best match
- save candidate matches
- update match score/evidence
- flag user overrides
- query unresolved or ambiguous matches

### `IEmbeddingRepository`

Optional if embedding storage is centralised rather than folded into entity repositories.

Typical operations:

- save embedding source document
- save model/version vector
- fetch vectors by entity and model
- delete or replace by document hash

## Unit of Work

Multi-step operations should be able to execute in one transaction when the provider supports it.

Examples:

- upsert an entity plus its external ref and aliases
- save a recording plus artist credits plus release linkage
- save a media file plus metadata plus fingerprint plus embedding source document
- write a match result plus candidate list atomically

Suggested unit-of-work capabilities:

- begin transaction
- commit transaction
- rollback transaction
- optional retry policy only in provider-specific adapters

## Provider Contract

A provider implementation should provide:

- schema/migration execution
- connection/session creation
- repository creation
- transaction support
- provider capability reporting

Suggested capabilities:

- supports transactions
- supports case-insensitive search
- supports binary vector storage
- supports JSON payload storage
- supports indexed lookups by external ref

## SQLite as the First Provider

SQLite is the initial provider because it is:

- local
- portable
- easy to inspect
- good for development and testing

The SQLite adapter should implement the same repository interfaces as future providers.

## Azure-Hosted Provider Expectations

A future Azure-hosted provider should still satisfy the same repository interfaces, but may differ in:

- connection management
- authentication
- migration strategy
- indexing features
- JSON/vector storage shape

The goal is not to make every backend identical. The goal is to make the domain-facing contract stable.

## Recommended Initial Interface Set

If you want the smallest useful seam, start with these:

- `IStorageProvider`
- `IUnitOfWork`
- `IMediaFileRepository`
- `ILabelRepository`
- `IArtistRepository`
- `IReleaseRepository`
- `IRecordingRepository`
- `IMatchRepository`

Then add `IEmbeddingRepository` if embedding persistence needs to be centralised.

## Versioning and Migration

The storage layer should expose a migration runner contract that can:

- inspect current version
- apply pending migrations
- report migration results
- be idempotent on startup

Migrations should be provider-aware but schema-driven. The application should not need to know whether the backend is SQLite or Azure-hosted.

## Practical Boundary Decision

A good first implementation is:

- one storage provider interface
- one unit-of-work interface
- one repository per major domain entity
- one SQLite adapter
- one migration runner

That is enough to start building without overengineering the abstraction.

## Initial Contract Sketch

The first concrete contract should be small enough to implement against SQLite immediately, but still stable enough that a later hosted provider can follow the same shape.

```csharp
public interface IStorageProvider
{
  ValueTask<IUnitOfWork> BeginUnitOfWorkAsync(CancellationToken cancellationToken = default);
  IMigrationRunner Migrations { get; }
  StorageCapabilities Capabilities { get; }
}

public interface IUnitOfWork : IAsyncDisposable
{
  ILabelRepository Labels { get; }
  IArtistRepository Artists { get; }
  IReleaseRepository Releases { get; }
  IRecordingRepository Recordings { get; }
  IMediaFileRepository MediaFiles { get; }
  IMatchRepository Matches { get; }
  IEmbeddingRepository? Embeddings { get; }

  ValueTask CommitAsync(CancellationToken cancellationToken = default);
  ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

public interface ILabelRepository
{
  ValueTask<Label?> GetByIdAsync(string labelId, CancellationToken cancellationToken = default);
  ValueTask<Label?> GetByExternalRefAsync(string source, string externalId, CancellationToken cancellationToken = default);
  ValueTask<Label?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);
  ValueTask UpsertAsync(Label label, CancellationToken cancellationToken = default);
  ValueTask AddAliasAsync(string labelId, string alias, CancellationToken cancellationToken = default);
}
```

The same pattern should apply to `IArtistRepository`, `IReleaseRepository`, and `IRecordingRepository`, with entity-specific lookup methods added where they matter:

- artists: aliases and relationship lookup
- releases: label and title-based lookup
- recordings: ISRC and mix/title lookup
- media files: path, content hash, fingerprint, and extracted metadata
- matches: best match, candidate list, evidence, confidence, override state

## Import and Upsert Paths

Initial ingestion should flow through explicit repository upsert methods so Beatport or other source adapters can populate canonical rows without knowing storage details.

Suggested import/upsert path shape:

- label import/upsert: source payload -> normalized label -> external ref -> aliases
- artist import/upsert: source payload -> normalized artist -> external ref -> aliases -> relationships
- release import/upsert: source payload -> normalized release -> external ref -> artist credits -> label links
- recording import/upsert: source payload -> normalized recording -> external ref -> ISRC -> artist credits -> release linkage

The adapter that ingests source data should be responsible for preparing the canonical entity and its derived lookup fields. The repository should then persist that entity and its associated references atomically inside the unit of work.

That means each import path should be able to do the following in one transaction when the provider supports it:

- upsert the canonical entity row
- upsert or refresh the external ref row
- upsert aliases and relationship rows
- write any source payload or provenance fields
- update `last_seen_utc` for refs seen again during re-import

Repository methods should stay focused on persistence, so the import layer can compose them without requiring special-purpose bulk APIs on day one.

SQLite should implement the contract by keeping a transaction-scoped connection inside the unit of work and binding repository instances to that transaction. That keeps multi-step writes atomic without pushing SQL details into the domain layer.

For the first pass, the contract should favor these rules:

- repositories are persistence-focused, not query-everything facades
- write methods live behind the unit of work so a commit is explicit
- lookups return nullable single entities first, with list queries added only when needed
- source/external IDs remain opaque strings at the storage boundary
- SQLite-specific SQL, pragmas, and indexes stay inside the adapter
- any future remote sync adapter should sit beside the provider adapter, not inside the core repository contract
