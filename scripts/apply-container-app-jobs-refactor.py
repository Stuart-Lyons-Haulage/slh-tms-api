from pathlib import Path


def replace_exact(path: str, old: str, new: str, count: int = 1) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    found = text.count(old)
    if found != count:
        raise SystemExit(f"{path}: expected {count} occurrence(s), found {found}: {old[:120]!r}")
    p.write_text(text.replace(old, new), encoding="utf-8")


def write(path: str, content: str) -> None:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(content, encoding="utf-8")


# API project must not compile the nested console project's top-level Program.cs.
replace_exact(
    "Slh.Tms.Api.csproj",
    '    <Compile Remove="Slh.Tms.Api.Tests/**/*.cs" />\n',
    '    <Compile Remove="Slh.Tms.Api.Tests/**/*.cs" />\n    <Compile Remove="Slh.Tms.Jobs/**/*.cs" />\n',
)

# Register the SQL lease service and remove process-owned scheduled integration timers.
replace_exact(
    "Program.cs",
    "builder.Services.AddScoped<AzureSmsDispatchService>();\nbuilder.Services.AddScoped<IntegrationSyncCoordinator>();",
    "builder.Services.AddScoped<AzureSmsDispatchService>();\nbuilder.Services.AddScoped<DistributedLeaseManager>();\nbuilder.Services.AddScoped<IntegrationSyncCoordinator>();",
)
replace_exact("Program.cs", "builder.Services.AddHostedService<IntegrationBackgroundSyncService>();\n", "")
replace_exact("Program.cs", "builder.Services.AddHostedService<TachoCanonicalDriverMasterDailyBackgroundService>();\n", "")

# Integration coordinator: replace static in-process gates with SQL-backed leases.
p = Path("Services/IntegrationSyncCoordinator.cs")
text = p.read_text(encoding="utf-8")
text = text.replace(
    "    FleetioClient fleetioClient,\n    ILogger<IntegrationSyncCoordinator> logger)",
    "    FleetioClient fleetioClient,\n    DistributedLeaseManager leases,\n    ILogger<IntegrationSyncCoordinator> logger)",
)
text = text.replace("    private static readonly SemaphoreSlim FleetioSyncGate = new(1, 1);\n\n", "")
old = '''    public async Task<IntegrationSyncResult> SyncTachoMasterAsync(string actor, CancellationToken ct)\n    {\n        await TachoMasterSyncGate.WaitAsync(ct);\n        try\n        {\n            return await SyncTachoMasterCoreAsync(actor, ct);\n        }\n        finally\n        {\n            TachoMasterSyncGate.Release();\n        }\n    }'''
new = '''    public async Task<IntegrationSyncResult> SyncTachoMasterAsync(string actor, CancellationToken ct)\n    {\n        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.TachoMaster, TimeSpan.FromMinutes(30), ct);\n        if (lease is null)\n            return new("TachoMaster", false, DateTimeOffset.UtcNow, "TachoMaster sync skipped because another distributed writer currently holds the integration lease.");\n        return await SyncTachoMasterCoreAsync(actor, ct);\n    }'''
if text.count(old) != 1:
    raise SystemExit("IntegrationSyncCoordinator: Tacho wrapper anchor mismatch")
text = text.replace(old, new)
old = "    public async Task<IntegrationSyncResult> SyncSageHrAsync(string actor, CancellationToken ct)\n    {\n"
new = '''    public async Task<IntegrationSyncResult> SyncSageHrAsync(string actor, CancellationToken ct)\n    {\n        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.SageHr, TimeSpan.FromMinutes(30), ct);\n        if (lease is null)\n            return new("Sage HR", false, DateTimeOffset.UtcNow, "Sage HR sync skipped because another distributed writer currently holds the integration lease.");\n        return await SyncSageHrCoreAsync(actor, ct);\n    }\n\n    internal async Task<IntegrationSyncResult> SyncSageHrCoreAsync(string actor, CancellationToken ct)\n    {\n'''
if text.count(old) != 1:
    raise SystemExit("IntegrationSyncCoordinator: Sage wrapper anchor mismatch")
