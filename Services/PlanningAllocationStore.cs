using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class PlanningAllocationStore
{
    public const string EntityType = "planningpalletallocation";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<bool> SyncSingleOrderRunAsync(TmsDbContext db, Load load, string? reviewedBy, CancellationToken ct)
    {
        var capacityType = (load.CapacityType ?? string.Empty).Trim();
        if (!capacityType.Equals("Standard pallets", StringComparison.OrdinalIgnoreCase) &&
            !capacityType.Equals("Euro pallets", StringComparison.OrdinalIgnoreCase))
            return false;

        if (load.PalletSpacesUsed is null || load.PalletSpacesUsed < 0 || decimal.Truncate(load.PalletSpacesUsed.Value) != load.PalletSpacesUsed.Value)
            return false;

        var orderIds = load.Stops
            .Where(stop => stop.OrderId is not null)
            .Select(stop => stop.OrderId!.Value)
            .Distinct()
            .ToList();

        if (orderIds.Count == 0)
        {
            try
            {
                orderIds = await db.LoadStops.AsNoTracking()
                    .Where(stop => stop.LoadId == load.Id && stop.OrderId != null)
                    .Select(stop => stop.OrderId!.Value)
                    .Distinct()
                    .Take(2)
                    .ToListAsync(ct);
            }
            catch (Exception ex) when (SchemaUnavailable(ex))
            {
                db.ChangeTracker.Clear();
            }
        }

        if (orderIds.Count != 1) return false;

        var quantity = decimal.ToInt32(load.PalletSpacesUsed.Value);
        var now = DateTimeOffset.UtcNow;
        var payload = new AllocationState(orderIds[0], load.Id, quantity, load.PlanningDate, now, reviewedBy);
        db.StagedImports.Add(new StagedImport
        {
            EntityType = EntityType,
            IdempotencyKey = $"palletallocation:auto:{orderIds[0]:N}:{load.Id:N}:{now:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            Source = "SLH run quantity sync",
            Status = StagingStatus.Promoted,
            ReviewedAtUtc = now,
            ReviewedBy = reviewedBy,
            ReviewNote = $"Single-order run quantity synchronised automatically at {quantity} pallet{(quantity == 1 ? string.Empty : "s")}."
        });
        return true;
    }

    private static bool SchemaUnavailable(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record AllocationState(Guid OrderId, Guid LoadId, int Pallets, DateOnly Date, DateTimeOffset UpdatedAtUtc, string? UpdatedBy);
}
