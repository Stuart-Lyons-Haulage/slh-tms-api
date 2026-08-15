using Xunit;
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
}
