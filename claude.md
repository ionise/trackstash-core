# GitHub Copilot Project Prompt: trackstash-core

You are assisting with the implementation of **trackstash-core**, the shared architecture and contract layer for the TrackStash ecosystem.

## Purpose
trackstash-core has ONE responsibility:
Define the shared domain model, repository contracts, provider abstraction, and ecosystem-level documentation for TrackStash.

It is the home for shared concepts used by:
- `trackstash-scan`
- future `trackstash-catalog`
- future `trackstash-match`
- future `trackstash-tag`
- future `trackstash-organize` integrations

trackstash-core is not a scanner, not a tagging engine, and not a Beatport client.

## Mandatory Design Rules
- Keep `trackstash-scan` focused on filesystem scanning, metadata extraction, hashing, and fingerprinting.
- Keep cataloging, matching, and writeback separate from scanning.
- Treat storage as a provider boundary.
- Design for SQLite first, but do not hard-code SQLite assumptions into shared contracts.
- Allow future relational or cloud-backed providers without rewriting the scanner.
- Keep CosmosDB-style or document-store layouts isolated behind adapters if they are ever introduced.

## Core Responsibilities

### 1. Shared documentation
trackstash-core is the canonical home for the ecosystem documentation, including:
- module boundaries
- storage/provider strategy
- schema conventions
- canonical entity schemas
- media matching schema
- roadmap and TODOs for cross-module work

### 2. Shared contracts
Define interfaces and abstractions for:
- label repositories
- release repositories
- artist repositories
- recording repositories
- media-file repositories
- match repositories
- embedding/document repositories

### 3. Canonical domain model
Define the concepts and rules that all modules should agree on:
- canonical entity identity
- external references
- aliases
- relationships
- credits
- embeddings
- match provenance

### 4. Storage provider abstraction
Provide the seam between the domain layer and the backing store.
The abstraction should support:
- SQLite for local development
- a future relational backend
- a future cloud backend
- a future document-store adapter if needed

## Non-Goals
trackstash-core must NOT:
- scan filesystems
- parse tags directly from media files
- compute audio hashes or fingerprints
- call Beatport APIs directly
- move or rename files
- write tags into files
- implement UI code
- implement application-specific business logic that belongs in downstream modules

## Architectural Principles
- Single responsibility: shared contracts and shared architecture only.
- Provider agnostic: code should depend on interfaces, not a specific database engine.
- Modular: each downstream module should own one job.
- Stable canonical identity: use GUID-based IDs for canonical entities.
- Additive evolution: prefer new tables or fields over destructive schema changes.
- Keep local concerns local: `trackstash-scan` remains close to the filesystem.

## Working Model
The clean split is:
- `trackstash-scan` = file discovery and extraction
- `trackstash-core` = shared contracts, schemas, and provider abstractions
- `trackstash-catalog` = canonical metadata ingestion and persistence
- `trackstash-match` = file-to-recording resolution
- `trackstash-tag` = writeback to media files
- `trackstash-organize` = file relocation/copy workflows
- `trackstash-transcode` = audio conversion/transcoding workflows

## What Copilot Should Generate
- shared interfaces and repository contracts
- provider abstraction boundaries
- ecosystem documentation
- migration strategy for shared schemas
- TODO/roadmap items for cross-module work
- small, composable helper utilities that are genuinely shared

## What Copilot Should Avoid
- adding scanner-specific logic
- adding database-engine-specific assumptions to domain contracts
- adding embedding or matching code directly into the scanner layer
- duplicating responsibilities that should live in downstream modules
- inventing new data stores unless they are hidden behind the shared abstraction

## Documentation Guidance
If a new feature crosses module boundaries, document:
1. which module owns the behavior
2. which module owns the storage
3. whether the data is canonical, derived, or transient
4. whether the behavior is local-only or provider-agnostic

## Follow This Prompt Strictly
Keep `trackstash-core` small, explicit, and focused on the shared foundation of the ecosystem.
