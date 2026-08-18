using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Corrects NWF pallet-order rows that were staged before the PO-first reference rule
/// was deployed. Only PendingReview NWF CSV rows are changed; approved/live orders are
/// deliberately left untouched so historical planning is never silently rewritten.
/// </summary>
public static class NwfPendingOrderReferenceRepair
{
    public static async Task<int> Apply(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.StagedImports
            .Where(row => row.EntityType == "order" && row.Status == StagingStatus.PendingReview)
            .OrderByDescending(row => row.ReceivedAtUtc)
            .Take(5000)
            .ToListAsync(ct);

        var repaired = 0;
        foreach (var row in rows)
        {
            JsonObject payload;
            try { payload = JsonNode.Parse(row.PayloadJson)?.AsObject() ?? new JsonObject(); }
            catch (JsonException) { continue; }

            if (!string.Equals(Text(payload, "intakeParser"), "NWF Pallet Order CSV", StringComparison.OrdinalIgnoreCase))
                continue;

            var poRef = Text(payload, "poRef") ?? Text(payload, "customerPo");
            var salesOrderId = Text(payload, "salesOrderId");
            var collection = Text(payload, "collectionSite") ?? Text(payload, "sellerName");
            var depot = Text(payload, "depotId") ?? Text(payload, "depotDescription") ?? Text(payload, "stallNumber");
            var date = Text(payload, "collectionDate");
            var pallet = Text(payload, "palletName");

            if (string.IsNullOrWhiteSpace(poRef) || string.IsNullOrWhiteSpace(salesOrderId)
                || string.IsNullOrWhiteSpace(collection) || string.IsNullOrWhiteSpace(depot)
                || string.IsNullOrWhiteSpace(date))
                continue;

            var expectedReference = Clip($"{poRef}/{salesOrderId}/{collection}/{depot}", 80);
            var currentReference = Text(payload, "poNumber");
            if (string.Equals(currentReference, expectedReference, StringComparison.OrdinalIgnoreCase))
                continue;

            payload["poNumber"] = expectedReference;
            payload["customerPo"] = poRef;

            var poToken = Normalise(poRef);
            var salesToken = Normalise(salesOrderId);
            var collectionToken = Normalise(collection);
            var depotToken = Normalise(depot);
            var palletToken = Normalise(pallet);
            payload["intakeNaturalKey"] = $"NWFCSV|{date}|PO:{poToken}|SO:{salesToken}|{collectionToken}|{depotToken}|{palletToken}";
            payload["intakeMatchKeys"] = new JsonArray(
                $"NWF|{date}|PO:{poToken}:{collectionToken}:{depotToken}",
                $"NWF|{date}|SALES:{salesToken}:{collectionToken}:{depotToken}");

            row.PayloadJson = payload.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            row.ReviewedAtUtc = DateTimeOffset.UtcNow;
            row.ReviewNote = "Pending NWF pallet-order reference corrected automatically: PO REF is now the primary TMS reference and Sales Order ID is retained for traceability.";
            repaired++;
        }

        if (repaired > 0)
            await db.SaveChangesAsync(ct);
        return repaired;
    }

    private static string? Text(JsonObject payload, string name)
    {
        var property = payload.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        if (property.Value is null) return null;
        var value = property.Value.ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
