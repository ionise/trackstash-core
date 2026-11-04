using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed class SqliteLabelMergeRepository(SqliteConnection connection, SqliteTransaction transaction) : ILabelMergeRepository
{
    private readonly SqliteLabelRepository _labels = new(connection, transaction);
    private const int PlanFormatVersion = 1;
    private sealed record Plan(Label Survivor, Label Retired, Label Combined, MergeReleaseLink[] Links,
        Dictionary<string, List<Dictionary<string, object?>>> Rows, string[] Blockers)
    {
        public string Snapshot => JsonSerializer.Serialize(new { PlanFormatVersion, Survivor, Retired, Combined, Links, Rows, Blockers });
        public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Snapshot)));
    }

    public async ValueTask<LabelMergeResult> PreviewAsync(string survivorId, string retiredId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(survivorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(retiredId);
        if (survivorId == retiredId) throw new ArgumentException("Choose two different labels.", nameof(retiredId));
        var plan = await InspectAsync(survivorId, retiredId, cancellationToken).ConfigureAwait(false);
        if (plan is null) return new(LabelMergeStatus.NotFound);
        var preview = new LabelMergePreview(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), now.AddMinutes(15),
            Summary(plan.Survivor), Summary(plan.Retired),
            plan.Combined.Aliases.Select(a => new MergeAlias(a.Value, a.IsPrimary)).ToArray(),
            plan.Combined.ExternalReferences.Select(r => new MergeReference(r.Source, r.ExternalId, r.IsPrimary, r.LastSeenUtc)).ToArray(),
            plan.Links, plan.Blockers,
            ["The retired ID will redirect permanently. This operation has no automatic undo.",
             "Survivor name and sort name are retained; transferred aliases are non-primary.",
             "Operator attribution is unverified; the API key represents a shared local credential."]);
        await ExecuteAsync("""
            INSERT INTO label_merge_operation(operation_id, survivor_id, retired_id, version, expires_utc, state_hash, preview_json)
            VALUES (@op, @s, @r, @version, @expires, @hash, @preview)
            """, cancellationToken, ("op", preview.OperationId), ("s", survivorId), ("r", retiredId),
            ("version", preview.Version), ("expires", preview.ExpiresUtc.ToString("O")), ("hash", plan.Hash),
            ("preview", JsonSerializer.Serialize(preview))).ConfigureAwait(false);
        return new(LabelMergeStatus.Ready, preview);
    }

    public async ValueTask<LabelMergeResult> GetPreviewAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var rows = await RowsAsync("SELECT preview_json, outcome_json FROM label_merge_operation WHERE operation_id = @op",
            cancellationToken, ("op", operationId)).ConfigureAwait(false);
        if (rows.Count == 0) return new(LabelMergeStatus.NotFound);
        return new(LabelMergeStatus.Ready, Deserialize<LabelMergePreview>((string)rows[0]["preview_json"]!),
            rows[0]["outcome_json"] is string json ? Deserialize<LabelMergeOutcome>(json) : null);
    }

    public async ValueTask<LabelMergeResult> ApplyAsync(string operationId, string expectedVersion, string reason,
        string? operatorName, string principal, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        if (reason.Length > 2000 || operatorName?.Length > 200) throw new ArgumentException("Merge attribution is too long.");
        var operations = await RowsAsync("SELECT * FROM label_merge_operation WHERE operation_id = @op", cancellationToken,
            ("op", operationId)).ConfigureAwait(false);
        if (operations.Count == 0) return new(LabelMergeStatus.NotFound);
        var operation = operations[0];
        if ((string)operation["version"]! != expectedVersion) return new(LabelMergeStatus.Stale);
        if (operation["outcome_json"] is string outcomeJson)
            return (string?)operation["reason"] == reason && (string?)operation["operator_name"] == operatorName
                && (string?)operation["principal"] == principal
                ? new(LabelMergeStatus.Applied, Outcome: Deserialize<LabelMergeOutcome>(outcomeJson))
                : new(LabelMergeStatus.ReplayConflict);
        if (DateTimeOffset.Parse((string)operation["expires_utc"]!) <= now) return new(LabelMergeStatus.Expired);
        var survivorId = (string)operation["survivor_id"]!;
        var retiredId = (string)operation["retired_id"]!;
        var plan = await InspectAsync(survivorId, retiredId, cancellationToken).ConfigureAwait(false);
        if (plan is null || plan.Hash != (string)operation["state_hash"]!) return new(LabelMergeStatus.Stale);
        if (plan.Blockers.Length != 0) return new(LabelMergeStatus.Blocked);

        // A savepoint also protects callers that catch a write failure and later commit their unit of work.
        await ExecuteAsync("SAVEPOINT label_merge_apply", cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync("DELETE FROM label_alias WHERE label_id IN (@s, @r); DELETE FROM label_external_ref WHERE label_id IN (@s, @r);",
                cancellationToken, ("s", survivorId), ("r", retiredId)).ConfigureAwait(false);
            await _labels.UpsertAsync(plan.Combined with { UpdatedUtc = now }, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("DELETE FROM release_label_link WHERE label_id IN (@s, @r)", cancellationToken,
                ("s", survivorId), ("r", retiredId)).ConfigureAwait(false);
            foreach (var link in plan.Links)
                await ExecuteAsync("""
                    INSERT INTO release_label_link(release_id, label_id, is_primary, role) VALUES (@release, @s, @primary, @role)
                    """, cancellationToken, ("release", link.ReleaseId), ("s", survivorId),
                    ("primary", link.IsPrimary), ("role", link.Role)).ConfigureAwait(false);

            var models = plan.Rows["embedding_document"].Concat(plan.Rows["embedding_work_item"])
                .Select(r => ((string)r["model_name"]!, (string)r["model_version"]!)).Distinct().OrderBy(x => x.Item1, StringComparer.Ordinal)
                .ThenBy(x => x.Item2, StringComparer.Ordinal).ToArray();
            await ExecuteAsync("""
                DELETE FROM embedding_document WHERE lower(entity_type) = 'label' AND entity_id IN (@s, @r);
                DELETE FROM embedding_work_item WHERE lower(entity_type) = 'label' AND entity_id IN (@s, @r);
                UPDATE retired_label SET canonical_id = @s WHERE canonical_id = @r;
                DELETE FROM label WHERE label_id = @r;
                """, cancellationToken, ("s", survivorId), ("r", retiredId)).ConfigureAwait(false);
            foreach (var model in models)
                await ExecuteAsync("""
                    INSERT INTO embedding_work_item(work_item_id, entity_id, entity_type, model_name, model_version,
                        operation, force_regenerate, status, attempt_count, enqueued_utc)
                    VALUES (@id, @s, 'Label', @model, @modelVersion, 'Updated', 1, 'Pending', 0, @now)
                    """, cancellationToken, ("id", Guid.NewGuid().ToString("N")), ("s", survivorId),
                    ("model", model.Item1), ("modelVersion", model.Item2), ("now", now.ToString("O"))).ConfigureAwait(false);
            var outcome = new LabelMergeOutcome(operationId, Guid.NewGuid().ToString("N"), survivorId, now);
            var after = await _labels.GetByIdAsync(survivorId, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("""
                INSERT INTO label_merge_audit(audit_id, operation_id, survivor_id, retired_id, applied_utc, principal,
                    operator_name, reason, before_json, after_json)
                VALUES (@audit, @op, @s, @r, @now, @principal, @operator, @reason, @before, @after);
                INSERT INTO retired_label(retired_id, canonical_id, retired_utc, audit_id) VALUES (@r, @s, @now, @audit);
                UPDATE label_merge_operation SET reason = @reason, operator_name = @operator, principal = @principal,
                    outcome_json = @outcome WHERE operation_id = @op;
                """, cancellationToken, ("audit", outcome.AuditId), ("op", operationId), ("s", survivorId),
                ("r", retiredId), ("now", now.ToString("O")), ("principal", principal), ("operator", operatorName),
                ("reason", reason), ("before", plan.Snapshot),
                ("after", JsonSerializer.Serialize(new { PlanFormatVersion, Label = after, plan.Links, outcome,
                    Embeddings = "Both invalidated; known models queued for survivor regeneration", RedirectTarget = survivorId })),
                ("outcome", JsonSerializer.Serialize(outcome))).ConfigureAwait(false);
            await ExecuteAsync("RELEASE label_merge_apply", cancellationToken).ConfigureAwait(false);
            return new(LabelMergeStatus.Applied, Outcome: outcome);
        }
        catch
        {
            await ExecuteAsync("ROLLBACK TO label_merge_apply; RELEASE label_merge_apply", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Plan?> InspectAsync(string survivorId, string retiredId, CancellationToken ct)
    {
        foreach (var id in new[] { survivorId, retiredId })
        {
            var identity = await _labels.ResolveIdentityAsync(id, ct).ConfigureAwait(false);
            if (identity.IsRetired) throw new RetiredLabelException(id, identity.CanonicalId!);
        }
        var survivor = await _labels.GetByIdAsync(survivorId, ct).ConfigureAwait(false);
        var retired = await _labels.GetByIdAsync(retiredId, ct).ConfigureAwait(false);
        if (survivor is null || retired is null) return null;
        var blockers = new List<string>();
        var rows = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var table in new[] { "label", "label_alias", "label_external_ref", "release_label_link" })
            rows[table] = await RowsAsync($"SELECT * FROM {table} WHERE label_id IN (@s, @r) ORDER BY {(table == "label_external_ref" ? "1, 2, 3" : "1, 2")}",
                ct, ("s", survivorId), ("r", retiredId)).ConfigureAwait(false);
        rows["retired_label"] = await RowsAsync("SELECT * FROM retired_label WHERE canonical_id IN (@s, @r) ORDER BY retired_id",
            ct, ("s", survivorId), ("r", retiredId)).ConfigureAwait(false);
        foreach (var table in new[] { "embedding_document", "embedding_work_item" })
            rows[table] = await RowsAsync($"SELECT * FROM {table} WHERE lower(entity_type) = 'label' AND entity_id IN (@s, @r) ORDER BY 1, 2, 3, 4",
                ct, ("s", survivorId), ("r", retiredId)).ConfigureAwait(false);
        if (rows["embedding_work_item"].Any(r => (string?)r["status"] == "Processing"))
            blockers.Add("Embedding work is processing for a participant. Finish or resolve that work before merging.");
        rows["provider_conflicts"] = await RowsAsync("""
            SELECT other.* FROM label_external_ref other
            WHERE other.label_id NOT IN (@s, @r) AND EXISTS (
                SELECT 1 FROM label_external_ref involved WHERE involved.label_id IN (@s, @r)
                AND involved.source = other.source AND involved.external_id = other.external_id)
            ORDER BY other.label_id, other.source, other.external_id
            """, ct, ("s", survivorId), ("r", retiredId)).ConfigureAwait(false);
        foreach (var other in rows["provider_conflicts"])
            blockers.Add($"Provider reference {other["source"]}/{other["external_id"]} also belongs to label {other["label_id"]}.");
        await InspectUnknownDependenciesAsync(rows, blockers, survivorId, retiredId, ct).ConfigureAwait(false);

        if (survivor.Aliases.Count(a => a.IsPrimary) > 1 || retired.Aliases.Count(a => a.IsPrimary) > 1)
            blockers.Add("A participant has multiple primary aliases. Review aliases before merging.");
        var aliases = survivor.Aliases.Select(a => a with { NormalizedValue = EntityNameNormalizer.NormalizeStrict(a.Value) })
            .ToDictionary(a => a.Value, StringComparer.Ordinal);
        foreach (var alias in retired.Aliases.Concat(string.IsNullOrWhiteSpace(retired.Name) ? [] : new[] { new EntityAlias { Value = retired.Name } }))
            aliases.TryAdd(alias.Value, alias with { IsPrimary = false, NormalizedValue = EntityNameNormalizer.NormalizeStrict(alias.Value) });

        var references = new List<EntityReference>();
        foreach (var provider in survivor.ExternalReferences.Concat(retired.ExternalReferences).Select(r => r.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var kept = survivor.ExternalReferences.Where(r => r.Source == provider).ToArray();
            var moved = retired.ExternalReferences.Where(r => r.Source == provider).ToArray();
            if (kept.Count(r => r.IsPrimary) > 1 || moved.Count(r => r.IsPrimary) > 1)
                blockers.Add($"Provider {provider} has ambiguous primary references.");
            var primaryId = kept.FirstOrDefault(r => r.IsPrimary)?.ExternalId ?? moved.FirstOrDefault(r => r.IsPrimary)?.ExternalId;
            foreach (var group in kept.Concat(moved).GroupBy(r => r.ExternalId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var payloads = group.Select(r => r.PayloadJson).Where(p => p is not null).Distinct(StringComparer.Ordinal).ToArray();
                if (payloads.Length > 1) blockers.Add($"Provider reference {provider}/{group.Key} has conflicting payload observations.");
                references.Add(group.First() with
                {
                    IsPrimary = group.Key == primaryId, PayloadJson = payloads.FirstOrDefault(),
                    LastSeenUtc = group.Max(r => r.LastSeenUtc),
                });
            }
        }
        var links = new List<MergeReleaseLink>();
        foreach (var group in rows["release_label_link"].GroupBy(r => (string)r["release_id"]!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var roles = group.Select(r => (string?)r["role"]).Where(r => r is not null).Distinct(StringComparer.Ordinal).ToArray();
            if (roles.Length > 1) blockers.Add($"Release {group.Key} has incompatible label roles.");
            links.Add(new(group.Key, group.Any(r => Convert.ToInt64(r["is_primary"]) != 0), roles.FirstOrDefault()));
        }
        return new(survivor, retired, survivor with
        {
            NormalizedName = EntityNameNormalizer.NormalizeStrict(survivor.Name),
            Aliases = aliases.Values.OrderBy(a => a.Value, StringComparer.Ordinal).ToArray(),
            ExternalReferences = references,
        }, links.ToArray(), rows, blockers.ToArray());
    }

    private async Task InspectUnknownDependenciesAsync(Dictionary<string, List<Dictionary<string, object?>>> rows,
        List<string> blockers, string survivorId, string retiredId, CancellationToken ct)
    {
        var tables = await RowsAsync("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name", ct).ConfigureAwait(false);
        foreach (var tableRow in tables)
        {
            var table = (string)tableRow["name"]!;
            if (table is "label_alias" or "label_external_ref" or "release_label_link" or "retired_label") continue;
            var foreignKeys = await RowsAsync($"PRAGMA foreign_key_list({Quote(table)})", ct).ConfigureAwait(false);
            foreach (var fk in foreignKeys.Where(f => (string?)f["table"] == "label"))
            {
                var column = (string)fk["from"]!;
                var dependencies = await RowsAsync($"SELECT * FROM {Quote(table)} WHERE {Quote(column)} IN (@s, @r) ORDER BY 1",
                    ct, ("s", survivorId), ("r", retiredId)).ConfigureAwait(false);
                if (dependencies.Count == 0) continue;
                rows[$"unsupported:{table}:{column}"] = dependencies;
                blockers.Add($"Unsupported label dependency ({dependencies.Count} rows). This storage schema needs a merge policy before applying.");
            }
        }
    }

    private async Task<List<Dictionary<string, object?>>> RowsAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
        return command;
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidOperationException("Persisted merge operation is invalid.");
    private static MergeLabelSummary Summary(Label label) => new(label.Id, label.Name, label.SortName, label.Version);
}
