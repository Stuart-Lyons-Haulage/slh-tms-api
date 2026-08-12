using Xunit;

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
    }
}
