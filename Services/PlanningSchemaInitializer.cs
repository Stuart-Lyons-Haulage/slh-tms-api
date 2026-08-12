using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class PlanningSchemaInitializer
{
    private static readonly string[] Scripts =
    [
        "Slh.Tms.Api.Database.001_Initial_Tms_Schema.sql",
        "Slh.Tms.Api.Database.002_Planning_Loads_Schema.sql",
        "Slh.Tms.Api.Database.003_Market_Order_Details.sql",
        "Slh.Tms.Api.Database.004_Driver_Mobile_Number.sql",
        "Slh.Tms.Api.Database.005_Delivery_Windows.sql",
        "Slh.Tms.Api.Database.006_Customer_Contacts.sql"
    ];

    public static async Task Apply(TmsDbContext db, ILogger logger, CancellationToken ct)
    {
        var assembly = typeof(PlanningSchemaInitializer).Assembly;
        foreach (var resourceName in Scripts)
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
    }
}