text = text.replace(old, new)
old = '''    public async Task<IntegrationSyncResult> SyncFleetioAsync(string actor, CancellationToken ct)\n    {\n        if (!fleetioClient.IsConfigured)\n            return new("Fleetio", false, DateTimeOffset.UtcNow, $"Fleetio is not configured: {string.Join(", ", fleetioClient.MissingSettings)}.");\n\n        await FleetioSyncGate.WaitAsync(ct);\n        try\n        {\n            var assets = await fleetioClient.GetVehiclesAsync(100, ct);'''
new = '''    public async Task<IntegrationSyncResult> SyncFleetioAsync(string actor, CancellationToken ct)\n    {\n        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.Fleetio, TimeSpan.FromMinutes(45), ct);\n        if (lease is null)\n            return new("Fleetio", false, DateTimeOffset.UtcNow, "Fleetio sync skipped because another distributed writer currently holds the integration lease.");\n        return await SyncFleetioCoreAsync(actor, ct);\n    }\n\n    internal async Task<IntegrationSyncResult> SyncFleetioCoreAsync(string actor, CancellationToken ct)\n    {\n        if (!fleetioClient.IsConfigured)\n            return new("Fleetio", false, DateTimeOffset.UtcNow, $"Fleetio is not configured: {string.Join(", ", fleetioClient.MissingSettings)}.");\n\n        var assets = await fleetioClient.GetVehiclesAsync(100, ct);'''
if text.count(old) != 1:
    raise SystemExit("IntegrationSyncCoordinator: Fleetio opening anchor mismatch")
text = text.replace(old, new)
old = '''            return new("Fleetio", true, now,\n                $"Fleetio canonical sync: {createdVehicles} vehicle(s) created, {updatedVehicles} updated, {createdTrailers} trailer(s) created, {updatedTrailers} updated, {mergedTrailerAliases} trailer alias(es) consolidated, {quarantinedVehicles} TMS-only vehicle(s) and {quarantinedTrailers} TMS-only trailer(s) quarantined, {correctedVehicleMappings} stale vehicle mapping(s) repaired, {duplicateVehicleSourceRows} duplicate source registration row(s) resolved against canonical vehicles. Trailer capacities were retained from TMS.", changed);\n        }\n        finally\n        {\n            FleetioSyncGate.Release();\n        }\n    }'''
new = '''        return new("Fleetio", true, now,\n            $"Fleetio canonical sync: {createdVehicles} vehicle(s) created, {updatedVehicles} updated, {createdTrailers} trailer(s) created, {updatedTrailers} updated, {mergedTrailerAliases} trailer alias(es) consolidated, {quarantinedVehicles} TMS-only vehicle(s) and {quarantinedTrailers} TMS-only trailer(s) quarantined, {correctedVehicleMappings} stale vehicle mapping(s) repaired, {duplicateVehicleSourceRows} duplicate source registration row(s) resolved against canonical vehicles. Trailer capacities were retained from TMS.", changed);\n    }'''
if text.count(old) != 1:
    raise SystemExit("IntegrationSyncCoordinator: Fleetio closing anchor mismatch")
text = text.replace(old, new)
# Scheduled loop moves out of API process entirely.
marker = "\npublic sealed class IntegrationBackgroundSyncService("
if text.count(marker) != 1:
    raise SystemExit("IntegrationSyncCoordinator: background scheduler marker mismatch")
text = text.split(marker, 1)[0].rstrip() + "\n"
p.write_text(text, encoding="utf-8")

# Canonical Tacho orchestration now uses the same SQL-backed provider lease; daily timer moves to ACA Job.
p = Path("Services/TachoCanonicalDriverMasterOrchestrator.cs")
text = p.read_text(encoding="utf-8")
text = text.replace(
    "    DriverMasterClassificationService classification,\n    ILogger<TachoCanonicalDriverMasterOrchestrator> logger)",
    "    DriverMasterClassificationService classification,\n    DistributedLeaseManager leases,\n    ILogger<TachoCanonicalDriverMasterOrchestrator> logger)",
)
old = '''    public async Task<TachoCanonicalOrchestrationResult> RunAsync(string actor, CancellationToken ct)\n    {\n        await TachoMasterSyncGate.WaitAsync(ct);\n        try\n        {\n            return await RunCoreAsync(actor, ct);\n        }\n        finally\n        {\n            TachoMasterSyncGate.Release();\n        }\n    }'''
new = '''    public async Task<TachoCanonicalOrchestrationResult> RunAsync(string actor, CancellationToken ct)\n    {\n        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.TachoMaster, TimeSpan.FromMinutes(60), ct);\n        if (lease is null)\n        {\n            var now = DateTimeOffset.UtcNow;\n            var canonicalResult = new TachoDriverMasterSyncResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,\n                "Canonical TachoMaster sync skipped because another distributed writer currently holds the integration lease.", now);\n            var enrichment = new IntegrationSyncResult("TachoMaster", false, now, canonicalResult.Message);\n            return new TachoCanonicalOrchestrationResult(false, canonicalResult, enrichment, now, canonicalResult.Message);\n        }\n        return await RunCoreAsync(actor, ct);\n    }'''
if text.count(old) != 1:
    raise SystemExit("TachoCanonicalDriverMasterOrchestrator: gate wrapper mismatch")
