using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Shared production-safe planning readers. Production can contain current runs in the
/// planning register, the legacy/live Loads table, and the durable planner-plan import audit.
/// Real planning/live rows always win. The import audit is a final read-only recovery source
/// so a reset cannot make the wallboards collapse to only the runs that still have live rows.
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
            foreach (var load in live)
            {
                if (merged.TryGetValue(load.Id, out var registered) && KeepRegisteredOverLiveTombstone(registered, load))
                    continue;
                merged[load.Id] = load;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        // Planner imports are also durably audited as one promoted plannerplanrun row per
        // imported run. If a planning-day reset removes the operational Load row, that audit
        // still represents an imported run and must remain visible to Operations and TV.
        // Never replace a real active live/register row with the audit projection.
        try
        {
            var audited = await PlannerPlanAuditProjection.ReadLoadsAsync(db, date, ct);
            var activeReferences = merged.Values
                .Where(load => load.Status != LoadStatus.Cancelled)
                .Select(load => load.Reference)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var load in audited)
            {
                if (activeReferences.Contains(load.Reference)) continue;
                merged[load.Id] = load;
                activeReferences.Add(load.Reference);
            }
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

    internal static bool KeepRegisteredOverLiveTombstone(Load registered, Load live) =>
        live.Status == LoadStatus.Cancelled && registered.Status != LoadStatus.Cancelled;

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
            var live = await db.Loads.AsNoTracking().Include(x => x.Stops).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (live is not null) return live;
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
        }

        try
        {
            return (await PlannerPlanAuditProjection.ReadLoadsAsync(db, null, ct)).SingleOrDefault(load => load.Id == id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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