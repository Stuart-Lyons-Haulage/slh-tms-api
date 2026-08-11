using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class PlanningSchemaInitializer
{
    public static async Task Apply(TmsDbContext db, CancellationToken ct)
    {
        var assembly = typeof(PlanningSchemaInitializer).Assembly;
        await using var stream = assembly.GetManifestResourceStream("Slh.Tms.Api.Database.002_Planning_Loads_Schema.sql")
            ?? throw new InvalidOperationException("Planning schema script was not found.");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync(ct);
        await db.Database.ExecuteSqlRawAsync(script, ct);
    }
}