text = text.replace(old, new)
marker = "\n/// <summary>\n/// Runs the full canonical Driver Master once per day at 04:30 Europe/London."
if text.count(marker) != 1:
    raise SystemExit("TachoCanonicalDriverMasterOrchestrator: daily background marker mismatch")
text = text.split(marker, 1)[0].rstrip() + "\n"
p.write_text(text, encoding="utf-8")

# Direct canonical sync calls also use the distributed Tacho provider lease.
p = Path("Services/TachoDriverMasterSyncService.cs")
text = p.read_text(encoding="utf-8")
text = text.replace(
    "    TachoMasterOptions options,\n    ILogger<TachoDriverMasterSyncService> logger)",
    "    TachoMasterOptions options,\n    DistributedLeaseManager leases,\n    ILogger<TachoDriverMasterSyncService> logger)",
)
old = '''    public async Task<TachoDriverMasterSyncResult> SyncAsync(string actor, CancellationToken ct)\n    {\n        await TachoMasterSyncGate.WaitAsync(ct);\n        try\n        {\n            return await SyncCoreAsync(actor, ct);\n        }\n        finally\n        {\n            TachoMasterSyncGate.Release();\n        }\n    }'''
new = '''    public async Task<TachoDriverMasterSyncResult> SyncAsync(string actor, CancellationToken ct)\n    {\n        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.TachoMaster, TimeSpan.FromMinutes(60), ct);\n        if (lease is null)\n            return new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,\n                "TachoMaster Driver Master sync skipped because another distributed writer currently holds the integration lease.", DateTimeOffset.UtcNow);\n        return await SyncCoreAsync(actor, ct);\n    }'''
if text.count(old) != 1:
    raise SystemExit("TachoDriverMasterSyncService: gate wrapper mismatch")
p.write_text(text.replace(old, new), encoding="utf-8")

# Remove the obsolete process-local gate source file.
Path("Services/TachoMasterSyncGate.cs").unlink()

write("Database/040_Distributed_Integration_Lease.sql", r'''IF OBJECT_ID(N'dbo.DistributedLease', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DistributedLease
    (
        LeaseId nvarchar(160) NOT NULL CONSTRAINT PK_DistributedLease PRIMARY KEY,
        AcquiredAt datetime2(7) NOT NULL,
        ExpiresAt datetime2(7) NOT NULL,
        InstanceId nvarchar(240) NOT NULL
    );
    CREATE INDEX IX_DistributedLease_ExpiresAt ON dbo.DistributedLease(ExpiresAt);
END;
''')

write("Services/DistributedLeaseManager.cs", r'''using System.Data;
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
''')

write("Slh.Tms.Jobs/Slh.Tms.Jobs.csproj", r'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="../Slh.Tms.Api.csproj" />
  </ItemGroup>
</Project>
''')

write("Slh.Tms.Jobs/ScheduledJobRunner.cs", r'''using Slh.Tms.Api.Services;

namespace Slh.Tms.Jobs;

public sealed record JobExecutionResult(bool Success, string Message, int Changed = 0);

public sealed class ScheduledJobRunner(DistributedLeaseManager leases, ILogger<ScheduledJobRunner> logger)
{
    public async Task<int> RunAsync(
        string jobName,
        string leaseId,
        TimeSpan leaseDuration,
        Func<CancellationToken, Task<JobExecutionResult>> action,
        CancellationToken ct)
    {
        var instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";
        var started = DateTimeOffset.UtcNow;
        await using var lease = await leases.TryAcquireAsync(leaseId, leaseDuration, ct);
        if (lease is null)
        {
            logger.LogInformation("ScheduledJobSkippedLeaseHeld JobName={JobName} LeaseId={LeaseId} InstanceId={InstanceId}", jobName, leaseId, instanceId);
            return 0;
        }

        logger.LogInformation("ScheduledJobStarted JobName={JobName} LeaseId={LeaseId} InstanceId={InstanceId} StartedAtUtc={StartedAtUtc}",
            jobName, leaseId, instanceId, started);
        try
        {
            var result = await action(ct);
            if (!result.Success) throw new InvalidOperationException(result.Message);
            logger.LogInformation(
                "ScheduledJobCompleted JobName={JobName} LeaseId={LeaseId} InstanceId={InstanceId} StartedAtUtc={StartedAtUtc} CompletedAtUtc={CompletedAtUtc} Changed={Changed} Message={Message}",
                jobName, leaseId, instanceId, started, DateTimeOffset.UtcNow, result.Changed, result.Message);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("ScheduledJobCancelled JobName={JobName} LeaseId={LeaseId} InstanceId={InstanceId}", jobName, leaseId, instanceId);
            return 2;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "ScheduledJobFailed JobName={JobName} LeaseId={LeaseId} InstanceId={InstanceId} StartedAtUtc={StartedAtUtc} FailedAtUtc={FailedAtUtc}",
                jobName, leaseId, instanceId, started, DateTimeOffset.UtcNow);
            return 1;
        }
    }
}
''')

write("Slh.Tms.Jobs/TachoMasterScheduledJob.cs", r'''using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Jobs;

