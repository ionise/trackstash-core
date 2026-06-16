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

### Recommended Multi-Key Strategy

Use three name representations during ingestion:

- `display_value`: original source string, preserved for provenance and UI.
- `strict_norm`: deterministic normalization key used for high-confidence matching.
- `loose_norm`: reduced key for candidate generation only (never sole auto-merge key).

Suggested `strict_norm` transforms:

1. Trim and lowercase.
2. Unicode normalize to NFKC.
3. Fold quote variants to ASCII (`’` -> `'`).
4. Remove punctuation separators that are usually stylistic (`'`, `.`, `:`, `•`, `-`).
5. Collapse whitespace.

Suggested `loose_norm` transforms (after `strict_norm`):

1. Remove known filler/legal suffix tokens such as `records`, `recordings`, `music`, `ltd`, `limited`, `catalogue`.
2. Keep remaining token order stable.

Important:

- `loose_norm` is for ranking and review candidate generation only.
- Auto-merge should not be driven by `loose_norm` alone.

### Delimiter and Compound Value Handling

Some source strings contain multiple labels or organizations in one field.

During ingestion, split compound values into candidates before normalization when delimiters are clearly structural:

- newline (`\n`)
- slash (`/`)
- semicolon (`;`)

Treat split results as candidate identities and keep original unsplit text in provenance payloads.

### Duplicate-Avoidance Lookup Order

Apply this lookup order before creating a canonical row:

1. External reference match `(source, external_id)`.
2. Unique `strict_norm` match.
3. Candidate set from `loose_norm` for manual or rule-assisted review.
4. Create new canonical identity only when no deterministic match exists.

If more than one plausible candidate remains after step 3, return a review-required result and do not auto-merge.

### Entity-Specific Merge Guidance

Use the same framework across domains, but with different confidence thresholds.

#### Labels

- Most suitable for strict-name auto-merge after external-ref lookup.
- Punctuation/style variants should generally map to one canonical label identity.
- Keep all observed variants as aliases.

#### Artists

- More collision-prone than labels.
- Require stronger evidence for auto-merge than name similarity alone.
- Prefer external refs and additional contextual evidence when available.

#### Releases

- Avoid title-only merge decisions.
- Prefer composite identity checks: normalized title + artist set + label (+ year/catalog when available).

#### Recordings

- ISRC is strongest when trustworthy.
- Otherwise rely on multi-signal matching (title, mix/version, artist credits, duration/fingerprint evidence).
- Do not collapse known version variants (for example `Radio Edit` vs `Extended Mix`) without strong evidence.

### Auto-Merge Confidence Tiers

- `high`: external ref match or unique strict match with corroborating evidence -> auto-merge.
- `medium`: strict match without corroboration -> configurable, default review.
- `low`: loose match only -> review required.

### Iteration Policy During Real Ingestion

Normalization rules should be expected to evolve as real catalog data is ingested.

Required process:

1. Log normalization decisions and review outcomes.
2. Capture false positives and false negatives in regression fixtures.
3. Update normalization/token rules in shared `trackstash-core` utilities.
4. Re-run fixture tests before rollout.
5. Version notable rule changes in docs and release notes.

Rule-change principle:

- Favor precision over recall for auto-merge paths.
- It is better to queue a human review than to merge two distinct canonical identities.

### Synthetic Fixture Examples

Use synthetic fixture names for tests and documentation examples to avoid leaking real entities.

Suggested canonical and variation groups:

- `Distynqive Records` <- `Distyn'qive Records`, `Distynqive`
- `OneDotSevenThree` <- `onedotseventhree`, `One Dot Seven Three`
- `EnVisiqn Recordings` <- `En:Visiqn Recordings`, `en:visiqn recordings`
- `Bozra Bozra` <- `BozraBozra`
- `Absolutely No Tolerance Audioworks` <- `Absolutely No Tolerance`

Compound-value fixture examples (split before normalization):

- `Virgina\nFreestyle Dusk`
- `Cheeky Orbit\nFestival Orbit`

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
