using System.Text;

namespace TrackStash.Core.Sqlite;

internal static partial class Migrations
{
    private static string ReleaseImports()
    {
        var sql = new StringBuilder("""
            CREATE TABLE release_import_state(singleton INTEGER PRIMARY KEY CHECK(singleton=1), version TEXT NOT NULL);
            INSERT INTO release_import_state VALUES(1,lower(hex(randomblob(16))));
            CREATE TABLE release_import_preview(
                operation_id TEXT PRIMARY KEY NOT NULL, version TEXT NOT NULL, plan_json TEXT NOT NULL CHECK(json_valid(plan_json))
            );
            CREATE TABLE release_import_outcome(
                operation_id TEXT PRIMARY KEY NOT NULL REFERENCES release_import_preview(operation_id),
                reason TEXT NOT NULL, outcome_json TEXT NOT NULL CHECK(json_valid(outcome_json)),
                audit_json TEXT NOT NULL CHECK(json_valid(audit_json))
            );
            CREATE TABLE release_import_supersession(
                previous_operation_id TEXT PRIMARY KEY NOT NULL REFERENCES release_import_preview(operation_id),
                next_operation_id TEXT UNIQUE NOT NULL REFERENCES release_import_preview(operation_id)
            );
            CREATE TABLE release_import_binding(
                source TEXT NOT NULL, external_id TEXT NOT NULL, release_id TEXT NOT NULL REFERENCES release(release_id),
                source_digest TEXT NOT NULL, source_fingerprint TEXT NOT NULL, operation_id TEXT NOT NULL REFERENCES release_import_preview(operation_id),
                PRIMARY KEY(source,external_id)
            );
            CREATE TABLE catalog_embedding_source(
                entity_type TEXT NOT NULL, entity_id TEXT NOT NULL, template_version TEXT NOT NULL,
                document_text TEXT NOT NULL, desired_hash TEXT NOT NULL, generated_hash TEXT,
                status TEXT NOT NULL CHECK(status IN ('pending','stale','current','failed')),
                PRIMARY KEY(entity_type,entity_id)
            );
            CREATE TABLE catalog_embedding_outbox(
                entity_type TEXT NOT NULL, entity_id TEXT NOT NULL, revision TEXT NOT NULL,
                enqueued_utc TEXT NOT NULL, PRIMARY KEY(entity_type,entity_id)
            );
            """);
        foreach (var table in new[] { "release_import_preview", "release_import_outcome", "release_import_binding", "release_import_supersession" })
        foreach (var op in new[] { "UPDATE", "DELETE" })
            sql.AppendLine($"""
                CREATE TRIGGER {table}_no_{op.ToLowerInvariant()} BEFORE {op} ON {table}
                BEGIN SELECT RAISE(ABORT,'Release import review/outcome/provenance is permanent and immutable'); END;
                """);
        foreach (var table in new[]
        {
            "label","label_external_ref","retired_label","artist","artist_alias","artist_external_ref",
            "release","release_external_ref","release_label_link","release_artist_credit",
            "recording","recording_external_ref","recording_artist_credit","recording_relationship",
            "release_recording","placement_artist_credit","placement_external_ref","release_import_binding",
        })
        foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            sql.AppendLine($"""
                CREATE TRIGGER import_stamp_{table}_{op.ToLowerInvariant()} AFTER {op} ON {table}
                BEGIN UPDATE release_import_state SET version=lower(hex(randomblob(16))) WHERE singleton=1; END;
                """);
        }
        // Until a processor is activated, canonical edits retain durable rebuild requests instead of obsolete vectors.
        foreach (var table in new[]
        {
            "label","artist","artist_alias","release","release_label_link","release_artist_credit",
            "recording","recording_artist_credit",
        })
        foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var rows = op == "INSERT" ? new[] { "NEW" } : op == "DELETE" ? new[] { "OLD" } : new[] { "OLD", "NEW" };
            string Predicate(string row) => table switch
            {
                "artist" or "artist_alias" => $"""
                    (entity_type='artist' AND entity_id={row}.artist_id)
                    OR (entity_type='release' AND entity_id IN (SELECT release_id FROM release_artist_credit WHERE artist_id={row}.artist_id))
                    OR (entity_type='recording' AND entity_id IN (SELECT recording_id FROM recording_artist_credit WHERE artist_id={row}.artist_id))
                    """,
                "label" => $"(entity_type='release' AND entity_id IN (SELECT release_id FROM release_label_link WHERE label_id={row}.label_id))",
                "release" or "release_label_link" or "release_artist_credit" => $"(entity_type='release' AND entity_id={row}.release_id)",
                _ => $"(entity_type='recording' AND entity_id={row}.recording_id)",
            };
            var predicate = string.Join(" OR ", rows.Select(Predicate));
            var changedColumns = table switch
            {
                "artist" => "name,artist_kind,identity_status",
                "artist_alias" => "value",
                "label" => "name",
                "release" => "title,catalog_number,upc,publish_date,new_release_date",
                "recording" => "title,mix_name,isrc,duration_ms",
                "release_label_link" => "label_id,is_primary,role",
                _ => "artist_id,role,position,credit_name",
            };
            var updateColumns = op == "UPDATE" ? " OF " + changedColumns : "";
            var changedValues = op == "UPDATE" ? " WHEN " + string.Join(" OR ", changedColumns.Split(',').Select(column => $"OLD.{column} IS NOT NEW.{column}")) : "";
            sql.AppendLine($"""
                CREATE TRIGGER embedding_invalidate_{table}_{op.ToLowerInvariant()} AFTER {op}{updateColumns} ON {table}{changedValues}
                BEGIN
                    UPDATE catalog_embedding_source SET status='stale' WHERE {predicate};
                    INSERT INTO catalog_embedding_outbox(entity_type,entity_id,revision,enqueued_utc)
                    SELECT entity_type,entity_id,lower(hex(randomblob(16))),strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    FROM catalog_embedding_source WHERE {predicate}
                    ON CONFLICT(entity_type,entity_id) DO UPDATE SET revision=excluded.revision,enqueued_utc=excluded.enqueued_utc;
                END;
                """);
        }
        return sql.ToString();
    }
}