public sealed class TachoMasterScheduledJob(
    TmsDbContext db,
    IntegrationSyncCoordinator integration,
    TachoCanonicalDriverMasterOrchestrator canonical,
    ILogger<TachoMasterScheduledJob> logger)
{
    public async Task<JobExecutionResult> RunAsync(CancellationToken ct)
    {
        if (await CanonicalDueAsync(ct))
        {
            logger.LogInformation("TachoMasterCanonicalDue LocalSchedule=04:30 Europe/London");
            var result = await canonical.RunAsync("system:aca-job:tachomaster-canonical", ct);
            return new JobExecutionResult(result.Success, result.Message, result.Canonical.Created + result.Canonical.Updated + result.Canonical.DuplicateRecordsRetired);
        }

        var sync = await integration.SyncTachoMasterAsync("system:aca-job:tachomaster", ct);
        return new JobExecutionResult(sync.Success, sync.Message, sync.Changed);
    }

    private async Task<bool> CanonicalDueAsync(CancellationToken ct)
    {
        var zone = LondonTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        if (localNow.TimeOfDay < new TimeSpan(4, 30, 0)) return false;

        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var startLocal = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var endLocal = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone), TimeSpan.Zero);
        var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone), TimeSpan.Zero);

        return !await db.StagedImports.AsNoTracking().AnyAsync(row =>
            row.EntityType == "tachodrivermasterorchestration" &&
            row.Status == StagingStatus.Promoted &&
            row.ReviewedAtUtc >= startUtc &&
            row.ReviewedAtUtc < endUtc, ct);
    }

    private static TimeZoneInfo LondonTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}
''')

write("Slh.Tms.Jobs/EtaRecalculationJob.cs", r'''using System.Text.Json;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Jobs;

