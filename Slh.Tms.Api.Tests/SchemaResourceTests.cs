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
    }

    [Fact]
    public void Schema_initializer_runs_every_embedded_database_script()
    {
        var resources = typeof(Program).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Slh.Tms.Api.Database.") && name.EndsWith(".sql"))
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(resources, PlanningSchemaInitializer.GetSchemaScripts());
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
