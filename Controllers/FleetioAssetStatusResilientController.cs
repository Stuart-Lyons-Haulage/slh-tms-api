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
public sealed class FleetioAssetStatusResilientController(
    FleetioClient fleetioClient,
    TmsDbContext db,
    ILogger<FleetioAssetStatusResilientController> logger) : ControllerBase
{
    [HttpGet("asset-status-resilient")]
    public async Task<IActionResult> AssetStatus(CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured)
            return BadRequest(new { configured = false, missingSettings = fleetioClient.MissingSettings });

        try
        {
            var assets = await fleetioClient.GetVehiclesAsync(100, ct);
            var vehicles = await db.Set<Vehicle>().AsNoTracking().Where(item => item.Active).ToListAsync(ct);
            var trailers = await db.Set<Trailer>().AsNoTracking().Where(item => item.Active).ToListAsync(ct);
            var mappings = await SafeMappings(ct);

            var powered = assets.Where(asset => !IsTrailer(asset)).Select(asset =>
            {
                var mappedId = MappingTarget(mappings, asset.Id, "Vehicle");
                var registration = BestVehicleRegistration(asset);
                var match = mappedId is not null ? vehicles.FirstOrDefault(item => item.Id == mappedId.Value) : null;
                match ??= string.IsNullOrWhiteSpace(registration) ? null : vehicles.FirstOrDefault(item => Normalise(item.Registration) == Normalise(registration));
                match ??= vehicles.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.FleetioId) && string.Equals(item.FleetioId, asset.Id, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    tmsVehicleId = match?.Id,
                    registration = match?.Registration ?? registration ?? asset.Name ?? asset.Id,
                    fleetNumber = match?.FleetNumber ?? asset.FleetNumber,
                    fleetioId = asset.Id,
                    fleetioName = asset.Name,
                    fleetioStatus = asset.Status,
                    fleetioVor = asset.Vor == true || IsVorText(asset.Status),
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
            }).OrderBy(item => item.registration).ToList();

            var trailerRows = assets.Where(IsTrailer).Select(asset =>
            {
                var mappedId = MappingTarget(mappings, asset.Id, "Trailer");
                var slh = asset.Name?.Trim();
                var cNumber = asset.Registration?.Trim();
                var match = mappedId is not null ? trailers.FirstOrDefault(item => item.Id == mappedId.Value) : null;
                match ??= !string.IsNullOrWhiteSpace(slh) ? trailers.FirstOrDefault(item => Normalise(item.TrailerNumber) == Normalise(slh)) : null;
                match ??= !string.IsNullOrWhiteSpace(cNumber) ? trailers.FirstOrDefault(item => Normalise(item.TrailerNumber) == Normalise(cNumber)) : null;
                return new
                {
                    tmsTrailerId = match?.Id,
                    trailerNumber = match?.TrailerNumber ?? slh ?? cNumber ?? asset.Id,
                    fleetioCNumber = cNumber,
                    fleetioId = asset.Id,
                    fleetioName = slh,
                    fleetioStatus = asset.Status,
                    fleetioVor = asset.Vor == true || IsVorText(asset.Status),
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
            }).OrderBy(item => item.trailerNumber).ToList();

            return Ok(new
            {
                configured = true,
                connected = true,
                retrievedAtUtc = DateTimeOffset.UtcNow,
                vehicles = powered,
                trailers = trailerRows
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Resilient Fleetio asset status failed.");
            return StatusCode(500, new { configured = true, connected = false, message = ex.GetBaseException().Message });
        }
    }

    private async Task<List<IntegrationMapping>> SafeMappings(CancellationToken ct)
    {
        try
        {
            return await db.IntegrationMappings.AsNoTracking()
                .Where(item => item.Active && item.Provider == "Fleetio")
                .ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "IntegrationMappings unavailable during Fleetio status read; using registration/name matching.");
            return [];
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

    private static bool IsVorText(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Equals("VOR", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vehicle off road", StringComparison.OrdinalIgnoreCase)
            || text.Contains("out of service", StringComparison.OrdinalIgnoreCase)
            || text.Contains("out-of-service", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
