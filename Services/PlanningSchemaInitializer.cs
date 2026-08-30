using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class PlanningSchemaInitializer
{
    public static IReadOnlyList<string> GetSchemaScripts() => typeof(PlanningSchemaInitializer).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Slh.Tms.Api.Database.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();

    public static async Task Apply(TmsDbContext db, ILogger logger, CancellationToken ct)
    {
        var assembly = typeof(PlanningSchemaInitializer).Assembly;
        var scripts = GetSchemaScripts();

        foreach (var resourceName in scripts)
        {
            try
            {
                await using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException($"Schema script {resourceName} was not found.");
                using var reader = new StreamReader(stream);
                await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(ct), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "TMS schema repair script {SchemaScript} failed; continuing with remaining scripts.", resourceName);
            }
        }

        // Driver identity was introduced as a guarded repair, but a SQL Server batch
        // can be partially applied if an older repair fails after its first ALTER.
        // Re-issue each DDL statement independently so one missing column cannot
        // prevent the remaining identity columns from being created.
        await EnsureDriverIdentityColumnsAsync(db, logger, ct);
    }

    private static async Task EnsureDriverIdentityColumnsAsync(TmsDbContext db, ILogger logger, CancellationToken ct)
    {
        var statements = new[]
        {
            """
            IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.Drivers', N'TachoMasterDriverId') IS NULL
                EXEC(N'ALTER TABLE dbo.Drivers ADD TachoMasterDriverId nvarchar(80) NULL');
            """,
            """
            IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.Drivers', N'TachoCardNumber') IS NULL
                EXEC(N'ALTER TABLE dbo.Drivers ADD TachoCardNumber nvarchar(80) NULL');
            """,
            """
            IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.Drivers', N'LastTachoSyncUtc') IS NULL
                EXEC(N'ALTER TABLE dbo.Drivers ADD LastTachoSyncUtc datetimeoffset(7) NULL');
            """,
            """
            IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.Drivers', N'TachoMasterDriverId') IS NOT NULL
               AND NOT EXISTS
               (
                   SELECT 1
                   FROM sys.indexes
                   WHERE name = N'IX_Drivers_TachoMasterDriverId'
                     AND object_id = OBJECT_ID(N'dbo.Drivers')
               )
                EXEC(N'CREATE INDEX IX_Drivers_TachoMasterDriverId ON dbo.Drivers(TachoMasterDriverId) WHERE TachoMasterDriverId IS NOT NULL');
            """
        };

        foreach (var statement in statements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(statement, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Driver Tacho identity schema repair statement failed.");
            }
        }
    }
}
