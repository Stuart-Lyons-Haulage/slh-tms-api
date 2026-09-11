using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Slh.Tms.MasterDataSync;

public sealed class SqlMasterDataRepository(IOptions<SyncOptions> options, ILogger<SqlMasterDataRepository> logger)
{
    private readonly string connectionString = options.Value.SqlConnectionString;

    public async Task<SyncSummary> UpsertListAsync(MasterListDefinition definition, IReadOnlyList<SharePointItem> items, Func<SharePointItem, Exception, Task> deadLetter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("MasterDataSync SQL connection is not configured.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var summary = new MutableSummary(items.Count);

        foreach (var item in items)
        {
            try
            {
                var values = ToSqlValues(definition, item);
                var wasExisting = await ExistsAsync(connection, transaction, definition, values[definition.AnchorColumn]!, ct);
                await UpsertAsync(connection, transaction, definition, values, ct);
                if (wasExisting) summary.Updated++; else summary.Added++;
            }
            catch (Exception ex)
            {
                summary.Failed++;
                try
                {
                    await deadLetter(item, ex);
                }
                catch (Exception deadLetterException)
                {
                    logger.LogError(deadLetterException, "Could not dead-letter {ListName} item {ItemId}.", definition.ListName, item.Id);
                }
            }
        }

        if (summary.Failed == 0)
            summary.Deactivated = await DeactivateMissingAsync(connection, transaction, definition, items.Select(x => x.Id).ToArray(), ct);

        await transaction.CommitAsync(ct);
        return summary.ToImmutable();
    }

    private static Dictionary<string, object?> ToSqlValues(MasterListDefinition definition, SharePointItem item)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.Fields)
        {
            var value = field.SharePointNames
                .Select(name => item.Fields.TryGetValue(name, out var candidate) ? candidate : null)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            values[field.SqlColumn] = ConvertValue(field.SqlColumn, value);
        }

        if (values[definition.AnchorColumn] is not string anchor || string.IsNullOrWhiteSpace(anchor))
            throw new FormatException($"{definition.ListName} item {item.Id} has no {definition.AnchorColumn}.");
        if (values["IsActive"] is null) values["IsActive"] = true;
        values["SharePointItemId"] = item.Id;
        values["LastSyncedAt"] = DateTime.UtcNow;
        return values;
    }

    private static async Task<bool> ExistsAsync(SqlConnection connection, SqlTransaction tx, MasterListDefinition definition, object anchor, CancellationToken ct)
    {
        await using var command = new SqlCommand($"SELECT COUNT(1) FROM dbo.[{definition.TableName}] WHERE [{definition.AnchorColumn}] = @Anchor", connection, tx);
        command.Parameters.AddWithValue("@Anchor", anchor);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task UpsertAsync(SqlConnection connection, SqlTransaction tx, MasterListDefinition definition, Dictionary<string, object?> values, CancellationToken ct)
    {
        var columns = definition.Fields.Select(x => x.SqlColumn).Where(x => x != "IsActive").ToArray();
        var assignments = columns.Where(x => x != definition.AnchorColumn)
            .Select(x => $"[{x}] = @{x}")
            .Append("[IsActive] = @IsActive")
            .Append("[SharePointItemId] = @SharePointItemId")
            .Append("[LastSyncedAt] = @LastSyncedAt")
            .Append("[UpdatedAt] = SYSUTCDATETIME()");
        var insertColumns = columns.Append("SharePointItemId").Append("LastSyncedAt").Append("IsActive");
        var insertValues = columns.Select(x => "@" + x).Append("@SharePointItemId").Append("@LastSyncedAt").Append("@IsActive");
        var sql = $@"
IF EXISTS (SELECT 1 FROM dbo.[{definition.TableName}] WHERE [{definition.AnchorColumn}] = @{definition.AnchorColumn})
BEGIN
    UPDATE dbo.[{definition.TableName}] SET {string.Join(", ", assignments)}
    WHERE [{definition.AnchorColumn}] = @{definition.AnchorColumn};
END
ELSE
BEGIN
    INSERT dbo.[{definition.TableName}] ({string.Join(", ", insertColumns.Select(x => "[" + x + "]"))}, [CreatedAt], [UpdatedAt])
    VALUES ({string.Join(", ", insertValues)}, SYSUTCDATETIME(), SYSUTCDATETIME());
END";
        await using var command = new SqlCommand(sql, connection, tx) { CommandTimeout = 60 };
        foreach (var pair in values)
            command.Parameters.AddWithValue("@" + pair.Key, pair.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> DeactivateMissingAsync(SqlConnection connection, SqlTransaction tx, MasterListDefinition definition, int[] seenIds, CancellationToken ct)
    {
        var parameters = seenIds.Select((_, i) => "@p" + i).ToArray();
        var predicate = seenIds.Length == 0 ? "1 = 1" : $"[SharePointItemId] NOT IN ({string.Join(", ", parameters)})";
        var sql = $@"UPDATE dbo.[{definition.TableName}]
                     SET [IsActive] = 0, [UpdatedAt] = SYSUTCDATETIME(), [LastSyncedAt] = SYSUTCDATETIME()
                     WHERE {predicate} AND [IsActive] = 1;";
        await using var command = new SqlCommand(sql, connection, tx);
        for (var i = 0; i < seenIds.Length; i++) command.Parameters.AddWithValue(parameters[i], seenIds[i]);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static object? ConvertValue(string column, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (column is "Latitude" or "Longitude")
            return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var coordinate)
                ? coordinate : throw new FormatException($"{column} is not numeric.");
        if (column is "GeofenceRadiusMetres" or "StandardCapacity" or "EuroCapacity")
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                ? integer : throw new FormatException($"{column} is not an integer.");
        if (column is "DvsCompliant" or "IsActive" or "IsPricingMaximum")
            return bool.TryParse(value, out var boolean)
                ? boolean : throw new FormatException($"{column} is not true/false.");
        if (column.EndsWith("Expiry", StringComparison.OrdinalIgnoreCase) || column == "WeekCommencing")
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? date.Date : throw new FormatException($"{column} is not a valid date.");
        if (column is "OpenTime" or "CloseTime")
            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time)
                ? time : throw new FormatException($"{column} is not a valid time.");
        return value;
    }

    private sealed class MutableSummary(int read)
    {
        public int Read { get; } = read;
        public int Added;
        public int Updated;
        public int Deactivated;
        public int Failed;
        public SyncSummary ToImmutable() => new(Read, Added, Updated, Deactivated, Failed);
    }
}
