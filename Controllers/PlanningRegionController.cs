using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/planning-control")]
[Authorize]
public sealed class PlanningRegionController(TmsDbContext db) : ControllerBase
{
    [HttpGet("regions")]
    public async Task<IActionResult> Regions([FromQuery] DateOnly date, CancellationToken ct)
    {
        await SitePlanningProfileStore.SyncOrderProfilesAsync(db, date, ct);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => (x.EntityType == "order" || x.EntityType == "register:order") && x.Status != StagingStatus.Rejected)
            .OrderByDescending(x => x.ReceivedAtUtc).Take(8000).ToListAsync(ct);

        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                if (DateOnly.TryParse(Text(root, "collectionDate"), out var collectionDate) && collectionDate != date) continue;
                var pallets = int.TryParse(Text(root, "pallets", "palletQty", "palletQuantity", "quantity"), out var parsed) ? parsed : 0;
                if (pallets <= 0) continue;
                var destination = Text(root, "deliveryLocation", "deliverySite", "delivery", "destination", "depot", "stallNumber");
                if (!string.IsNullOrWhiteSpace(destination)) destinations.Add(destination);
            }
            catch (JsonException) { }
        }

        var map = await SitePlanningProfileStore.ResolveRegionsAsync(db, destinations, ct);
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["North"] = 0,
            ["Midlands"] = 1,
            ["East"] = 2,
            ["London"] = 3,
            ["South East"] = 4,
            ["South West"] = 5,
            ["West / Wales"] = 6,
            ["Other"] = 7
        };
        var ordered = destinations.OrderBy(x => rank.TryGetValue(map.GetValueOrDefault(x, "Other"), out var value) ? value : 99)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return Ok(new { date, destinations = ordered, destinationRegions = map });
    }

    private static string? Text(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            foreach (var property in root.EnumerateObject())
                if (Normalise(property.Name) == Normalise(name))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? property.Value.ToString() : null;
        return null;
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
