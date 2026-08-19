using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Shared production-safe planning readers. Production planning is register-first;
/// legacy Loads/LoadStops are used only as a fallback for older environments.
/// </summary>
internal static class PlanningResilience
{
    public static async Task<List<Load>> ReadLoadsAsync(TmsDbContext db, DateOnly? date, CancellationToken ct)
    {
        try
        {
            var registered = await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);
            if (registered.Count > 0) return registered;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        try
        {
            var query = db.Loads.AsNoTracking().Include(x => x.Stops).AsQueryable();
            if (date is not null) query = query.Where(x => x.PlanningDate == date.Value);
            return await query.OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).Take(2000).ToListAsync(ct);
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            return [];
        }
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