public sealed class EtaRecalculationJob(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    TmsDbContext db)
{
    public async Task<JobExecutionResult> RunAsync(CancellationToken ct)
    {
        var baseUrl = configuration["TmsApi:BaseUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("TmsApi:BaseUrl is required for the ETA recalculation job.");
        var wallboardKey = configuration["TvWallboard:AccessKey"]
            ?? throw new InvalidOperationException("TvWallboard:AccessKey is required for the ETA recalculation job.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/operations/delivery-etas");
        request.Headers.TryAddWithoutValidation(TvWallboardAccess.HeaderName, wallboardKey);
        using var response = await httpClientFactory.CreateClient("eta-job").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ETA endpoint response did not contain a records array.");

        var samples = new List<EtaSnapshotCaptureItem>();
        foreach (var record in records.EnumerateArray())
        {
            if (!TryGuid(record, "loadId", out var loadId) || !TryGuid(record, "stopId", out var stopId)) continue;
            samples.Add(new EtaSnapshotCaptureItem(
                loadId,
                stopId,
                null,
                ReadDate(record, "etaUtc"),
                ReadString(record, "source") ?? "Unavailable",
                ReadString(record, "risk") ?? "Pending",
                ReadString(record, "tachoStatus") ?? "Unavailable",
                ReadInt(record, "breakDelayMinutes"),
                ReadDate(record, "trackingUpdatedAtUtc")));
        }

        var added = await ManagementReportingStore.CaptureAsync(db, samples, ct);
        return new JobExecutionResult(true, $"Recalculated {samples.Count} ETA record(s) and persisted {added} precision snapshot(s).", added);
    }

    private static bool TryGuid(JsonElement element, string property, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String && Guid.TryParse(node.GetString(), out value);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.TryGetInt32(out var value) ? value : 0;

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(node.GetString(), out var value) ? value : null;
}
''')

write("Slh.Tms.Jobs/Program.cs", r'''using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Slh.Tms.Jobs;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;

var connectionString = configuration.GetConnectionString("TmsDb")
    ?? throw new InvalidOperationException("ConnectionStrings:TmsDb is required.");
builder.Services.AddDbContext<TmsDbContext>(options => options.UseSqlServer(connectionString));

var dot = new DotTrackingOptions();
configuration.GetSection("Tracking:Dot").Bind(dot);
builder.Services.AddSingleton(dot);

var tacho = new TachoMasterOptions();
configuration.GetSection("Integrations:TachoMaster").Bind(tacho);
tacho.Enabled = ReadBool(configuration, tacho.Enabled, "Integrations:TachoMaster:Enabled", "Integrations__TachoMaster__Enabled", "tachomaster-enabled", "tacho-enabled", "TachoMaster--Enabled");
tacho.BaseUrl = ReadSetting(configuration, tacho.BaseUrl, "Integrations:TachoMaster:BaseUrl", "Integrations__TachoMaster__BaseUrl", "tachomaster-base-url", "tacho-base-url", "TachoMaster--BaseUrl");
tacho.ApiKey = ReadSetting(configuration, tacho.ApiKey, "Integrations:TachoMaster:ApiKey", "Integrations__TachoMaster__ApiKey", "tachomaster-api-key", "tacho-api-key", "TachoMaster--ApiKey");
tacho.Username = ReadSetting(configuration, tacho.Username, "Integrations:TachoMaster:Username", "Integrations__TachoMaster__Username", "tachomaster-username", "tacho-username", "TachoMaster--Username");
tacho.Password = ReadSetting(configuration, tacho.Password, "Integrations:TachoMaster:Password", "Integrations__TachoMaster__Password", "tachomaster-password", "tacho-password", "TachoMaster--Password");
if (string.IsNullOrWhiteSpace(tacho.ApiKey) && string.IsNullOrWhiteSpace(tacho.Username) && string.IsNullOrWhiteSpace(tacho.Password) && dot.IsConfigured)
{
    tacho.Enabled = true;
    tacho.BaseUrl = dot.BaseUrl;
    tacho.ApiKey = dot.ApiKey;
    tacho.Username = dot.Username;
    tacho.Password = dot.Password;
    tacho.UsesSharedRoadTechCredentials = true;
}
builder.Services.AddSingleton(tacho);

var sage = new SageHrOptions();
configuration.GetSection("Integrations:SageHr").Bind(sage);
builder.Services.AddSingleton(sage);

var fleetio = new FleetioOptions();
configuration.GetSection("Integrations:Fleetio").Bind(fleetio);
fleetio.Enabled = ReadBool(configuration, fleetio.Enabled, "Integrations:Fleetio:Enabled", "Integrations__Fleetio__Enabled", "fleetio-enabled", "Fleetio--Enabled");
fleetio.BaseUrl = ReadSetting(configuration, fleetio.BaseUrl, "Integrations:Fleetio:BaseUrl", "Integrations__Fleetio__BaseUrl", "fleetio-base-url", "Fleetio--BaseUrl");
fleetio.ApiKey = ReadSetting(configuration, fleetio.ApiKey, "Integrations:Fleetio:ApiKey", "Integrations__Fleetio__ApiKey", "fleetio-api-key", "Fleetio--ApiKey");
fleetio.AccountToken = ReadSetting(configuration, fleetio.AccountToken, "Integrations:Fleetio:AccountToken", "Integrations__Fleetio__AccountToken", "fleetio-account-token", "Fleetio--AccountToken");
fleetio.ApiVersion = ReadSetting(configuration, fleetio.ApiVersion, "Integrations:Fleetio:ApiVersion", "Integrations__Fleetio__ApiVersion", "fleetio-api-version", "Fleetio--ApiVersion");
if (fleetio.BaseUrl.EndsWith("/api/v2", StringComparison.OrdinalIgnoreCase)) fleetio.BaseUrl = fleetio.BaseUrl[..^1] + "1";
builder.Services.AddSingleton(fleetio);

builder.Services.AddTransient<TachoMasterRetryHandler>();
builder.Services.AddHttpClient<DotTrackingClient>();
builder.Services.AddHttpClient<TachoMasterClient>().AddHttpMessageHandler<TachoMasterRetryHandler>();
builder.Services.AddHttpClient<SageHrClient>();
builder.Services.AddHttpClient<FleetioClient>();
builder.Services.AddHttpClient("eta-job");
builder.Services.AddScoped<DistributedLeaseManager>();
builder.Services.AddScoped<IntegrationSyncCoordinator>();
builder.Services.AddScoped<TachoDriverMasterSyncService>();
builder.Services.AddScoped<DriverMasterClassificationService>();
builder.Services.AddScoped<TachoCanonicalDriverMasterOrchestrator>();
builder.Services.AddScoped<TachoMasterScheduledJob>();
builder.Services.AddScoped<EtaRecalculationJob>();
builder.Services.AddScoped<ScheduledJobRunner>();

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var services = scope.ServiceProvider;
var runner = services.GetRequiredService<ScheduledJobRunner>();
var jobKind = (configuration["TMS_JOB_KIND"] ?? args.FirstOrDefault() ?? string.Empty).Trim().ToLowerInvariant();
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };

var exitCode = jobKind switch
{
    "tachomaster" => await runner.RunAsync("TachoMaster", "job:tachomaster", TimeSpan.FromMinutes(70),
        services.GetRequiredService<TachoMasterScheduledJob>().RunAsync, shutdown.Token),
    "fleetio" => await runner.RunAsync("Fleetio", "job:fleetio", TimeSpan.FromMinutes(55), async ct =>
    {
        var result = await services.GetRequiredService<IntegrationSyncCoordinator>().SyncFleetioAsync("system:aca-job:fleetio", ct);
        return new JobExecutionResult(result.Success, result.Message, result.Changed);
    }, shutdown.Token),
    "sagehr" => await runner.RunAsync("SageHR", "job:sagehr", TimeSpan.FromMinutes(45), async ct =>
    {
        var result = await services.GetRequiredService<IntegrationSyncCoordinator>().SyncSageHrAsync("system:aca-job:sagehr", ct);
        return new JobExecutionResult(result.Success, result.Message, result.Changed);
    }, shutdown.Token),
    "eta" => await runner.RunAsync("ETARecalculation", "job:eta-recalculation", TimeSpan.FromMinutes(10),
        services.GetRequiredService<EtaRecalculationJob>().RunAsync, shutdown.Token),
    _ => throw new InvalidOperationException($"Unsupported TMS_JOB_KIND '{jobKind}'. Expected tachomaster, fleetio, sagehr or eta.")
};

Environment.ExitCode = exitCode;

static string ReadSetting(IConfiguration configuration, string fallback, params string[] keys) =>
    keys.Select(key => configuration[key]).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? fallback;

static bool ReadBool(IConfiguration configuration, bool fallback, params string[] keys) =>
    bool.TryParse(ReadSetting(configuration, fallback.ToString(), keys), out var value) ? value : fallback;
''')

write("Slh.Tms.Jobs/Dockerfile", r'''FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Slh.Tms.Api.csproj ./
COPY Slh.Tms.Jobs/Slh.Tms.Jobs.csproj Slh.Tms.Jobs/
RUN dotnet restore Slh.Tms.Jobs/Slh.Tms.Jobs.csproj
COPY . .
RUN dotnet publish Slh.Tms.Jobs/Slh.Tms.Jobs.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Slh.Tms.Jobs.dll"]
''')

write("infra/container-app-jobs.bicep", r'''@description('Azure Container Apps managed environment resource ID')
param containerAppsEnvironmentId string
@description('User-assigned identity resource ID used for ACR and Key Vault')
param jobIdentityResourceId string
@description('ACR server, for example slhtmsacrprod.azurecr.io')
param registryServer string
@description('Immutable Jobs image, for example slhtmsacrprod.azurecr.io/slh-tms-jobs:<sha>')
param jobImage string
@description('Key Vault secret URI for SQL connection string')
param tmsDbSecretUri string
@description('Key Vault secret URI for RoadTech/Tacho base URL')
param dotBaseUrlSecretUri string
@description('Key Vault secret URI for RoadTech/Tacho API key')
param dotApiKeySecretUri string
@description('Key Vault secret URI for RoadTech company code')
param dotCompanyCodeSecretUri string
@description('Key Vault secret URI for RoadTech username')
param dotUsernameSecretUri string
@description('Key Vault secret URI for RoadTech password')
param dotPasswordSecretUri string
@description('Key Vault secret URI for Sage HR base URL')
param sageBaseUrlSecretUri string
@description('Key Vault secret URI for Sage HR API key')
param sageApiKeySecretUri string
@description('Key Vault secret URI for Fleetio base URL')
param fleetioBaseUrlSecretUri string
@description('Key Vault secret URI for Fleetio API key')
param fleetioApiKeySecretUri string
@description('Key Vault secret URI for Fleetio account token')
param fleetioAccountTokenSecretUri string
@description('Key Vault secret URI for TV wallboard key used by ETA calculation endpoint')
param tvWallboardKeySecretUri string
@description('Production API base URL')
param tmsApiBaseUrl string = 'https://slh-tms-api-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io'

var jobs = [
  { name: 'slh-tms-job-tachomaster', kind: 'tachomaster', cron: '*/5 * * * *', timeout: 4200 }
  { name: 'slh-tms-job-fleetio', kind: 'fleetio', cron: '5 * * * *', timeout: 3300 }
  { name: 'slh-tms-job-sagehr', kind: 'sagehr', cron: '30 5 * * *', timeout: 2700 }
  { name: 'slh-tms-job-eta', kind: 'eta', cron: '*/5 * * * *', timeout: 600 }
]

resource scheduledJobs 'Microsoft.App/jobs@2024-03-01' = [for job in jobs: {
  name: job.name
  location: resourceGroup().location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${jobIdentityResourceId}': {} }
  }
  properties: {
    environmentId: containerAppsEnvironmentId
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: job.timeout
      replicaRetryLimit: 1
      scheduleTriggerConfig: {
        cronExpression: job.cron
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        { server: registryServer, identity: jobIdentityResourceId }
      ]
      secrets: [
        { name: 'tms-db', keyVaultUrl: tmsDbSecretUri, identity: jobIdentityResourceId }
        { name: 'dot-base-url', keyVaultUrl: dotBaseUrlSecretUri, identity: jobIdentityResourceId }
        { name: 'dot-api-key', keyVaultUrl: dotApiKeySecretUri, identity: jobIdentityResourceId }
        { name: 'dot-company-code', keyVaultUrl: dotCompanyCodeSecretUri, identity: jobIdentityResourceId }
        { name: 'dot-username', keyVaultUrl: dotUsernameSecretUri, identity: jobIdentityResourceId }
        { name: 'dot-password', keyVaultUrl: dotPasswordSecretUri, identity: jobIdentityResourceId }
        { name: 'sage-base-url', keyVaultUrl: sageBaseUrlSecretUri, identity: jobIdentityResourceId }
        { name: 'sage-api-key', keyVaultUrl: sageApiKeySecretUri, identity: jobIdentityResourceId }
        { name: 'fleetio-base-url', keyVaultUrl: fleetioBaseUrlSecretUri, identity: jobIdentityResourceId }
        { name: 'fleetio-api-key', keyVaultUrl: fleetioApiKeySecretUri, identity: jobIdentityResourceId }
        { name: 'fleetio-account-token', keyVaultUrl: fleetioAccountTokenSecretUri, identity: jobIdentityResourceId }
        { name: 'tv-wallboard-key', keyVaultUrl: tvWallboardKeySecretUri, identity: jobIdentityResourceId }
      ]
    }
    template: {
      containers: [
        {
          name: 'tms-job'
          image: jobImage
          env: [
            { name: 'TMS_JOB_KIND', value: job.kind }
            { name: 'ConnectionStrings__TmsDb', secretRef: 'tms-db' }
            { name: 'Tracking__Dot__Enabled', value: 'true' }
            { name: 'Tracking__Dot__BaseUrl', secretRef: 'dot-base-url' }
            { name: 'Tracking__Dot__ApiKey', secretRef: 'dot-api-key' }
            { name: 'Tracking__Dot__CompanyCode', secretRef: 'dot-company-code' }
            { name: 'Tracking__Dot__Username', secretRef: 'dot-username' }
            { name: 'Tracking__Dot__Password', secretRef: 'dot-password' }
            { name: 'Integrations__TachoMaster__Enabled', value: 'true' }
            { name: 'Integrations__TachoMaster__BaseUrl', secretRef: 'dot-base-url' }
            { name: 'Integrations__TachoMaster__ApiKey', secretRef: 'dot-api-key' }
            { name: 'Integrations__TachoMaster__Username', secretRef: 'dot-username' }
            { name: 'Integrations__TachoMaster__Password', secretRef: 'dot-password' }
            { name: 'Integrations__SageHr__Enabled', value: 'true' }
            { name: 'Integrations__SageHr__BaseUrl', secretRef: 'sage-base-url' }
            { name: 'Integrations__SageHr__ApiKey', secretRef: 'sage-api-key' }
            { name: 'Integrations__SageHr__DriverTeamName', value: 'Drivers' }
            { name: 'Integrations__SageHr__DriverPositionKeyword', value: 'Driver' }
            { name: 'Integrations__Fleetio__Enabled', value: 'true' }
            { name: 'Integrations__Fleetio__BaseUrl', secretRef: 'fleetio-base-url' }
            { name: 'Integrations__Fleetio__ApiKey', secretRef: 'fleetio-api-key' }
            { name: 'Integrations__Fleetio__AccountToken', secretRef: 'fleetio-account-token' }
            { name: 'TmsApi__BaseUrl', value: tmsApiBaseUrl }
            { name: 'TvWallboard__AccessKey', secretRef: 'tv-wallboard-key' }
          ]
          resources: { cpu: json('0.5'), memory: '1Gi' }
        }
      ]
    }
  }
}]
''')

write("infra/container-app-job.example.yaml", r'''# Azure Container Apps Job ARM/YAML shape. Create four resources from this template
# with TMS_JOB_KIND and schedule changed to tachomaster, fleetio, sagehr and eta.
name: slh-tms-job-fleetio
location: uksouth
type: Microsoft.App/jobs
properties:
  environmentId: /subscriptions/<sub>/resourceGroups/slh-tms-prod-rg/providers/Microsoft.App/managedEnvironments/<environment>
  configuration:
    triggerType: Schedule
    replicaTimeout: 3300
    replicaRetryLimit: 1
    scheduleTriggerConfig:
      cronExpression: '5 * * * *'
      parallelism: 1
      replicaCompletionCount: 1
  template:
    containers:
      - name: tms-job
        image: slhtmsacrprod.azurecr.io/slh-tms-jobs:<tested-sha>
        env:
          - name: TMS_JOB_KIND
            value: fleetio
          - name: ConnectionStrings__TmsDb
            secretRef: tms-db
          - name: Integrations__Fleetio__Enabled
            value: 'true'
          - name: Integrations__Fleetio__BaseUrl
            secretRef: fleetio-base-url
          - name: Integrations__Fleetio__ApiKey
            secretRef: fleetio-api-key
          - name: Integrations__Fleetio__AccountToken
            secretRef: fleetio-account-token
