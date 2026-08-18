using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Middleware;

public sealed class PlanLockMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (ControlPageFallback.IsProtectedGet(context.Request))
        {
            try
            {
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ControlPageFallback.WriteAsync(context, ex);
            }
            return;
        }

        if (!IsOperationalPlanWrite(context.Request))
        {
            await next(context);
            return;
        }

        var db = context.RequestServices.GetRequiredService<TmsDbContext>();
        var target = await ResolveTargetAsync(context, db, context.RequestAborted);
        if (target.Date is null || !await PlanLockStore.IsLockedAsync(db, target.Date.Value, context.RequestAborted))
        {
            await next(context);
            return;
        }

        var reason = context.Request.Headers[PlanLockStore.ReasonHeader].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                detail = "PLAN_LOCKED: This planning day is locked. Enter a reason for this operational change to continue.",
                planningDate = target.Date,
                requiredHeader = PlanLockStore.ReasonHeader
            }, context.RequestAborted);
            return;
        }

        LoadBaseline? before = null;
        if (target.LoadId is Guid loadId)
        {
            var load = await db.Loads.AsNoTracking().Include(x => x.Stops).SingleOrDefaultAsync(x => x.Id == loadId, context.RequestAborted);
            if (load is not null) before = PlanLockStore.Snapshot(load);
        }

        await next(context);
        if (context.Response.StatusCode < 200 || context.Response.StatusCode >= 300) return;

        LoadBaseline? after = null;
        if (target.LoadId is Guid changedLoadId)
        {
            var load = await db.Loads.AsNoTracking().Include(x => x.Stops).SingleOrDefaultAsync(x => x.Id == changedLoadId, context.RequestAborted);
            if (load is not null) after = PlanLockStore.Snapshot(load);
        }
        var type = ChangeType(context.Request.Path, before, after);
        await PlanLockStore.RecordChangeAsync(db, target.Date.Value, target.LoadId, type, reason, context.User.Identity?.Name, before, after, context.RequestAborted);
    }

    private static bool IsOperationalPlanWrite(HttpRequest request)
    {
        if (request.Method is not ("POST" or "PUT" or "PATCH" or "DELETE")) return false;
        var path = request.Path.Value ?? "";
        if (path.Equals("/api/v1/loads", StringComparison.OrdinalIgnoreCase) && request.Method == "POST") return true;
        if (!path.StartsWith("/api/v1/loads/", StringComparison.OrdinalIgnoreCase)) return false;
        return path.Contains("/allocation", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/stops", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/status", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(DateOnly? Date, Guid? LoadId)> ResolveTargetAsync(HttpContext context, TmsDbContext db, CancellationToken ct)
    {
        var path = context.Request.Path.Value ?? "";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && Guid.TryParse(parts[3], out var id))
        {
            var date = await db.Loads.AsNoTracking().Where(x => x.Id == id).Select(x => (DateOnly?)x.PlanningDate).SingleOrDefaultAsync(ct);
            return (date, id);
        }
        if (path.Equals("/api/v1/loads", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST")
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var json = await reader.ReadToEndAsync(ct);
            context.Request.Body.Position = 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("planningDate", out var property) && DateOnly.TryParse(property.GetString(), out var date)) return (date, null);
            }
            catch (JsonException) { }
        }
        return (null, null);
    }

    private static string ChangeType(PathString path, LoadBaseline? before, LoadBaseline? after)
    {
        var value = path.Value ?? "";
        if (value.Equals("/api/v1/loads", StringComparison.OrdinalIgnoreCase)) return "Run added";
        if (value.Contains("/stops", StringComparison.OrdinalIgnoreCase)) return "Route amendment";
        if (value.Contains("/status", StringComparison.OrdinalIgnoreCase)) return "Status change";
        if (value.Contains("/allocation", StringComparison.OrdinalIgnoreCase))
        {
            if (before?.DriverId != after?.DriverId && before?.VehicleId == after?.VehicleId) return "Driver swap";
            if (before?.VehicleId != after?.VehicleId && before?.DriverId == after?.DriverId) return "Vehicle swap";
            if (before?.DriverId != after?.DriverId && before?.VehicleId != after?.VehicleId) return "Driver/vehicle swap";
            return "Allocation change";
        }
        return "Plan change";
    }
}
