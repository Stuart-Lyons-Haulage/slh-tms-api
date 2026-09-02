using Xunit;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Tests;

public sealed class SchemaResourceTests
{
    [Fact]
    public void All_database_repair_scripts_are_embedded()
    {
        var resources = typeof(Program).Assembly.GetManifestResourceNames();

        Assert.Contains("Slh.Tms.Api.Database.007_Market_Contact_Salesman.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.008_Customer_Contacts_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.009_Market_Contact_Sender.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.015_Driver_Existing_Table_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.023_Planning_Table_Complete_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.024_Integration_Mappings.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.027_Integration_Mappings_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.031_Order_Import_Audit_History.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.032_Order_Movement_Source_Lines.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.036_Order_Review_Schema_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.037_Driver_Tacho_Identity.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.038_Driver_Tacho_Identity_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.039_Canonical_Relational_Planning.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.000_Operational_Storage_Recovery.sql", resources);
    }

    [Fact]
    public void Schema_migration_catalog_versions_every_embedded_database_script_once()
    {
        var resources = typeof(Program).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Slh.Tms.Api.Database.") && name.EndsWith(".sql"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var migrations = SchemaMigrationRunner.GetMigrations();

        Assert.Equal(resources.Length, migrations.Count);
        Assert.Equal(44, migrations.Count);
        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(migration => migration.Version));
        Assert.Equal(
            resources,
            migrations.Select(migration => migration.ResourceName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(migrations, migration => Assert.Matches("^[0-9A-F]{64}$", migration.Checksum));
        Assert.Equal("037_Driver_Tacho_Identity.sql", migrations[^3].Name);
        Assert.Equal("038_Driver_Tacho_Identity_Repair.sql", migrations[^2].Name);
        Assert.Equal("039_Canonical_Relational_Planning.sql", migrations[^1].Name);
    }

    [Fact]
    public void Schema_history_table_has_required_version_name_timestamp_and_checksum_columns()
    {
        Assert.Contains("CREATE TABLE dbo.SchemaMigration", SchemaMigrationRunner.HistoryTableSql);
        Assert.Contains("Version int NOT NULL", SchemaMigrationRunner.HistoryTableSql);
        Assert.Contains("Name nvarchar", SchemaMigrationRunner.HistoryTableSql);
        Assert.Contains("AppliedAtUtc datetime2", SchemaMigrationRunner.HistoryTableSql);
        Assert.Contains("Checksum nvarchar", SchemaMigrationRunner.HistoryTableSql);
        Assert.Contains("PRIMARY KEY (Version)", SchemaMigrationRunner.HistoryTableSql);
    }

    [Fact]
    public void Applied_migration_checksum_drift_is_rejected()
    {
        var migration = SchemaMigrationRunner.GetMigrations()[0];
        var history = new Dictionary<int, AppliedSchemaMigration>
        {
            [migration.Version] = new(migration.Version, migration.Name, new string('0', 64))
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SchemaMigrationRunner.ValidateAppliedHistory([migration], history));

        Assert.Contains("checksum mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Applied_migration_history_gaps_are_rejected()
    {
        var migrations = SchemaMigrationRunner.GetMigrations().Take(3).ToArray();
        var history = new Dictionary<int, AppliedSchemaMigration>
        {
            [1] = new(1, migrations[0].Name, migrations[0].Checksum),
            [3] = new(3, migrations[2].Name, migrations[2].Checksum)
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SchemaMigrationRunner.ValidateAppliedHistory(migrations, history));

        Assert.Contains("gap at version 2", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_integration_mapping_repair_covers_partial_tables()
    {
        Assert.Contains("Provider", IntegrationMappingSchemaRepair.RepairSql);
        Assert.Contains("ExternalKey", IntegrationMappingSchemaRepair.RepairSql);
        Assert.Contains("TmsEntityType", IntegrationMappingSchemaRepair.RepairSql);
        Assert.Contains("TmsEntityId", IntegrationMappingSchemaRepair.RepairSql);
        Assert.Contains("IX_IntegrationMappings_Provider_ExternalKey_Type", IntegrationMappingSchemaRepair.RepairSql);
    }

    [Fact]
    public void Fleetio_mapping_fallback_message_does_not_make_tms_sole_authority()
    {
        Assert.DoesNotContain("TMS master remains authoritative", FleetioResilientSyncController.MappingUnavailableWarning);
        Assert.Contains("Fleetio-supplied identity, status and compliance fields were applied", FleetioResilientSyncController.MappingUnavailableWarning);
    }
}
