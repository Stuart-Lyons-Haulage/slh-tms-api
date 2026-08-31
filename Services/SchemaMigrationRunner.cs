using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public sealed record SchemaMigrationDefinition(
    int Version,
    string Name,
    string ResourceName,
    string Sql,
    string Checksum);

internal sealed record AppliedSchemaMigration(int Version, string Name, string Checksum);

public sealed class SchemaMigrationException : Exception
{
    public SchemaMigrationException(int version, string name, string message, Exception innerException)
        : base(message, innerException)
    {
        Version = version;
        MigrationName = name;
    }

    public int Version { get; }
    public string MigrationName { get; }
}

/// <summary>
/// Applies the embedded SQL schema migrations exactly once and records their
/// immutable SHA256 checksums in dbo.SchemaMigration. The migration catalogue is
/// append-only: existing entries must never be reordered, renamed or edited after
/// they have been applied to an environment.
/// </summary>
public static class SchemaMigrationRunner
{
    private const string ResourcePrefix = "Slh.Tms.Api.Database.";
    private const string MigrationLockResource = "SLH.TMS.SchemaMigration";

    // These are the 43 SQL resources that existed when versioned schema history
    // was introduced. Their legacy filename prefixes are retained as names only;
    // the authoritative migration version is the 1-based position in this list.
    // Future migrations must be appended to the end of this catalogue.
    private static readonly string[] OrderedMigrationFiles =
    [
        "000_Critical_Master_Site_Compatibility.sql",
        "000_Operational_Storage_Recovery.sql",
        "001_Initial_Tms_Schema.sql",
        "002_Planning_Loads_Schema.sql",
        "003_Market_Order_Details.sql",
        "004_Driver_Mobile_Number.sql",
        "005_Delivery_Windows.sql",
        "006_Customer_Contacts.sql",
        "007_Market_Contact_Salesman.sql",
        "008_Customer_Contacts_Repair.sql",
        "009_Market_Contact_Sender.sql",
        "010_Fuel_Prices.sql",
        "011_Master_Data_Column_Repair.sql",
        "012_Fleetio_Vehicle_Columns.sql",
        "013_Vehicle_Fuel_Card_Details.sql",
        "014_Master_Data_Table_Repair.sql",
        "015_Driver_Existing_Table_Repair.sql",
        "016_Master_Data_Existing_Table_Repair.sql",
        "017_Master_Data_Column_Repair_Retry.sql",
        "018_Fuel_Price_Table_Repair.sql",
        "019_Planning_Order_Table_Repair.sql",
        "019b_Master_Data_Audit.sql",
        "020_Master_Workbook_Detail_Columns.sql",
        "021_Fleetio_Compliance_Fields.sql",
        "022_Safe_Master_Field_Repair.sql",
        "023_Planning_Table_Complete_Repair.sql",
        "024_Integration_Mappings.sql",
        "025_Market_Stall_Backfill.sql",
        "026_Operations_Intelligence_Schema_Repair.sql",
        "027_Integration_Mappings_Repair.sql",
        "027b_Night_Out_Loads_Repair.sql",
        "028_Geofence_Runtime_Integrity_Repair.sql",
        "028_Geofence_Tables_Repair.sql",
        "029_Geofence_Site_Link_Repair.sql",
        "030_Geofence_Maintenance_Compatibility.sql",
        "031_Order_Import_Audit_History.sql",
        "032_Order_Movement_Source_Lines.sql",
        "033_Intake_Mapping_Governance.sql",
        "034_Order_Completeness_References.sql",
        "035_Planning_Optimiser.sql",
        "036_Order_Review_Schema_Repair.sql",
        "037_Driver_Tacho_Identity.sql",
        "038_Driver_Tacho_Identity_Repair.sql"
    ];

    internal const string HistoryTableSql = """
        IF OBJECT_ID(N'dbo.SchemaMigration', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.SchemaMigration
            (
                Version int NOT NULL,
                Name nvarchar(260) NOT NULL,
                AppliedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_SchemaMigration_AppliedAtUtc DEFAULT (SYSUTCDATETIME()),
                Checksum nvarchar(64) NOT NULL,
                CONSTRAINT PK_SchemaMigration PRIMARY KEY (Version)
            );
        END;
        """;

