using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class LoadCommercialStore
{
    private const string EntityType = "loadcommercial";

    public static async Task EnrichAsync(TmsDbContext db, IEnumerable<Load> loads, CancellationToken ct)
    {
        var rows = loads.ToList();
        if (rows.Count == 0) return;
        var keys = rows.Select(load => Key(load.Id)).ToList();
        var stored = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == EntityType && keys.Contains(item.IdempotencyKey))
            .ToDictionaryAsync(item => item.IdempotencyKey, item => item.PayloadJson, ct);
        foreach (var load in rows)
        {
            if (!stored.TryGetValue(Key(load.Id), out var json)) continue;
            try
            {
                var values = JsonSerializer.Deserialize<LoadCommercialValues>(json, JsonOptions);
                if (values is not null) Apply(load, values);
            }
            catch (JsonException) { /* Leave malformed legacy control data blank and editable. */ }
        }
    }

    public static async Task SaveAsync(TmsDbContext db, Load load, LoadCommercialValues values, string? reviewedBy, CancellationToken ct)
    {
        var key = Key(load.Id);
        var stored = await db.StagedImports.SingleOrDefaultAsync(item => item.IdempotencyKey == key, ct);
        if (stored is null)
        {
            stored = new StagedImport { EntityType = EntityType, IdempotencyKey = key, PayloadJson = "{}", Source = "SLH commercial control" };
            db.StagedImports.Add(stored);
        }
        stored.PayloadJson = JsonSerializer.Serialize(values, JsonOptions);
        stored.Status = StagingStatus.Promoted;
        stored.ReviewedAtUtc = DateTimeOffset.UtcNow;
        stored.ReviewedBy = reviewedBy;
        stored.ReviewNote = "Commercial control updated from the Planner.";
        Apply(load, values);
        await PlanningAllocationStore.SyncSingleOrderRunAsync(db, load, reviewedBy, ct);
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(Load load, LoadCommercialValues values)
    {
        load.RevenueAmount = values.RevenueAmount;
        load.FuelSurchargeAmount = values.FuelSurchargeAmount;
        load.EstimatedCostAmount = values.EstimatedCostAmount;
        load.ActualCostAmount = values.ActualCostAmount;
        load.EstimatedDistanceMiles = values.EstimatedDistanceMiles;
        load.EmptyMiles = values.EmptyMiles;
        load.InvoiceStatus = values.InvoiceStatus;
        load.CommercialNotes = values.CommercialNotes;
        load.PalletSpacesUsed = values.PalletSpacesUsed;
        load.TotalPalletSpaces = values.TotalPalletSpaces;
        load.CapacityType = values.CapacityType;
        load.DepotSplits = values.DepotSplits;
        load.TemperatureC = values.TemperatureC;
        load.PlannerNotes = values.PlannerNotes;
    }

    private static string Key(Guid loadId) => $"loadcommercial:{loadId:N}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record LoadCommercialValues(decimal? RevenueAmount, decimal? FuelSurchargeAmount, decimal? EstimatedCostAmount, decimal? ActualCostAmount,
    decimal? EstimatedDistanceMiles, decimal? EmptyMiles, string? InvoiceStatus, string? CommercialNotes, decimal? PalletSpacesUsed = null,
    decimal? TotalPalletSpaces = null, string? CapacityType = null, string? DepotSplits = null, decimal? TemperatureC = null, string? PlannerNotes = null);
