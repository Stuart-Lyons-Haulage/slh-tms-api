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
public sealed class FleetioResilientSyncController(
    FleetioClient fleetioClient,
    TmsDbContext db,
    ILogger<FleetioResilientSyncController> logger) : ControllerBase
{
    private sealed record DesiredMapping(string ExternalKey, string ExternalLabel, string EntityType, Guid EntityId);

    [HttpPost("sync-assets-resilient")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncAssetsResilient(CancellationToken ct)
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

            var (matchingMappings, mappingsAvailable) = await TryReadMappings(ct);
            var desiredMappings = new List<DesiredMapping>();

            var vehiclesUpdated = 0;
            var vehiclesCreated = 0;
            var trailersUpdated = 0;
            var trailersCreated = 0;
            var skipped = 0;

            foreach (var asset in vehicleAssets)
            {
                var registration = BestVehicleRegistration(asset);
                if (string.IsNullOrWhiteSpace(registration))
                {
                    skipped++;
                    continue;
                }

                var mappedId = MappingTarget(matchingMappings, asset.Id, "Vehicle");
                var registrationKey = Normalise(registration);
                var vehicle = mappedId is not null ? vehicles.FirstOrDefault(item => item.Id == mappedId.Value) : null;
                vehicle ??= vehicles.FirstOrDefault(item => Normalise(item.Registration) == registrationKey);
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

                desiredMappings.Add(new DesiredMapping(asset.Id, asset.Name ?? registration, "Vehicle", vehicle.Id));
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

                var mappedId = MappingTarget(matchingMappings, asset.Id, "Trailer");
                var trailer = mappedId is not null ? trailers.FirstOrDefault(item => item.Id == mappedId.Value) : null;
                trailer ??= !string.IsNullOrWhiteSpace(fleetioName)
                    ? trailers.FirstOrDefault(item => Normalise(item.TrailerNumber) == Normalise(fleetioName))
                    : null;
                trailer ??= !string.IsNullOrWhiteSpace(cNumber)
                    ? trailers.FirstOrDefault(item => Normalise(item.TrailerNumber) == Normalise(cNumber))
                    : null;

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
                    if (!string.IsNullOrWhiteSpace(fleetioName)) trailer.TrailerNumber = ClipRequired(fleetioName, 40);
                    trailer.Type = Clip(asset.Type, 80) ?? trailer.Type;
                    trailer.Active = true;
                    trailersUpdated++;
                }

                desiredMappings.Add(new DesiredMapping(asset.Id, fleetioName ?? cNumber ?? preferredTrailerNumber, "Trailer", trailer.Id));
            }

            // Commit the operational master first. IntegrationMappings is useful identity metadata,
            // but a stale/temporarily unavailable mapping table must never block Vehicles/Trailers.
            await db.SaveChangesAsync(ct);

            string? mappingWarning = null;
            var mappingsUpdated = 0;
            if (!mappingsAvailable)
            {
                mappingWarning = "Fleetio assets were synced to the TMS master, but Integration Mappings is temporarily unavailable. Registration/name matching remains active.";
            }
            else
            {
                try
                {
                    db.ChangeTracker.Clear();
                    var trackedMappings = await db.IntegrationMappings
                        .Where(item => item.Provider == "Fleetio")
                        .ToListAsync(ct);

                    foreach (var wanted in desiredMappings)
                    {
                        var mapping = trackedMappings.FirstOrDefault(item =>
                            item.Active &&
                            string.Equals(item.ExternalKey, wanted.ExternalKey, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.TmsEntityType, wanted.EntityType, StringComparison.OrdinalIgnoreCase));

                        if (mapping is null)
                        {
                            mapping = new IntegrationMapping
                            {
                                Provider = "Fleetio",
                                ExternalKey = ClipRequired(wanted.ExternalKey, 200),
                                ExternalLabel = Clip(wanted.ExternalLabel, 200),
                                TmsEntityType = wanted.EntityType,
                                TmsEntityId = wanted.EntityId,
                                Active = true,
                                UpdatedBy = User.Identity?.Name ?? "Fleetio sync"
                            };
                            db.IntegrationMappings.Add(mapping);
                            trackedMappings.Add(mapping);
                        }
                        else
                        {
                            mapping.ExternalLabel = Clip(wanted.ExternalLabel, 200);
                            mapping.TmsEntityId = wanted.EntityId;
                            mapping.Active = true;
                            mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
                            mapping.UpdatedBy = User.Identity?.Name ?? "Fleetio sync";
                        }
                        mappingsUpdated++;
                    }
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Fleetio assets synced but IntegrationMappings could not be persisted.");
                    mappingWarning = $"Fleetio assets were synced to the TMS master, but Integration Mappings could not be updated: {ex.GetBaseException().Message}";
                }
            }

            var summary = $"Fleetio sync completed: {vehiclesUpdated} vehicle(s) updated, {vehiclesCreated} vehicle(s) created, {trailersUpdated} trailer(s) updated and {trailersCreated} trailer(s) created.";
            if (!string.IsNullOrWhiteSpace(mappingWarning)) summary += $" {mappingWarning}";

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
                skipped,
                mappingsUpdated,
                mappingWarning,
                syncedAtUtc = DateTimeOffset.UtcNow,
                message = summary
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Fleetio resilient asset sync failed before the operational master could be committed.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                configured = true,
                connected = false,
                message = $"Fleetio asset sync failed before the TMS master could be saved: {exception.GetBaseException().Message}"
            });
        }
    }

    private async Task<(List<IntegrationMapping> Mappings, bool Available)> TryReadMappings(CancellationToken ct)
    {
        try
        {
            return (await db.IntegrationMappings.AsNoTracking()
                .Where(item => item.Active && item.Provider == "Fleetio")
                .ToListAsync(ct), true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "IntegrationMappings unavailable during Fleetio sync; falling back to registration/name identity matching.");
            return ([], false);
        }
    }

    private static Guid? MappingTarget(IEnumerable<IntegrationMapping> mappings, string fleetioId, string entityType) =>
        mappings.FirstOrDefault(item =>
            string.Equals(item.ExternalKey, fleetioId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.TmsEntityType, entityType, StringComparison.OrdinalIgnoreCase))?.TmsEntityId;

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
