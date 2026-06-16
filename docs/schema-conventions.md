# Schema Conventions (Shared)

Shared conventions for canonical entities and external-reference tables.

Status: design guidance for future migrations.

## ID Generation

- Canonical entity IDs use GUID text (`TEXT PRIMARY KEY` in SQLite).
- Generate IDs in application code (PowerShell: `New-Guid`) rather than DB-side random SQL.
- Store lowercase GUIDs in 36-char canonical form.
- IDs are immutable and never repurposed.

Examples:

- `label.label_id`
- `release.release_id`

## Normalization Rules

Use normalized keys to support deterministic matching and dedupe.

Recommended normalization pipeline for `*_norm` fields:

1. Trim leading/trailing whitespace.
2. Convert to lowercase.
3. Normalize Unicode to NFKC.
4. Collapse internal whitespace runs to a single space.
5. Remove surrounding punctuation-only wrappers.

Apply to:

- `label.canonical_name_norm`
- `label_alias.alias_name_norm`
- `release.title_norm`

Notes:

- Keep original display values in non-normalized columns.
- Do not overwrite source text with normalized text.

## Timestamp Standards

- Store timestamps as UTC ISO-8601 text in SQLite.
- Preferred format: `yyyy-MM-ddTHH:mm:ss.fffZ`.
- Columns ending with `_utc` must always be UTC.
- `created_utc` is write-once.
- `updated_utc` must update on every material mutation.
- For external refs:
  - `first_seen_utc` set when link is first created.
  - `last_seen_utc` updated whenever the same external link is observed again.

## `is_primary` Semantics

`is_primary` marks the preferred external reference for a given entity within a source.

Rules:

- `is_primary` is scoped to `(entity_id, source)`.
- At most one row should have `is_primary = 1` per `(entity_id, source)`.
- Multiple rows with `is_primary = 0` are allowed.
- If only one external ref exists for `(entity_id, source)`, set it to primary.

Implementation options:

- Enforce in application logic, or
- Add SQLite trigger(s) to clear existing primary row before setting a new one.

## External Reference Source Names

- Use lowercase stable slugs in `source`.
- Recommended initial values: `beatport`, `musicbrainz`, `discogs`.
- Treat unknown/future providers as first-class values without schema change.

## JSON Payload Policy

- Use `source_payload_json` for provider-specific fields that are not canonical.
- Store valid JSON text only.
- Keep canonical columns as source-agnostic as possible.
- Prefer additive payload updates; avoid destructive rewrites unless data is stale/invalid.
