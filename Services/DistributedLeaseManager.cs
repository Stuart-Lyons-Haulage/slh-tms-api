using System.Data;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class IntegrationLeaseNames
{
    public const string TachoMaster = "integration:tachomaster";
    public const string Fleetio = "integration:fleetio";
    public const string SageHr = "integration:sagehr";
}

/// <summary>
/// SQL-backed lease used to serialise cross-replica integration writers. Expiry provides
/// crash recovery; release is owner-qualified so an expired/reacquired lease cannot be
/// accidentally removed by the previous owner finishing late.
/// </summary>
public sealed class DistributedLeaseManager(TmsDbContext db, ILogger<DistributedLeaseManager> logger)
{
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<DistributedLeaseHandle?> TryAcquireAsync(string leaseId, TimeSpan duration, CancellationToken ct)
    {
        Validate(leaseId, duration);
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;
DECLARE @now datetime2(7) = SYSUTCDATETIME();
DECLARE @expires datetime2(7) = DATEADD(SECOND, @leaseSeconds, @now);
DECLARE @acquired bit = 0;

UPDATE dbo.DistributedLease WITH (UPDLOCK, HOLDLOCK)
SET AcquiredAt = @now,
    ExpiresAt = @expires,
    InstanceId = @instanceId
WHERE LeaseId = @leaseId
  AND (ExpiresAt <= @now OR InstanceId = @instanceId);

IF @@ROWCOUNT = 1
BEGIN
    SET @acquired = 1;
END
ELSE IF NOT EXISTS (SELECT 1 FROM dbo.DistributedLease WITH (UPDLOCK, HOLDLOCK) WHERE LeaseId = @leaseId)
BEGIN
    INSERT dbo.DistributedLease (LeaseId, AcquiredAt, ExpiresAt, InstanceId)
    VALUES (@leaseId, @now, @expires, @instanceId);
    SET @acquired = 1;
END;

COMMIT TRANSACTION;
SELECT @acquired;
""";
            AddParameter(command, "@leaseId", leaseId);
            AddParameter(command, "@instanceId", _instanceId);
            AddParameter(command, "@leaseSeconds", checked((int)Math.Ceiling(duration.TotalSeconds)));
            var result = await command.ExecuteScalarAsync(ct);
            var acquired = result is not null && result != DBNull.Value && Convert.ToBoolean(result);
            if (!acquired)
            {
                logger.LogInformation("DistributedLeaseBusy LeaseId={LeaseId} InstanceId={InstanceId}", leaseId, _instanceId);
                return null;
            }

            logger.LogInformation("DistributedLeaseAcquired LeaseId={LeaseId} InstanceId={InstanceId} DurationSeconds={DurationSeconds}",
                leaseId, _instanceId, duration.TotalSeconds);
            return new DistributedLeaseHandle(this, leaseId, _instanceId);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    internal async Task ReleaseAsync(string leaseId, string instanceId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM dbo.DistributedLease WHERE LeaseId = @leaseId AND InstanceId = @instanceId;";
            AddParameter(command, "@leaseId", leaseId);
            AddParameter(command, "@instanceId", instanceId);
            var released = await command.ExecuteNonQueryAsync(ct);
            logger.LogInformation("DistributedLeaseReleased LeaseId={LeaseId} InstanceId={InstanceId} ReleasedRows={ReleasedRows}",
                leaseId, instanceId, released);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    internal static void Validate(string leaseId, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(leaseId)) throw new ArgumentException("LeaseId is required.", nameof(leaseId));
        if (leaseId.Length > 160) throw new ArgumentOutOfRangeException(nameof(leaseId), "LeaseId cannot exceed 160 characters.");
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(6))
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be greater than zero and no more than six hours.");
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed class DistributedLeaseHandle : IAsyncDisposable
{
    private readonly DistributedLeaseManager _manager;
    private readonly string _leaseId;
    private readonly string _instanceId;
    private int _released;

    internal DistributedLeaseHandle(DistributedLeaseManager manager, string leaseId, string instanceId)
    {
        _manager = manager;
        _leaseId = leaseId;
        _instanceId = instanceId;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        try { await _manager.ReleaseAsync(_leaseId, _instanceId, CancellationToken.None); }
        catch { /* Expiry still guarantees recovery if release cannot reach SQL. */ }
    }
}
