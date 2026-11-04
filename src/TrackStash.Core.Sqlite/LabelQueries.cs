using Microsoft.Data.Sqlite;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Core.Sqlite;

internal sealed partial class SqliteLabelRepository
{
    public async ValueTask<LabelPage> QueryAsync(LabelQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        _connection.CreateFunction<string?, string>("trackstash_normalize", EntityNameNormalizer.NormalizeStrict, isDeterministic: true);
        const string filter = """
            WHERE (@search IS NULL OR instr(trackstash_normalize(l.name), @search) > 0
                OR EXISTS (SELECT 1 FROM label_alias a WHERE a.label_id = l.label_id
                    AND instr(trackstash_normalize(a.value), @search) > 0))
              AND (@normalizedName IS NULL OR l.normalized_name = @normalizedName)
              AND (@source IS NULL OR EXISTS (SELECT 1 FROM label_external_ref r
                    WHERE r.label_id = l.label_id AND r.source = @source
                      AND (@externalId IS NULL OR r.external_id = @externalId)))
            """;
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.Parameters.AddWithValue("@search", query.Search is null ? DBNull.Value : EntityNameNormalizer.NormalizeStrict(query.Search));
        cmd.Parameters.AddWithValue("@normalizedName", (object?)query.NormalizedName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@source", (object?)query.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@externalId", (object?)query.ExternalId ?? DBNull.Value);
        cmd.CommandText = $"SELECT COUNT(*) FROM label l {filter}";
        var total = checked((int)(long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
        var column = query.SortBy switch
        {
            LabelSortField.Name => "l.name",
            LabelSortField.SortName => "l.sort_name",
            LabelSortField.Id => "l.label_id",
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };
        var direction = query.Descending ? "DESC" : "ASC";
        cmd.CommandText = $"""
            SELECT l.label_id, l.name, l.sort_name, l.version FROM label l
            {filter}
            ORDER BY {column} {direction}, l.label_id ASC
            LIMIT @limit OFFSET @offset
            """;
        cmd.Parameters.AddWithValue("@limit", query.PageSize);
        cmd.Parameters.AddWithValue("@offset", ((long)query.Page - 1) * query.PageSize);
        var items = new List<LabelListItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(new LabelListItem(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
        return new LabelPage(items, total, query.Page, query.PageSize);
    }

    public async ValueTask<LabelUpdateResult> UpdateAsync(string labelId, string expectedVersion, LabelUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        ArgumentNullException.ThrowIfNull(update);
        update.Validate();
        await RejectRetiredAsync(labelId, cancellationToken).ConfigureAwait(false);
        var before = update.SetProfile ? await ReadProfileAsync(labelId, cancellationToken).ConfigureAwait(false) : null;
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            UPDATE label SET
                name = CASE WHEN @setName THEN @name ELSE name END,
                normalized_name = CASE WHEN @setName THEN @normalizedName ELSE normalized_name END,
                sort_name = CASE WHEN @setSortName THEN @sortName ELSE sort_name END,
                updated_utc = @updated
            WHERE label_id = @id AND version = @version
            """;
        cmd.Parameters.AddWithValue("@id", labelId);
        cmd.Parameters.AddWithValue("@version", expectedVersion);
        cmd.Parameters.AddWithValue("@setName", update.SetName);
        cmd.Parameters.AddWithValue("@name", (object?)update.Name?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@normalizedName", EntityNameNormalizer.NormalizeStrict(update.Name));
        cmd.Parameters.AddWithValue("@setSortName", update.SetSortName);
        cmd.Parameters.AddWithValue("@sortName", (object?)update.SortName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
        using var savepoint = _connection.CreateCommand();
        savepoint.Transaction = _transaction;
        savepoint.CommandText = "SAVEPOINT label_update";
        await savepoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 1 && update.SetProfile)
            {
                await WriteProfileAsync(labelId, update.Profile!, cancellationToken).ConfigureAwait(false);
                await RecordProfileRevisionAsync(labelId, before!, update.Profile!, cancellationToken).ConfigureAwait(false);
            }
            var label = await GetByIdAsync(labelId, cancellationToken).ConfigureAwait(false);
            savepoint.CommandText = "RELEASE label_update";
            await savepoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return affected == 1
                ? new LabelUpdateResult(LabelUpdateStatus.Updated, label)
                : new LabelUpdateResult(label is null ? LabelUpdateStatus.NotFound : LabelUpdateStatus.Stale);
        }
        catch
        {
            savepoint.CommandText = "ROLLBACK TO label_update; RELEASE label_update";
            await savepoint.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