    public static IReadOnlyList<SchemaMigrationDefinition> GetMigrations()
    {
        var assembly = typeof(SchemaMigrationRunner).Assembly;
        var embeddedFiles = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name => name[ResourcePrefix.Length..])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var expectedFiles = OrderedMigrationFiles.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!embeddedFiles.SequenceEqual(expectedFiles, StringComparer.Ordinal))
        {
            var missing = OrderedMigrationFiles.Except(embeddedFiles, StringComparer.Ordinal).ToArray();
            var unexpected = embeddedFiles.Except(OrderedMigrationFiles, StringComparer.Ordinal).ToArray();
            throw new InvalidOperationException(
                $"Embedded schema migration catalogue mismatch. Missing: [{string.Join(", ", missing)}]. Unexpected/unversioned: [{string.Join(", ", unexpected)}]. " +
                "Every embedded Database/*.sql resource must have one immutable sequential entry in SchemaMigrationRunner.OrderedMigrationFiles.");
        }

        var migrations = new List<SchemaMigrationDefinition>(OrderedMigrationFiles.Length);
        for (var index = 0; index < OrderedMigrationFiles.Length; index++)
        {
            var fileName = OrderedMigrationFiles[index];
            var resourceName = ResourcePrefix + fileName;
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Schema migration resource {resourceName} was not found.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var checksum = Convert.ToHexString(SHA256.HashData(bytes));
            var sql = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            migrations.Add(new SchemaMigrationDefinition(index + 1, fileName, resourceName, sql, checksum));
        }

        return migrations;
    }

    public static async Task ApplyAsync(TmsDbContext db, ILogger logger, CancellationToken ct)
    {
        IReadOnlyList<SchemaMigrationDefinition> migrations;
        try
        {
            migrations = GetMigrations();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Schema migration catalogue is invalid. Application startup will stop.");
            throw;
        }

        var openedHere = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (openedHere)
            await db.Database.OpenConnectionAsync(ct);

        try
        {
            var connection = db.Database.GetDbConnection();
            await AcquireMigrationLockAsync(connection, ct);
            try
            {
                await db.Database.ExecuteSqlRawAsync(HistoryTableSql, ct);
                var applied = await ReadAppliedMigrationsAsync(connection, ct);
                ValidateAppliedHistory(migrations, applied);

                foreach (var migration in migrations)
                {
                    if (applied.ContainsKey(migration.Version))
                    {
                        logger.LogDebug(
                            "Schema migration {Version} {MigrationName} already applied with checksum {Checksum}.",
                            migration.Version, migration.Name, migration.Checksum);
                        continue;
                    }

                    logger.LogInformation(
                        "Applying required schema migration {Version} {MigrationName} ({Checksum}).",
                        migration.Version, migration.Name, migration.Checksum);

                    await ApplySingleMigrationAsync(db, migration, logger, ct);
                }

                logger.LogInformation(
                    "Schema migration check complete. Database is at version {SchemaVersion} with {MigrationCount} migration(s) registered.",
                    migrations.Count, migrations.Count);
            }
            finally
            {
                await ReleaseMigrationLockAsync(connection, logger, ct);
            }
        }
        catch (SchemaMigrationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Schema migration initialisation failed. Application startup will stop.");
            throw;
        }
        finally
        {
            if (openedHere)
                await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task ApplySingleMigrationAsync(
        TmsDbContext db,
        SchemaMigrationDefinition migration,
        ILogger logger,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(migration.Sql, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO dbo.SchemaMigration (Version, Name, AppliedAtUtc, Checksum)
                VALUES ({migration.Version}, {migration.Name}, SYSUTCDATETIME(), {migration.Checksum});
                """, ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Applied schema migration {Version} {MigrationName} successfully.",
                migration.Version, migration.Name);
        }
        catch (Exception ex)
        {
            try
            {
                await transaction.RollbackAsync(ct);
            }
            catch (Exception rollbackException)
            {
                logger.LogError(
                    rollbackException,
                    "Rollback also failed for schema migration {Version} {MigrationName}.",
                    migration.Version, migration.Name);
            }

            logger.LogCritical(
                ex,
                "Required schema migration {Version} {MigrationName} failed. Checksum {Checksum}. Application startup will stop. Reason: {FailureReason}",
                migration.Version, migration.Name, migration.Checksum, ex.GetBaseException().Message);

            throw new SchemaMigrationException(
                migration.Version,
                migration.Name,
                $"Required schema migration {migration.Version} ({migration.Name}) failed; application startup cannot continue.",
                ex);
        }
    }

    private static async Task<Dictionary<int, AppliedSchemaMigration>> ReadAppliedMigrationsAsync(
        DbConnection connection,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Name, Checksum FROM dbo.SchemaMigration ORDER BY Version;";
        var applied = new Dictionary<int, AppliedSchemaMigration>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var migration = new AppliedSchemaMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
            applied.Add(migration.Version, migration);
        }

        return applied;
    }

    internal static void ValidateAppliedHistory(
        IReadOnlyList<SchemaMigrationDefinition> migrations,
        IReadOnlyDictionary<int, AppliedSchemaMigration> applied)
    {
        if (applied.Count == 0) return;

        var migrationByVersion = migrations.ToDictionary(migration => migration.Version);
        var highestAppliedVersion = applied.Keys.Max();

        for (var version = 1; version <= highestAppliedVersion; version++)
        {
            if (!applied.ContainsKey(version))
                throw new InvalidOperationException(
                    $"SchemaMigration history has a gap at version {version}. Refusing to apply migrations out of order.");
        }

        foreach (var history in applied.Values.OrderBy(item => item.Version))
        {
            if (!migrationByVersion.TryGetValue(history.Version, out var migration))
                throw new InvalidOperationException(
                    $"Database contains schema migration version {history.Version} ({history.Name}) which is unknown to this application build. " +
                    "The database is newer than, or incompatible with, this build.");

            if (!string.Equals(history.Name, migration.Name, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Schema migration version {history.Version} name mismatch. Database has '{history.Name}', application expects '{migration.Name}'.");

            if (!string.Equals(history.Checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Schema migration version {history.Version} ({history.Name}) checksum mismatch. " +
                    $"Database has {history.Checksum}, application expects {migration.Checksum}. Applied migrations are immutable.");
        }
    }

    private static async Task AcquireMigrationLockAsync(DbConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = N'{MigrationLockResource}',
                @LockMode = N'Exclusive',
                @LockOwner = N'Session',
                @LockTimeout = 60000;
            SELECT @result;
            """;
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        if (result < 0)
            throw new InvalidOperationException($"Could not acquire SQL schema migration lock '{MigrationLockResource}'. Result: {result}.");
    }

    private static async Task ReleaseMigrationLockAsync(DbConnection connection, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                EXEC sys.sp_releaseapplock
                    @Resource = N'{MigrationLockResource}',
                    @LockOwner = N'Session';
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release SQL schema migration lock {MigrationLockResource}.", MigrationLockResource);
        }
    }
}
