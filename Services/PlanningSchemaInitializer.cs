using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class PlanningSchemaInitializer
{
    public static async Task Apply(TmsDbContext db, ILogger logger, CancellationToken ct)
    {
        var assembly = typeof(PlanningSchemaInitializer).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Slh.Tms.Api.Database.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

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
    }
}
