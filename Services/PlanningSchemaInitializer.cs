using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class PlanningSchemaInitializer
{
    public static async Task Apply(TmsDbContext db, CancellationToken ct)
    {
        var assembly = typeof(PlanningSchemaInitializer).Assembly;
        foreach (var resourceName in new[] { "Slh.Tms.Api.Database.002_Planning_Loads_Schema.sql", "Slh.Tms.Api.Database.003_Market_Order_Details.sql", "Slh.Tms.Api.Database.004_Driver_Mobile_Number.sql" })
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException($"Schema script {resourceName} was not found.");
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(ct), ct);
        }
    }
}
