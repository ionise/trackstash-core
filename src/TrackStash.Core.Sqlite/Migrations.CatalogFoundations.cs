using System.Text;

namespace TrackStash.Core.Sqlite;

internal static partial class Migrations
{
    private static string CatalogFoundations()
    {
        var sql = new StringBuilder();
        foreach (var entity in new[] { "artist", "release", "recording" })
        {
            sql.AppendLine($"""
                ALTER TABLE {entity} ADD COLUMN version TEXT NOT NULL DEFAULT '';
                UPDATE {entity} SET version=lower(hex(randomblob(16)));
                CREATE TRIGGER {entity}_initial_version AFTER INSERT ON {entity} WHEN NEW.version=''
                BEGIN UPDATE {entity} SET version=lower(hex(randomblob(16))) WHERE {entity}_id=NEW.{entity}_id; END;
                """);
        }
        sql.AppendLine("""
            ALTER TABLE artist ADD COLUMN artist_kind TEXT NOT NULL DEFAULT 'unknown' CHECK(artist_kind IN ('unknown','person','group','project'));
            ALTER TABLE artist ADD COLUMN identity_status TEXT NOT NULL DEFAULT 'unknown' CHECK(identity_status IN ('unknown','accepted','unresolved_compound'));
            ALTER TABLE release ADD COLUMN catalog_number TEXT;
            ALTER TABLE release ADD COLUMN upc TEXT;
            ALTER TABLE release ADD COLUMN publish_date TEXT;
            ALTER TABLE release ADD COLUMN new_release_date TEXT;
            ALTER TABLE recording ADD COLUMN duration_ms INTEGER CHECK(duration_ms IS NULL OR duration_ms>=0);
            CREATE TABLE catalog_foundation_revision(
                revision_id INTEGER PRIMARY KEY AUTOINCREMENT, entity_type TEXT NOT NULL, entity_id TEXT NOT NULL,
                action TEXT NOT NULL, before_json TEXT, after_json TEXT, recorded_utc TEXT NOT NULL
            );
            CREATE TRIGGER foundation_history_no_update BEFORE UPDATE ON catalog_foundation_revision
            BEGIN SELECT RAISE(ABORT,'Catalog foundation history is immutable'); END;
            CREATE TRIGGER foundation_history_no_delete BEFORE DELETE ON catalog_foundation_revision
            BEGIN SELECT RAISE(ABORT,'Catalog foundation history is permanent'); END;
            """);
        foreach (var entity in new[] { "release", "recording" })
        {
            var role = entity == "release" ? "'primary'" : "role";
            sql.AppendLine($"""
                ALTER TABLE {entity}_artist_credit RENAME TO {entity}_artist_credit_legacy;
                CREATE TABLE {entity}_artist_credit(
                    credit_id TEXT NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
                    {entity}_id TEXT NOT NULL REFERENCES {entity}({entity}_id),
                    artist_id TEXT NOT NULL REFERENCES artist(artist_id),
                    credit_name TEXT, role TEXT, position INTEGER,
                    source TEXT, source_path TEXT, source_expression TEXT, evidence_json TEXT,
                    legacy INTEGER NOT NULL DEFAULT 1 CHECK(legacy IN (0,1)),
                    CHECK(legacy=1 OR (role IS NOT NULL AND length(trim(role))>0 AND (position IS NULL OR position>0)))
                );
                INSERT INTO {entity}_artist_credit({entity}_id,artist_id,credit_name,role,position)
                SELECT {entity}_id,artist_id,credit_name,{role},position FROM {entity}_artist_credit_legacy;
                DROP TABLE {entity}_artist_credit_legacy;
                CREATE UNIQUE INDEX {entity}_legacy_credit ON {entity}_artist_credit({entity}_id,artist_id) WHERE legacy=1;
                CREATE INDEX {entity}_credits ON {entity}_artist_credit({entity}_id,role,position,credit_id);
                """);
        }
        sql.AppendLine("""
            ALTER TABLE release_recording RENAME TO release_recording_legacy;
            CREATE TABLE release_recording(
                placement_id TEXT NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
                release_id TEXT NOT NULL REFERENCES release(release_id),
                recording_id TEXT NOT NULL REFERENCES recording(recording_id),
                disc_number INTEGER, track_number INTEGER,
                display_order INTEGER NOT NULL DEFAULT 1 CHECK(display_order>0),
                source_position INTEGER, order_source TEXT NOT NULL DEFAULT 'legacy' CHECK(order_source IN ('legacy','source','manual','verified')),
                version TEXT NOT NULL DEFAULT '', legacy INTEGER NOT NULL DEFAULT 1 CHECK(legacy IN (0,1)),
                CHECK(legacy=1 OR ((disc_number IS NULL OR disc_number>0) AND (track_number IS NULL OR track_number>0)
                    AND (source_position IS NULL OR source_position>0)))
            );
            INSERT INTO release_recording(release_id,recording_id,disc_number,track_number,display_order,version)
            SELECT release_id,recording_id,disc_number,track_number,
                row_number() OVER(PARTITION BY release_id ORDER BY coalesce(disc_number,1),coalesce(track_number,2147483647),recording_id),
                lower(hex(randomblob(16))) FROM release_recording_legacy;
            DROP TABLE release_recording_legacy;
            CREATE UNIQUE INDEX placement_legacy_pair ON release_recording(release_id,recording_id) WHERE legacy=1;
            CREATE INDEX placement_release_order ON release_recording(release_id,display_order,placement_id);
            CREATE INDEX placement_recording ON release_recording(recording_id);
            CREATE TRIGGER placement_initial_version AFTER INSERT ON release_recording WHEN NEW.version=''
            BEGIN UPDATE release_recording SET version=lower(hex(randomblob(16))) WHERE placement_id=NEW.placement_id; END;
            CREATE TRIGGER placement_version AFTER UPDATE OF disc_number,track_number,display_order,source_position,order_source ON release_recording
            BEGIN UPDATE release_recording SET version=lower(hex(randomblob(16))) WHERE placement_id=NEW.placement_id; END;
            CREATE TRIGGER placement_identity_immutable BEFORE UPDATE OF placement_id,release_id,recording_id ON release_recording
            WHEN NEW.placement_id<>OLD.placement_id OR NEW.release_id<>OLD.release_id OR NEW.recording_id<>OLD.recording_id
            BEGIN SELECT RAISE(ABORT,'Placement endpoints are immutable'); END;
            CREATE TABLE placement_external_ref(
                placement_id TEXT NOT NULL REFERENCES release_recording(placement_id),
                source TEXT NOT NULL, external_id TEXT NOT NULL, is_primary INTEGER NOT NULL DEFAULT 0,
                last_seen_utc TEXT, payload_json TEXT, PRIMARY KEY(source,external_id)
            );
            CREATE TABLE placement_artist_credit(
                credit_id TEXT NOT NULL PRIMARY KEY,
                placement_id TEXT NOT NULL REFERENCES release_recording(placement_id),
                artist_id TEXT NOT NULL REFERENCES artist(artist_id),
                credit_name TEXT, role TEXT NOT NULL CHECK(length(trim(role))>0), position INTEGER CHECK(position IS NULL OR position>0),
                source TEXT, source_path TEXT, source_expression TEXT, evidence_json TEXT
            );
            CREATE INDEX placement_credits ON placement_artist_credit(placement_id,role,position,credit_id);
            CREATE TRIGGER placement_reference_identity_immutable BEFORE UPDATE OF placement_id,source,external_id ON placement_external_ref
            WHEN NEW.placement_id<>OLD.placement_id OR NEW.source<>OLD.source OR NEW.external_id<>OLD.external_id
            BEGIN SELECT RAISE(ABORT,'Placement reference ownership is immutable'); END;
            CREATE TRIGGER placement_credit_owner_immutable BEFORE UPDATE OF placement_id,credit_id ON placement_artist_credit
            WHEN NEW.placement_id<>OLD.placement_id OR NEW.credit_id<>OLD.credit_id
            BEGIN SELECT RAISE(ABORT,'Credit identity/owner is immutable'); END;
            """);
        foreach (var entity in new[] { "artist", "release", "recording" })
        {
            // Existing ambiguous references are retained; new competing ownership is rejected.
            foreach (var op in new[] { "INSERT", "UPDATE" })
                sql.AppendLine($"""
                    CREATE TRIGGER {entity}_reference_owner_{op.ToLowerInvariant()} BEFORE {op} ON {entity}_external_ref
                    WHEN EXISTS(SELECT 1 FROM {entity}_external_ref r WHERE r.source=NEW.source AND r.external_id=NEW.external_id AND r.{entity}_id<>NEW.{entity}_id)
                    BEGIN SELECT RAISE(ABORT,'Conflicting canonical provider reference'); END;
                    """);
            var extras = entity switch
            {
                "artist" => ",sort_name,artist_kind,identity_status",
                "release" => ",title,catalog_number,upc,publish_date,new_release_date",
                _ => ",title,mix_name,isrc,duration_ms",
            };
            sql.AppendLine($"""
                CREATE TRIGGER {entity}_aggregate_version AFTER UPDATE OF name,normalized_name,source_payload_json,updated_utc{extras} ON {entity}
                BEGIN UPDATE {entity} SET version=lower(hex(randomblob(16))) WHERE {entity}_id=NEW.{entity}_id; END;
                """);
            var children = entity switch
            {
                "artist" => new[] { "artist_alias", "artist_external_ref" },
                "release" => new[] { "release_external_ref", "release_artist_credit", "release_label_link", "release_recording" },
                _ => new[] { "recording_external_ref", "recording_artist_credit", "recording_relationship", "release_recording" },
            };
            foreach (var child in children)
            foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var column = child == "recording_relationship" ? "from_recording_id" : entity + "_id";
                var old = op == "INSERT" ? "" : $"UPDATE {entity} SET version=lower(hex(randomblob(16))) WHERE {entity}_id=OLD.{column};";
                var next = op == "DELETE" ? "" : $"UPDATE {entity} SET version=lower(hex(randomblob(16))) WHERE {entity}_id=NEW.{column};";
                sql.AppendLine($"""
                    CREATE TRIGGER foundation_{entity}_{child}_{op.ToLowerInvariant()} AFTER {op} ON {child}
                    BEGIN {old} {next} END;
                    """);
            }
        }
        foreach (var child in new[] { "placement_external_ref", "placement_artist_credit" })
        foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var row = op == "DELETE" ? "OLD" : "NEW";
            sql.AppendLine($"""
                CREATE TRIGGER foundation_{child}_{op.ToLowerInvariant()} AFTER {op} ON {child}
                BEGIN
                    UPDATE release_recording SET version=lower(hex(randomblob(16))) WHERE placement_id={row}.placement_id;
                END;
                """);
        }
        foreach (var entity in new[] { "release", "recording" })
            sql.AppendLine($"""
                CREATE TRIGGER {entity}_credit_owner_immutable BEFORE UPDATE OF {entity}_id,credit_id ON {entity}_artist_credit
                WHEN NEW.{entity}_id<>OLD.{entity}_id OR NEW.credit_id<>OLD.credit_id
                BEGIN SELECT RAISE(ABORT,'Credit identity/owner is immutable'); END;
                """);
        return sql.ToString();
    }
}
