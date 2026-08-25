using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Shared production-safe planning readers. Production can contain current runs in both
/// the planning register and the legacy/live Loads table, so reads merge both sources.
/// Live-table rows win when the same load id exists in both stores.
/// </summary>
internal static class PlanningResilience
{
    public static async Task<List<Load>> ReadLoadsAsync(TmsDbContext db, DateOnly? date, CancellationToken ct)
    {
        var merged = new Dictionary<Guid, Load>();

        try
        {
            var registered = await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);
            foreach (var load in registered) merged[load.Id] = load;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        try
        {
            var query = db.Loads.AsNoTracking().Include(x => x.Stops).AsQueryable();
            if (date is not null) query = query.Where(x => x.PlanningDate == date.Value);
            var live = await query.OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).Take(2000).ToListAsync(ct);
            foreach (var load in live) merged[load.Id] = load;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        return merged.Values
            .OrderBy(x => x.PlanningDate)
            .ThenBy(x => x.Reference)
            .Take(2000)
            .ToList();
    }

    public static async Task<Load?> ReadLoadAsync(TmsDbContext db, Guid id, CancellationToken ct)
    {
        try
        {
            var registered = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
            if (registered is not null) return registered;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        try
        {
            return await db.Loads.AsNoTracking().Include(x => x.Stops).SingleOrDefaultAsync(x => x.Id == id, ct);
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            return null;
        }
    }

    public static bool SchemaUnavailable(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        return ex is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
