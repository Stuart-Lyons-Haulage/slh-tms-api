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
/// Real planning/live rows always win. If an imported run survives only in audit, it is
/// materialised back into the Planning Register so it is not merely visible to Dashboard/TV:
/// it becomes writable by Driver Dispatch, routing, allocation and downstream execution too.
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

        // Planner imports are durably audited as one promoted plannerplanrun row per imported
        // run. If a reset removed the operational row, recover it into the Planning Register.
        // This closes the old split where Dashboard/TV could see an audit-only run but Driver
        // Dispatch could neither select nor save it. Never replace a real active row with audit.
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

                try
                {
                    await PlanningRegisterStore.SaveLoadAsync(db, load, "system:planner-audit-recovery", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Visibility remains available even if the recovery write is temporarily
                    // unavailable. A later read will retry materialisation automatically.
                    db.ChangeTracker.Clear();
                }

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
            var recovered = (await PlannerPlanAuditProjection.ReadLoadsAsync(db, null, ct)).SingleOrDefault(load => load.Id == id);
            if (recovered is null) return null;
            try
            {
                await PlanningRegisterStore.SaveLoadAsync(db, recovered, "system:planner-audit-recovery", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
            }
            return recovered;
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