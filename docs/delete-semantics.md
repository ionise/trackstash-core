# Entity Deletion Semantics

Status: defined
Last updated: 2026-06-20

This document captures the canonical delete rules for the four entity types:
`label`, `artist`, `release`, and `recording`.

Related documents:

- [label-schema.md](label-schema.md)
- [artist-schema.md](artist-schema.md)
- [release-schema.md](release-schema.md)
- [recording-schema.md](recording-schema.md)
- [storage-interface.md](storage-interface.md)

## Current FK Behavior in the SQLite Migration

The SQLite migration at `src/TrackStash.Core.Sqlite/Migrations.cs` uses bare `REFERENCES`
constraints with **no `ON DELETE` clause**. In SQLite this defaults to `NO ACTION`, meaning
the database will reject any `DELETE` that would leave orphaned FK rows.

This differs from several schema design docs (`label-schema.md`, `artist-schema.md`, etc.)
that describe `ON DELETE CASCADE` and `ON DELETE SET NULL`. Those docs are design notes
predating the current implementation. See the reconciliation note at the end of this document.

**Key implication:** all child row cleanup must be explicit. The delete service cannot rely
on database-level cascades to clean up any rows automatically.

## Blockers vs Owned Cleanup

The delete service distinguishes two dependency categories.

**Blockers** are rows that point to an entity from outside its own ownership boundary.
They represent a meaningful relationship that a human or operator must resolve before
the entity can be removed. The delete service should report these and refuse to proceed
while any remain.

**Owned cleanup rows** are rows whose existence is tied entirely to the entity being deleted.
The delete service should remove these automatically within the same transaction, with no
operator action required.

## Delete Rules Per Entity

### Label

Blockers — must be cleared first:

| Table | Column |
| --- | --- |
| `release_label_link` | `label_id` |

Owned cleanup — deleted automatically in the same transaction:

| Table | Column |
| --- | --- |
| `label_external_ref` | `label_id` |
| `label_alias` | `label_id` |
| `embedding_document` | `entity_id` where `entity_type = 'label'` |

---

### Artist

Blockers — must be cleared first:

| Table | Column |
| --- | --- |
| `release_artist_credit` | `artist_id` |
| `recording_artist_credit` | `artist_id` |

Owned cleanup — deleted automatically in the same transaction:

| Table | Column |
| --- | --- |
| `artist_external_ref` | `artist_id` |
| `artist_alias` | `artist_id` |
| `embedding_document` | `entity_id` where `entity_type = 'artist'` |

Note: there is no separate `artist_relationship` table in the current SQLite migration.
Artist-to-artist relationships are stored in the `EntityRelationship` model field but
are not yet persisted to a dedicated table.

---

### Release

Blockers — must be cleared first:

| Table | Column | Why |
| --- | --- | --- |
| `release_recording` | `release_id` | recordings may belong to other releases and must not be implicitly deleted |

Owned cleanup — deleted automatically in the same transaction:

| Table | Column |
| --- | --- |
| `release_external_ref` | `release_id` |
| `release_artist_credit` | `release_id` |
| `release_label_link` | `release_id` |
| `embedding_document` | `entity_id` where `entity_type = 'release'` |

Note: `release_label_link` is owned cleanup when deleting a **release**, but is a **blocker**
when deleting a **label**. This is the intentional join-table asymmetry: a release owns its
own label associations; a label does not own the releases that reference it.

Similarly, `release_artist_credit` is owned cleanup when deleting a **release**, but is a
**blocker** when deleting an **artist**.

---

### Recording

Blockers — must be cleared first:

| Table | Column | Why |
| --- | --- | --- |
| `release_recording` | `recording_id` | a recording may belong to multiple releases |
| `recording_relationship` | `from_recording_id` or `to_recording_id` | both sides of a relationship must agree |
| `media_file_recording_match` | `recording_id` | downstream match state from another module |
| `media_file_recording_candidate` | `recording_id` | downstream candidate state from another module |

Owned cleanup — deleted automatically in the same transaction:

