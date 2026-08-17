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

                // Exact full registration is authoritative. Do not use three-character
                // suffix matching here because that can bind the wrong Fleetio asset.
                var vehicle = vehicles.FirstOrDefault(item =>
                    Normalise(item.Registration) == registrationKey);

                // If a record was already linked to Fleetio, that stable ID is the next
                // safest fallback after an exact registration match.
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
                    // Fleetio owns these integration fields. Keep TMS-only planning,
                    // fuel and cab fields untouched.
                    vehicle.Registration = ClipRequired(registration, 20);
                    vehicle.FleetioId = Clip(asset.Id, 80);
                    vehicle.FleetioName = Clip(asset.Name, 160);
                    vehicle.FleetioStatus = Clip(asset.Status, 80);
                    if (!string.IsNullOrWhiteSpace(asset.FleetNumber))
                        vehicle.FleetNumber = Clip(asset.FleetNumber, 40);
                    vehicle.Active = true;
                    vehiclesUpdated++;
                }
            }

            foreach (var asset in trailerAssets)
            {
                var fleetioName = asset.Name?.Trim();
                var cNumber = asset.Registration?.Trim();
                var preferredTrailerNumber = !string.IsNullOrWhiteSpace(fleetioName)
                    ? fleetioName
                    : cNumber;

                if (string.IsNullOrWhiteSpace(preferredTrailerNumber))
                {
                    skipped++;
                    continue;
                }

                var candidateKeys = new[] { fleetioName, cNumber }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => Normalise(value!))
                    .Where(value => value.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var trailer = trailers.FirstOrDefault(item =>
                    candidateKeys.Contains(Normalise(item.TrailerNumber)));

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
                    // Fleetio is authoritative for the trailer identity/type while
                    // pallet capacities remain TMS planning data.
                    trailer.TrailerNumber = ClipRequired(preferredTrailerNumber, 40);
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
                skipped,
                syncedAtUtc = DateTimeOffset.UtcNow,
                message = $"Fleetio sync completed: {vehiclesUpdated} vehicle(s) updated, {vehiclesCreated} vehicle(s) created, {trailersUpdated} trailer(s) updated and {trailersCreated} trailer(s) created."
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
        if (asset.Type?.Contains("Trailer", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return !string.IsNullOrWhiteSpace(asset.Registration) &&
               Regex.IsMatch(asset.Registration.Trim(), "^C\\d{5,}$", RegexOptions.IgnoreCase);
    }

    private static string? BestVehicleRegistration(FleetioVehicle asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.Registration) &&
            !Regex.IsMatch(asset.Registration.Trim(), "^C\\d{5,}$", RegexOptions.IgnoreCase))
            return asset.Registration.Trim();

        // Some Fleetio vehicle records use Name as the registration.
        if (!string.IsNullOrWhiteSpace(asset.Name) && LooksLikeUkRegistration(asset.Name))
            return asset.Name.Trim();

        return null;
    }

    private static bool LooksLikeUkRegistration(string value)
    {
        var key = Normalise(value);
        return key.Length is >= 5 and <= 8 && key.Any(char.IsLetter) && key.Any(char.IsDigit);
    }

    private static string Normalise(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

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
