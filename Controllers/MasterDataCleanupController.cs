using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/master-data-cleanup")]
[Authorize]
public sealed class MasterDataCleanupController(TmsDbContext db) : ControllerBase
{
    [HttpPost("{entity}/{id:guid}/archive"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> Archive(string entity, Guid id, CancellationToken ct)
        => SetActive(entity, id, false, ct);

    [HttpPost("{entity}/{id:guid}/restore"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> Restore(string entity, Guid id, CancellationToken ct)
        => SetActive(entity, id, true, ct);

    [HttpDelete("{entity}/{id:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Delete(string entity, Guid id, CancellationToken ct)
    {
        entity = CanonicalEntity(entity);
        if (entity.Length == 0) return BadRequest(new { code = "unsupported_master_entity", message = "This master-data type cannot be deleted from the cleanup screen." });

        var activeState = await ReadActive(entity, id, ct);
        if (activeState is null) return NotFound(new { code = "master_data_not_found", message = "This master-data record no longer exists." });
        if (activeState.Value)
            return Conflict(new { code = "archive_before_delete", message = "Archive this record first. Permanent delete is only available for an archived duplicate or incorrect master record." });

        var usage = await Usage(entity, id, ct);
        if (usage.Count > 0)
            return Conflict(new
            {
                code = "master_data_in_use",
                message = $"This {Singular(entity)} is referenced by TMS history or live operational data and cannot be deleted. Keep it archived instead.",
                references = usage
            });

        var result = await Remove(entity, id, ct);
        if (result is null) return NotFound(new { code = "master_data_not_found", message = "This master-data record no longer exists." });

        return Ok(new
        {
            deleted = true,
            entity,
            id,
            integrationMappingsRemoved = result.Value.MappingsRemoved,
            message = $"{Title(Singular(entity))} permanently deleted from the TMS master. Historical operational records were not removed."
        });
    }

    private async Task<IActionResult> SetActive(string entity, Guid id, bool active, CancellationToken ct)
    {
        entity = CanonicalEntity(entity);
        if (entity.Length == 0) return BadRequest(new { code = "unsupported_master_entity", message = "This master-data type cannot be archived from the cleanup screen." });

        object? item = entity switch
        {
            "drivers" => await db.Drivers.FirstOrDefaultAsync(x => x.Id == id, ct),
            "vehicles" => await db.Vehicles.FirstOrDefaultAsync(x => x.Id == id, ct),
            "trailers" => await db.Trailers.FirstOrDefaultAsync(x => x.Id == id, ct),
            "sites" => await db.Sites.FirstOrDefaultAsync(x => x.Id == id, ct),
            "customers" => await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct),
            "geofences" => await db.SiteGeofences.FirstOrDefaultAsync(x => x.Id == id, ct),
            _ => null
        };
        if (item is null) return NotFound(new { code = "master_data_not_found", message = "This master-data record no longer exists." });

        var current = Active(item);
        if (current == active) return Ok(new { active, unchanged = true });

        var before = Snapshot(item);
        SetActiveValue(item, active);
        if (item is SiteGeofence geofence) geofence.UpdatedAtUtc = DateTimeOffset.UtcNow;
        db.MasterDataAudits.Add(NewAudit(Title(Singular(entity)), id, active ? "Restored" : "Archived", before, Snapshot(item)));
        await db.SaveChangesAsync(ct);
        return Ok(new { active, entity, id });
    }

    private async Task<bool?> ReadActive(string entity, Guid id, CancellationToken ct) => entity switch
    {
        "drivers" => await db.Drivers.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        "vehicles" => await db.Vehicles.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        "trailers" => await db.Trailers.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        "sites" => await db.Sites.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        "customers" => await db.Customers.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        "geofences" => await db.SiteGeofences.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        _ => null
    };

    private async Task<List<object>> Usage(string entity, Guid id, CancellationToken ct)
    {
        var result = new List<object>();
        var idText = id.ToString();

        if (entity == "vehicles")
        {
            var loads = await db.Loads.AsNoTracking().CountAsync(x => x.VehicleId == id, ct);
            if (loads > 0) result.Add(new { area = "Runs", count = loads });
            var visits = await db.GeofenceVisits.AsNoTracking().CountAsync(x => x.VehicleId == id, ct);
            if (visits > 0) result.Add(new { area = "Geofence history", count = visits });
        }
        else if (entity == "drivers")
        {
            var loads = await db.Loads.AsNoTracking().CountAsync(x => x.DriverId == id, ct);
            if (loads > 0) result.Add(new { area = "Runs", count = loads });
            var statuses = await db.DriverStatusLogs.AsNoTracking().CountAsync(x => x.DriverId == id, ct);
            if (statuses > 0) result.Add(new { area = "Driver status history", count = statuses });
        }
        else if (entity == "trailers")
        {
            var loads = await db.Loads.AsNoTracking().CountAsync(x => x.TrailerId == id, ct);
            if (loads > 0) result.Add(new { area = "Runs", count = loads });
        }
        else if (entity == "sites")
        {
            var geofences = await db.SiteGeofences.AsNoTracking().CountAsync(x => x.SiteId == id, ct);
            if (geofences > 0) result.Add(new { area = "Geofences", count = geofences });
        }
        else if (entity == "geofences")
        {
            var visits = await db.GeofenceVisits.AsNoTracking().CountAsync(x => x.GeofenceId == id, ct);
            if (visits > 0) result.Add(new { area = "Geofence visit history", count = visits });
        }
        else if (entity == "customers")
        {
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (customer is null) return result;
            var orders = await db.TransportOrders.AsNoTracking().CountAsync(x => x.CustomerCode == customer.Code, ct);
            if (orders > 0) result.Add(new { area = "Orders", count = orders });
            var contacts = await db.CustomerContacts.AsNoTracking().CountAsync(x => x.CustomerCode == customer.Code, ct);
            if (contacts > 0) result.Add(new { area = "Customer contacts", count = contacts });
            var stagedByCode = await db.StagedImports.AsNoTracking().CountAsync(x => x.PayloadJson.Contains(customer.Code), ct);
            if (stagedByCode > 0) result.Add(new { area = "Import / planning history", count = stagedByCode });
            return result;
        }

        var staged = await db.StagedImports.AsNoTracking().CountAsync(x => x.PayloadJson.Contains(idText), ct);
        if (staged > 0) result.Add(new { area = "Import / planning history", count = staged });
        return result;
    }

    private async Task<(int MappingsRemoved)?> Remove(string entity, Guid id, CancellationToken ct)
    {
        object? item = entity switch
        {
            "drivers" => await db.Drivers.FirstOrDefaultAsync(x => x.Id == id, ct),
            "vehicles" => await db.Vehicles.FirstOrDefaultAsync(x => x.Id == id, ct),
            "trailers" => await db.Trailers.FirstOrDefaultAsync(x => x.Id == id, ct),
            "sites" => await db.Sites.FirstOrDefaultAsync(x => x.Id == id, ct),
            "customers" => await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct),
            "geofences" => await db.SiteGeofences.FirstOrDefaultAsync(x => x.Id == id, ct),
            _ => null
        };
        if (item is null) return null;

        var snapshot = Snapshot(item);
        var mappings = await db.IntegrationMappings.Where(x => x.TmsEntityId == id).ToListAsync(ct);
        if (mappings.Count > 0) db.IntegrationMappings.RemoveRange(mappings);

        switch (item)
        {
            case Driver driver: db.Drivers.Remove(driver); break;
            case Vehicle vehicle: db.Vehicles.Remove(vehicle); break;
            case Trailer trailer: db.Trailers.Remove(trailer); break;
            case Site site: db.Sites.Remove(site); break;
            case Customer customer: db.Customers.Remove(customer); break;
            case SiteGeofence geofence: db.SiteGeofences.Remove(geofence); break;
            default: return null;
        }

        db.MasterDataAudits.Add(NewAudit(Title(Singular(entity)), id, "Deleted", snapshot, "null"));
        await db.SaveChangesAsync(ct);
        return (mappings.Count);
    }

    private MasterDataAudit NewAudit(string entityType, Guid entityId, string action, string before, string after)
        => new()
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            ChangesJson = JsonSerializer.Serialize(new
            {
                before = JsonDocument.Parse(before).RootElement,
                after = after == "null" ? (JsonElement?)null : JsonDocument.Parse(after).RootElement
            }),
            ChangedBy = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown"
        };

    private static bool Active(object item) => item switch
    {
        Driver x => x.Active,
        Vehicle x => x.Active,
        Trailer x => x.Active,
        Site x => x.Active,
        Customer x => x.Active,
        SiteGeofence x => x.Active,
        _ => false
    };

    private static void SetActiveValue(object item, bool active)
    {
        switch (item)
        {
            case Driver x: x.Active = active; break;
            case Vehicle x: x.Active = active; break;
            case Trailer x: x.Active = active; break;
            case Site x: x.Active = active; break;
            case Customer x: x.Active = active; break;
            case SiteGeofence x: x.Active = active; break;
        }
    }

    private static string CanonicalEntity(string value) => value.Trim().ToLowerInvariant() switch
    {
        "driver" or "drivers" => "drivers",
        "vehicle" or "vehicles" => "vehicles",
        "trailer" or "trailers" => "trailers",
        "site" or "sites" => "sites",
        "customer" or "customers" => "customers",
        "geofence" or "geofences" => "geofences",
        _ => string.Empty
    };

    private static string Singular(string entity) => entity.EndsWith('s') ? entity[..^1] : entity;
    private static string Title(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
    private static string Snapshot<T>(T value) => JsonSerializer.Serialize(value);
}