| Table | Column |
| --- | --- |
| `recording_external_ref` | `recording_id` |
| `recording_artist_credit` | `recording_id` |
| `embedding_document` | `entity_id` where `entity_type = 'recording'` |

Note: `recording_relationship` is always a blocker from both sides. An orphaned relationship
row pointing at a deleted recording would be corrupt data. Both directions must be absent
before a recording can be deleted.

Note: `release_recording` is a **blocker** when deleting a **recording**, but is **owned cleanup**
when deleting a **release**. A recording may belong to many releases, so the operator must
explicitly remove or reassign those placements. A release, however, owns its own track list.

## Join-Table Asymmetry Reference

| Table | Deleting release | Deleting recording | Deleting label | Deleting artist |
| --- | --- | --- | --- | --- |
| `release_label_link` | Owned cleanup | — | **Blocker** | — |
| `release_artist_credit` | Owned cleanup | — | — | **Blocker** |
| `release_recording` | Owned cleanup | **Blocker** | — | — |
| `recording_artist_credit` | — | Owned cleanup | — | **Blocker** |
| `recording_relationship` | — | **Blocker** (both sides) | — | — |
| `media_file_recording_match` | — | **Blocker** | — | — |
| `media_file_recording_candidate` | — | **Blocker** | — | — |

## Artist Credit Nullability

The schema design docs describe `release_artist_credit.artist_id` and
`recording_artist_credit.artist_id` as nullable (`TEXT NULL`) with `ON DELETE SET NULL`,
which would allow a credit line to survive after an artist entity is deleted.

The current SQLite migration defines both as `TEXT NOT NULL`. This means:

- Artist deletion is blocked whenever any artist credit rows reference the artist.
- There is no SET NULL path in the current schema.

This is a deliberate simplification. If SET NULL semantics are needed in a future iteration
(for example, to preserve credited name text after artist entity removal), a schema migration
will be needed to make `artist_id` nullable in both credit tables.

For now, `release_artist_credit` and `recording_artist_credit` are treated as **blockers**
for artist deletion.

## Schema Doc Reconciliation Note

The schema docs under `docs/` (`label-schema.md`, `artist-schema.md`, `release-schema.md`,
`recording-schema.md`) predate the current implementation and contain several differences
from the actual SQLite migration:

1. **Status headers** still say "design only, not yet applied" — the core tables are now
   implemented, though with a simplified column set.

2. **Column names differ** — docs use `canonical_name`, `display_name`, `title_norm` etc.;
   migration uses `name` and `normalized_name`.

3. **ON DELETE clauses** — docs describe cascade/set-null/restrict; migration has no cascade.

4. **Embedding tables** — docs describe per-entity embedding tables
   (`label_embedding`, `artist_embedding`, etc.); migration uses a single shared
   `embedding_document` table.

5. **artist_relationship table** — docs describe an `artist_relationship` table; migration
   has no such table. Artist relationships are stored in model layer only at this time.

6. **artist_id nullability** in credit tables — docs say nullable, migration says NOT NULL.

7. **Rich optional columns** (bpm, key, genre, duration_ms, etc.) in recording and release
   docs are not present in the current migration.

The schema docs should be treated as design notes for future column enrichment, not as
descriptions of the current database state. Delete logic and service contracts should be
implemented against the actual migration, not the doc DDL skeletons.

## Recycle Bin Requirement

The delete feature should include a core recycle-bin requirement before any implementation
is considered complete.

Preferred behavior:

- capture the deleted canonical entity and its owned cleanup rows in a tombstone/journal table
- perform the journal write in the same transaction as the delete
- keep the live delete semantics strict, so blockers still prevent unsafe removal
- treat restore as a separate operation that validates current dependency state before replaying

Restore limits:

- restore should not silently recreate downstream match or embedding state; those are derived and should be rebuilt
- restore should refuse if the entity id or required ownership edges now conflict with current live data
- restore should respect the same blocker rules that apply to a live entity, unless the caller explicitly repairs dependencies first

This requirement is intentionally a core contract item, not just a UI convenience feature.
