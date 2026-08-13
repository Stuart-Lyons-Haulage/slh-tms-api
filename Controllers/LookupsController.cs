using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;
[ApiController, Route("api/v1")]
[Authorize]
public sealed class LookupsController(TmsDbContext db) : ControllerBase
{
    [HttpGet("customers")] public async Task<IActionResult> Customers([FromQuery] string? q, CancellationToken ct) => Ok(await db.Customers.AsNoTracking().Where(x => x.Active && (q == null || x.Code.Contains(q) || x.Name.Contains(q))).OrderBy(x => x.Name).Take(5000).ToListAsync(ct));
    [HttpGet("customer-contacts")] public async Task<IActionResult> CustomerContacts([FromQuery] string? q, CancellationToken ct) => Ok(await db.CustomerContacts.AsNoTracking().Where(x => x.Active && (q == null || x.CustomerCode.Contains(q) || x.Name.Contains(q) || (x.Email != null && x.Email.Contains(q)))).OrderBy(x => x.CustomerCode).ThenBy(x => x.Name).Take(5000).ToListAsync(ct));
    [HttpGet("vehicles")] public async Task<IActionResult> Vehicles([FromQuery] string? q, CancellationToken ct) => Ok(await db.Vehicles.AsNoTracking().Where(x => x.Active && (q == null || x.Registration.Contains(q) || (x.FleetNumber != null && x.FleetNumber.Contains(q)))).OrderBy(x => x.Registration).Take(5000).ToListAsync(ct));
    [HttpGet("drivers")] public async Task<IActionResult> Drivers([FromQuery] string? q, CancellationToken ct) => Ok(await db.Drivers.AsNoTracking().Where(x => x.Active && (q == null || x.EmployeeNumber.Contains(q) || x.DisplayName.Contains(q))).OrderBy(x => x.DisplayName).Take(5000).ToListAsync(ct));
    [HttpGet("trailers")] public async Task<IActionResult> Trailers([FromQuery] string? q, CancellationToken ct) => Ok(await db.Trailers.AsNoTracking().Where(x => x.Active && (q == null || x.TrailerNumber.Contains(q) || (x.Type != null && x.Type.Contains(q)))).OrderBy(x => x.TrailerNumber).Take(5000).ToListAsync(ct));
    [HttpGet("sites")] public async Task<IActionResult> Sites([FromQuery] string? q, CancellationToken ct) => Ok(await db.Sites.AsNoTracking().Where(x => x.Active && (q == null || x.Name.Contains(q) || (x.DriverTextName != null && x.DriverTextName.Contains(q)))).OrderBy(x => x.Name).Take(5000).ToListAsync(ct));
    [HttpGet("market-contacts")] public async Task<IActionResult> MarketContacts([FromQuery] string? q, CancellationToken ct) => Ok(await db.MarketContacts.AsNoTracking().Where(x => x.Active && (q == null || x.Name.Contains(q) || x.Market.Contains(q))).OrderBy(x => x.Market).ThenBy(x => x.Name).Take(5000).ToListAsync(ct));


    [HttpPut("vehicles/{id:guid}")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateVehicle(Guid id, [FromBody] VehicleUpdateRequest request, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (vehicle is null) return NotFound();
        var registration = ClipRequired((request.Registration ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant(), 20);
        if (string.IsNullOrWhiteSpace(registration)) return BadRequest(new { message = "Registration is required." });
        if (await db.Vehicles.AnyAsync(item => item.Id != id && item.Registration == registration, ct)) return Conflict(new { message = $"Registration {registration} already exists." });
        vehicle.Registration = registration;
        vehicle.FleetNumber = Clip(request.FleetNumber, 40);
        vehicle.Abbreviation = Clip(request.Abbreviation, 20);
        vehicle.Transmission = Clip(request.Transmission, 20);
        vehicle.DvsCompliant = request.DvsCompliant;
        vehicle.FuelProvider = Clip(request.FuelProvider, 30);
        vehicle.CabMobile = Clip(request.CabMobile, 40);
        vehicle.FuelPin = Clip(request.FuelPin, 80);
        vehicle.ShellCard = Clip(request.ShellCard, 80);
        vehicle.BpRedCard = Clip(request.BpRedCard, 80);
        vehicle.BpPlainCard = Clip(request.BpPlainCard, 80);
        vehicle.Notes = Clip(request.Notes, 500);
        vehicle.FuelPinSecretName = Clip(request.FuelPinSecretName, 120);
        vehicle.FuelCardLastFour = Clip(request.FuelCardLastFour, 4);
        vehicle.Active = request.Active;
        await db.SaveChangesAsync(ct);
        return Ok(vehicle);
    }

    private static string? Clip(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
    private static string ClipRequired(string value, int maxLength) => value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
}

public sealed record VehicleUpdateRequest(string Registration, string? FleetNumber, string? Abbreviation, string? Transmission, bool? DvsCompliant, string? FuelProvider, string? CabMobile, string? FuelPin, string? ShellCard, string? BpRedCard, string? BpPlainCard, string? Notes, string? FuelPinSecretName, string? FuelCardLastFour, bool Active);
