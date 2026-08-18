using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/integrations/fleetio")]
[Authorize]
public sealed class FleetioAssetSyncController(
    FleetioClient fleetioClient,
    TmsDbContext db,
    ILogger<FleetioAssetSyncController> logger) : ControllerBase
{
    [HttpGet("asset-status")]
    public async Task<IActionResult> AssetStatus(CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured)
            return BadRequest(new { configured = false, missingSettings = fleetioClient.MissingSettings });

        try
        {
            var assets = await fleetioClient.GetVehiclesAsync(100, ct);
            var vehicles = await db.Set<Vehicle>().AsNoTracking().Where(v => v.Active).ToListAsync(ct);
            var trailers = await db.Set<Trailer>().AsNoTracking().Where(t => t.Active).ToListAsync(ct);

            var powered = assets.Where(asset => !IsTrailer(asset)).Select(asset =>
            {
                var registration = BestVehicleRegistration(asset);
                var match = string.IsNullOrWhiteSpace(registration) ? null : vehicles.FirstOrDefault(v => Normalise(v.Registration) == Normalise(registration));
                match ??= vehicles.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.FleetioId) && string.Equals(v.FleetioId, asset.Id, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    tmsVehicleId = match?.Id,
                    registration = match?.Registration ?? registration ?? asset.Name ?? asset.Id,
                    fleetNumber = match?.FleetNumber ?? asset.FleetNumber,
                    fleetioId = asset.Id,
                    fleetioName = asset.Name,
                    fleetioStatus = asset.Status,
                    vin = asset.Vin,
                    year = asset.Year,
                    make = asset.Make,
                    model = asset.Model,
                    trim = asset.Trim,
                    issuesCount = asset.IssuesCount,
                    workOrdersCount = asset.WorkOrdersCount,
                    primaryMeterValue = asset.PrimaryMeterValue,
                    primaryMeterUnit = asset.PrimaryMeterUnit,
                    pmiDueUtc = asset.PmiDueUtc,
                    motDueUtc = asset.MotDueUtc,
                    serviceStatus = asset.ServiceStatus,
                    matched = match is not null
                };
            }).OrderBy(x => x.registration).ToList();

            var trailerRows = assets.Where(IsTrailer).Select(asset =>
            {
                var slh = asset.Name?.Trim();
                var cNumber = asset.Registration?.Trim();
                var nameMatch = !string.IsNullOrWhiteSpace(slh)
                    ? trailers.FirstOrDefault(t => Normalise(t.TrailerNumber) == Normalise(slh))
                    : null;
                var cMatch = !string.IsNullOrWhiteSpace(cNumber)
                    ? trailers.FirstOrDefault(t => Normalise(t.TrailerNumber) == Normalise(cNumber))
                    : null;
                var match = nameMatch ?? cMatch;
                return new
                {
                    tmsTrailerId = match?.Id,
                    trailerNumber = match?.TrailerNumber ?? slh ?? cNumber ?? asset.Id,
                    fleetioCNumber = cNumber,
                    fleetioId = asset.Id,
                    fleetioName = slh,
                    fleetioStatus = asset.Status,
                    type = asset.Type,
                    vin = asset.Vin,
                    year = asset.Year,
                    make = asset.Make,
                    model = asset.Model,
                    trim = asset.Trim,
                    issuesCount = asset.IssuesCount,
                    workOrdersCount = asset.WorkOrdersCount,
                    pmiDueUtc = asset.PmiDueUtc,
                    motDueUtc = asset.MotDueUtc,
                    serviceStatus = asset.ServiceStatus,
                    matched = match is not null
                };
            }).OrderBy(x => x.trailerNumber).ToList();

            return Ok(new
            {
                configured = true,
                connected = true,
                retrievedAtUtc = DateTimeOffset.UtcNow,
                vehicles = powered,
                trailers = trailerRows
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Fleetio asset status failed.");
            return StatusCode(500, new { configured = true, connected = false, message = exception.GetBaseException().Message });
        }
    }

    [HttpGet("asset-maintenance/{fleetioId}")]
    public async Task<IActionResult> AssetMaintenance(string fleetioId, CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured)
            return BadRequest(new { configured = false, missingSettings = fleetioClient.MissingSettings });

        try
        {
            var snapshot = await fleetioClient.GetMaintenanceSnapshotAsync(fleetioId, ct);
            return Ok(snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Fleetio maintenance detail failed for asset {FleetioId}.", fleetioId);
            return StatusCode(500, new { configured = true, connected = false, message = exception.GetBaseException().Message });
        }
    }

    [HttpPost("sync-assets")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncAssets(CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured)
        {
            return BadRequest(new
            {
                configured = false,
                missingSettings = fleetioClient.MissingSettings,
                message = $"Fleetio cannot sync until these settings are complete: {string.Join(", ", fleetioClient.MissingSettings)}."
            });
        }

        try
        {
            var assets = await fleetioClient.GetVehiclesAsync(100, ct);
            var trailerAssets = assets.Where(IsTrailer).ToList();
            var vehicleAssets = assets.Where(asset => !IsTrailer(asset)).ToList();

            var vehicles = await db.Set<Vehicle>().ToListAsync(ct);
            var trailers = await db.Set<Trailer>().ToListAsync(ct);

            var vehiclesUpdated = 0;
            var vehiclesCreated = 0;
            var trailersUpdated = 0;
            var trailersCreated = 0;
            var trailerDuplicatesMerged = 0;
            var skipped = 0;

            foreach (var asset in vehicleAssets)
            {
                var registration = BestVehicleRegistration(asset);
                if (string.IsNullOrWhiteSpace(registration))
                {
                    skipped++;
                    continue;
                }

                var registrationKey = Normalise(registration);
                var vehicle = vehicles.FirstOrDefault(item => Normalise(item.Registration) == registrationKey);
                vehicle ??= vehicles.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(item.FleetioId) &&
                    string.Equals(item.FleetioId, asset.Id, StringComparison.OrdinalIgnoreCase));

                if (vehicle is null)
                {
                    vehicle = new Vehicle
                    {
                        Registration = ClipRequired(registration, 20),
                        FleetNumber = Clip(asset.FleetNumber, 40),
                        FleetioId = Clip(asset.Id, 80),
                        FleetioName = Clip(asset.Name, 160),
                        FleetioStatus = Clip(asset.Status, 80),
                        Active = true
                    };
                    db.Set<Vehicle>().Add(vehicle);
                    vehicles.Add(vehicle);
                    vehiclesCreated++;
                }
                else
                {
                    vehicle.Registration = ClipRequired(registration, 20);
                    vehicle.FleetioId = Clip(asset.Id, 80);
                    vehicle.FleetioName = Clip(asset.Name, 160);
                    vehicle.FleetioStatus = Clip(asset.Status, 80);
                    if (!string.IsNullOrWhiteSpace(asset.FleetNumber)) vehicle.FleetNumber = Clip(asset.FleetNumber, 40);
                    vehicle.Active = true;
                    vehiclesUpdated++;
                }

                vehicle.FleetioPmiDueUtc = asset.PmiDueUtc;
                vehicle.FleetioMotDueUtc = asset.MotDueUtc;
                vehicle.FleetioServiceStatus = asset.ServiceStatus;
                vehicle.FleetioLastSyncedUtc = DateTimeOffset.UtcNow;
            }

            foreach (var asset in trailerAssets)
            {
                var fleetioName = asset.Name?.Trim();
                var cNumber = asset.Registration?.Trim();
                var preferredTrailerNumber = !string.IsNullOrWhiteSpace(fleetioName) ? fleetioName : cNumber;
                if (string.IsNullOrWhiteSpace(preferredTrailerNumber))
                {
                    skipped++;
                    continue;
                }

                var nameMatch = !string.IsNullOrWhiteSpace(fleetioName)
                    ? trailers.FirstOrDefault(item => Normalise(item.TrailerNumber) == Normalise(fleetioName))
                    : null;
                var cMatch = !string.IsNullOrWhiteSpace(cNumber)
                    ? trailers.FirstOrDefault(item => Normalise(item.TrailerNumber) == Normalise(cNumber))
                    : null;
                var trailer = nameMatch ?? cMatch;

                if (nameMatch is not null && cMatch is not null && nameMatch.Id != cMatch.Id)
                {
                    var loadsUsingDuplicate = await db.Loads.Where(load => load.TrailerId == cMatch.Id).ToListAsync(ct);
                    foreach (var load in loadsUsingDuplicate) load.TrailerId = nameMatch.Id;
                    cMatch.Active = false;
                    trailer = nameMatch;
                    trailerDuplicatesMerged++;
                }

                if (trailer is null)
                {
                    trailer = new Trailer
                    {
                        TrailerNumber = ClipRequired(preferredTrailerNumber, 40),
                        Type = Clip(asset.Type, 80),
                        Active = true
                    };
                    db.Set<Trailer>().Add(trailer);
                    trailers.Add(trailer);
                    trailersCreated++;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(fleetioName))
                        trailer.TrailerNumber = ClipRequired(fleetioName, 40);
                    trailer.Type = Clip(asset.Type, 80) ?? trailer.Type;
                    trailer.Active = true;
                    trailersUpdated++;
                }
            }

            await db.SaveChangesAsync(ct);

            return Ok(new
            {
                configured = true,
                connected = true,
                sourceAssetCount = assets.Count,
                sourceVehicleCount = vehicleAssets.Count,
                sourceTrailerCount = trailerAssets.Count,
                vehiclesUpdated,
                vehiclesCreated,
                trailersUpdated,
                trailersCreated,
                trailerDuplicatesMerged,
                skipped,
                syncedAtUtc = DateTimeOffset.UtcNow,
                message = $"Fleetio sync completed: {vehiclesUpdated} vehicle(s) updated, {vehiclesCreated} vehicle(s) created, {trailersUpdated} trailer(s) updated, {trailersCreated} trailer(s) created and {trailerDuplicatesMerged} duplicate trailer identity record(s) merged."
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Fleetio full asset sync failed.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                configured = true,
                connected = false,
                message = $"Fleetio asset sync failed: {exception.GetBaseException().Message}"
            });
        }
    }

    private static bool IsTrailer(FleetioVehicle asset)
    {
        if (asset.Type?.Contains("Trailer", StringComparison.OrdinalIgnoreCase) == true) return true;
        return !string.IsNullOrWhiteSpace(asset.Registration) && Regex.IsMatch(asset.Registration.Trim(), "^C\\d{5,}$", RegexOptions.IgnoreCase);
    }

    private static string? BestVehicleRegistration(FleetioVehicle asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.Registration) && !Regex.IsMatch(asset.Registration.Trim(), "^C\\d{5,}$", RegexOptions.IgnoreCase))
            return asset.Registration.Trim();
        if (!string.IsNullOrWhiteSpace(asset.Name) && LooksLikeUkRegistration(asset.Name)) return asset.Name.Trim();
        return null;
    }

    private static bool LooksLikeUkRegistration(string value)
    {
        var key = Normalise(value);
        return key.Length is >= 5 and <= 8 && key.Any(char.IsLetter) && key.Any(char.IsDigit);
    }

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
    private static string ClipRequired(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
