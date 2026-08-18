using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class PlanLockStore
{
    public const string ReasonHeader = "X-Plan-Change-Reason";

    public static async Task EnsureSchemaAsync(TmsDbContext db, CancellationToken ct)
    {
        const string sql = """
IF OBJECT_ID(N'[dbo].[PlanBaselines]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[PlanBaselines](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [PlanningDate] date NOT NULL,
        [LockedAtUtc] datetimeoffset NOT NULL,
        [LockedBy] nvarchar(200) NULL,
        [SnapshotJson] nvarchar(max) NOT NULL
    );
    CREATE UNIQUE INDEX [IX_PlanBaselines_PlanningDate] ON [dbo].[PlanBaselines]([PlanningDate]);
END;
IF OBJECT_ID(N'[dbo].[PlanChangeEvents]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[PlanChangeEvents](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [PlanningDate] date NOT NULL,
        [LoadId] uniqueidentifier NULL,
        [ChangeType] nvarchar(80) NOT NULL,
        [Reason] nvarchar(1000) NOT NULL,
        [ChangedBy] nvarchar(200) NULL,
        [ChangedAtUtc] datetimeoffset NOT NULL,
        [BeforeJson] nvarchar(max) NULL,
        [AfterJson] nvarchar(max) NULL
    );
    CREATE INDEX [IX_PlanChangeEvents_PlanningDate] ON [dbo].[PlanChangeEvents]([PlanningDate],[ChangedAtUtc]);
    CREATE INDEX [IX_PlanChangeEvents_LoadId] ON [dbo].[PlanChangeEvents]([LoadId]);
END;
""";
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    public static async Task<bool> IsLockedAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await OpenAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM dbo.PlanBaselines WHERE PlanningDate=@date";
        command.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
    }

    public static async Task LockAsync(TmsDbContext db, DateOnly date, string? user, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        List<Load> loads;
        try
        {
            loads = await db.Loads.AsNoTracking().Include(x => x.Stops)
                .Where(x => x.PlanningDate == date && x.Status != LoadStatus.Cancelled)
                .OrderBy(x => x.Reference).ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            loads = (await PlanningRegisterStore.ReadLoadsAsync(db, date, ct))
                .Where(x => x.Status != LoadStatus.Cancelled).OrderBy(x => x.Reference).ToList();
        }
        var snapshot = JsonSerializer.Serialize(loads.Select(Snapshot));
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await OpenAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
MERGE dbo.PlanBaselines AS target
USING (SELECT @date AS PlanningDate) AS source ON target.PlanningDate=source.PlanningDate
WHEN MATCHED THEN UPDATE SET LockedAtUtc=@at, LockedBy=@by, SnapshotJson=@snapshot
WHEN NOT MATCHED THEN INSERT(Id,PlanningDate,LockedAtUtc,LockedBy,SnapshotJson) VALUES(@id,@date,@at,@by,@snapshot);
""";
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("@by", (object?)user ?? DBNull.Value);
        command.Parameters.AddWithValue("@snapshot", snapshot);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<PlanLockInfo?> GetAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await OpenAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT LockedAtUtc, LockedBy, SnapshotJson FROM dbo.PlanBaselines WHERE PlanningDate=@date";
        command.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var lockedAt = reader.GetFieldValue<DateTimeOffset>(0);
        var lockedBy = reader.IsDBNull(1) ? null : reader.GetString(1);
        var snapshotJson = reader.GetString(2);
        var baselineRuns = JsonSerializer.Deserialize<List<LoadBaseline>>(snapshotJson) ?? [];
        return new PlanLockInfo(date, lockedAt, lockedBy, baselineRuns.Count);
    }

    public static async Task<IReadOnlyList<LoadBaseline>> BaselineAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await OpenAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SnapshotJson FROM dbo.PlanBaselines WHERE PlanningDate=@date";
        command.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        var result = await command.ExecuteScalarAsync(ct);
        return result is string json ? JsonSerializer.Deserialize<List<LoadBaseline>>(json) ?? [] : [];
    }

    public static async Task RecordChangeAsync(TmsDbContext db, DateOnly date, Guid? loadId, string type, string reason, string? user, object? before, object? after, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await OpenAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO dbo.PlanChangeEvents(Id,PlanningDate,LoadId,ChangeType,Reason,ChangedBy,ChangedAtUtc,BeforeJson,AfterJson)
VALUES(@id,@date,@load,@type,@reason,@by,@at,@before,@after)
""";
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@load", (object?)loadId ?? DBNull.Value);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@reason", reason[..Math.Min(reason.Length, 1000)]);
        command.Parameters.AddWithValue("@by", (object?)user ?? DBNull.Value);
        command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("@before", before is null ? DBNull.Value : JsonSerializer.Serialize(before));
        command.Parameters.AddWithValue("@after", after is null ? DBNull.Value : JsonSerializer.Serialize(after));
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<IReadOnlyList<PlanChangeEvent>> ChangesAsync(TmsDbContext db, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        var rows = new List<PlanChangeEvent>();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await OpenAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PlanningDate,LoadId,ChangeType,Reason,ChangedBy,ChangedAtUtc FROM dbo.PlanChangeEvents WHERE PlanningDate>=@from AND PlanningDate<=@to ORDER BY ChangedAtUtc";
        command.Parameters.AddWithValue("@from", from.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@to", to.ToDateTime(TimeOnly.MinValue));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new PlanChangeEvent(
            DateOnly.FromDateTime(reader.GetDateTime(0)), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        return rows;
    }

    public static LoadBaseline Snapshot(Load load) => new(load.Id, load.Reference, load.VehicleId, load.DriverId, load.TrailerId,
        load.Stops.OrderBy(x => x.Sequence).Select(x => new StopBaseline(x.OrderId, x.Sequence, x.Name, x.Address, x.PlannedArrivalUtc)).ToList());

    private static async Task OpenAsync(SqlConnection connection, CancellationToken ct)
    {
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
    }
}

public sealed record PlanLockInfo(DateOnly PlanningDate, DateTimeOffset LockedAtUtc, string? LockedBy, int BaselineRuns);
public sealed record LoadBaseline(Guid Id, string Reference, Guid? VehicleId, Guid? DriverId, Guid? TrailerId, IReadOnlyList<StopBaseline> Stops);
public sealed record StopBaseline(Guid? OrderId, int Sequence, string Name, string? Address, DateTimeOffset? PlannedArrivalUtc);
public sealed record PlanChangeEvent(DateOnly PlanningDate, Guid? LoadId, string ChangeType, string Reason, string? ChangedBy, DateTimeOffset ChangedAtUtc);
