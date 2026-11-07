namespace TrackStash.Core.Sqlite;

internal static partial class Migrations
{
    private static string MetadataEnrichment()
    {
        var sql = new System.Text.StringBuilder();
        foreach (var kind in new[] { "recording", "release" })
        {
            sql.AppendLine($"""
                CREATE TABLE {kind}_musical_metadata(
                    entity_id TEXT PRIMARY KEY NOT NULL REFERENCES {kind}({kind}_id),
                    bpm REAL CHECK(bpm IS NULL OR bpm>0 AND bpm<=1000),
                    key_value TEXT, key_notation TEXT,
                    genres_json TEXT NOT NULL CHECK(json_valid(genres_json) AND json_type(genres_json)='array'),
                    subgenres_json TEXT NOT NULL CHECK(json_valid(subgenres_json) AND json_type(subgenres_json)='array'),
                    preferred_genre TEXT,
                    CHECK((key_value IS NULL)=(key_notation IS NULL))
                    {(kind == "release" ? ",CHECK(bpm IS NULL AND key_value IS NULL)" : "")}
                );
                CREATE TRIGGER {kind}_musical_identity BEFORE UPDATE OF entity_id ON {kind}_musical_metadata
                BEGIN SELECT RAISE(ABORT,'Musical metadata identity is immutable'); END;
                """);
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var row = operation == "DELETE" ? "OLD" : "NEW";
                sql.AppendLine($"""
                    CREATE TRIGGER {kind}_musical_version_{operation.ToLowerInvariant()} AFTER {operation} ON {kind}_musical_metadata
                    BEGIN UPDATE {kind} SET version=lower(hex(randomblob(16))) WHERE {kind}_id={row}.entity_id; END;
                    """);
            }
        }
        sql.AppendLine("""
            CREATE TABLE enrichment_review(
                review_id TEXT PRIMARY KEY NOT NULL,
                entity_kind TEXT NOT NULL CHECK(entity_kind IN ('Recording','Release')),
                entity_id TEXT NOT NULL, version TEXT NOT NULL,
                payload_json TEXT NOT NULL CHECK(json_valid(payload_json)),
                created_utc TEXT NOT NULL
            );
            CREATE INDEX idx_enrichment_reviews ON enrichment_review(entity_kind,entity_id,created_utc);
            CREATE TABLE enrichment_apply_revision(
                review_id TEXT PRIMARY KEY NOT NULL REFERENCES enrichment_review(review_id),
                fields_json TEXT NOT NULL CHECK(json_valid(fields_json)),
                reason TEXT NOT NULL CHECK(length(trim(reason)) BETWEEN 1 AND 2000),
                before_json TEXT NOT NULL CHECK(json_valid(before_json)),
                after_json TEXT NOT NULL CHECK(json_valid(after_json)),
                applied_utc TEXT NOT NULL
            );
            """);
        foreach (var table in new[] { "enrichment_review", "enrichment_apply_revision" })
        {
            sql.AppendLine($"""
                CREATE TRIGGER {table}_no_update BEFORE UPDATE ON {table}
                BEGIN SELECT RAISE(ABORT,'Enrichment history is immutable'); END;
                CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table}
                BEGIN SELECT RAISE(ABORT,'Enrichment history is permanent'); END;
                """);
        }
        return sql.ToString();
    }
}