''')

write("docs/container-app-jobs.md", r'''# Scheduled integration jobs

The API no longer owns the TachoMaster, Fleetio or Sage HR timers. `Slh.Tms.Jobs` is a dedicated console host used by four independently scheduled Azure Container Apps Jobs:

| Job kind | Example cron (UTC) | Purpose |
| --- | --- | --- |
| `tachomaster` | `*/5 * * * *` | Five-minute Tacho identity refresh. At/after 04:30 Europe/London it runs the full canonical Driver Master once per local day. |
| `fleetio` | `5 * * * *` | Hourly canonical fleet/trailer sync. |
| `sagehr` | `30 5 * * *` | Daily Sage HR driver sync. Container Apps cron is UTC; adjust if a fixed UK wall-clock time is required across DST. |
| `eta` | `*/5 * * * *` | Recalculate delivery ETAs through the existing live ETA engine and persist precision snapshots. |

Every execution first acquires a SQL row in `dbo.DistributedLease`. The row contains `LeaseId`, `AcquiredAt`, `ExpiresAt` and `InstanceId`. Acquisition uses a serializable transaction and `UPDLOCK/HOLDLOCK`; a crashed execution becomes eligible after `ExpiresAt`. Normal completion and failure release only the row owned by that instance.

There are two lease layers by design. The outer `job:*` lease suppresses duplicate Container Apps Job executions. Integration service methods also use `integration:*` leases so a manual API sync cannot overlap the scheduled job after the process-local `SemaphoreSlim` gates are removed.

The Tacho job keeps the historical 04:30 Europe/London canonical pass without a second in-process timer: the five-minute job checks the durable orchestration ledger and runs the canonical pass only when one successful pass has not yet completed for the current London date.

Build the jobs image with:

```bash
docker build -f Slh.Tms.Jobs/Dockerfile -t slhtmsacrprod.azurecr.io/slh-tms-jobs:<sha> .
```

Deploy the four scheduled resources with `infra/container-app-jobs.bicep`. Production deployment should use an immutable image tag and ensure `Database/040_Distributed_Integration_Lease.sql` has been applied before enabling schedules.
''')

write("Slh.Tms.Api.Tests/DistributedLeaseContractTests.cs", r'''using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DistributedLeaseContractTests
{
    [Theory]
    [InlineData(IntegrationLeaseNames.TachoMaster)]
    [InlineData(IntegrationLeaseNames.Fleetio)]
    [InlineData(IntegrationLeaseNames.SageHr)]
    public void Integration_lease_names_are_valid(string leaseId)
    {
        DistributedLeaseManager.Validate(leaseId, TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Lease_rejects_invalid_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DistributedLeaseManager.Validate("job:test", TimeSpan.Zero));
    }
}
''')

print("Container Apps Jobs refactor applied.")
