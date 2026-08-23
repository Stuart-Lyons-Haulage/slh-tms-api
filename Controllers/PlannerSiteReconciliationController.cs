using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning")]
[Authorize]
public sealed class PlannerSiteReconciliationController(TmsDbContext db) : ControllerBase
{
    [HttpPost("reconcile-sites/{date:datetime}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ReconcileSites(DateTime date, CancellationToken ct)
    {
        var planningDate = DateOnly.FromDateTime(date);
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        List<Load> loads;
        var registerFallback = false;
        try
        {
            loads = await db.Loads.Include(x => x.Stops).Where(x => x.PlanningDate == planningDate).ToListAsync(ct);
        }
        catch (Exception ex) when (IsSchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            loads = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct);
            registerFallback = true;
        }

        var changedStops = 0;
        var matchedStops = 0;
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var load in loads)
        {
            var loadChanged = false;
            foreach (var stop in load.Stops.OrderBy(x => x.Sequence))
            {
                var sourceName = ExtractSiteName(stop.Name);
                if (string.IsNullOrWhiteSpace(sourceName)) continue;

                var matches = MatchSites(sites, sourceName).Take(2).ToList();
                if (matches.Count == 0)
                {
                    unresolved.Add(sourceName);
                    continue;
                }
                if (matches.Count > 1)
                {
                    ambiguous.Add(sourceName);
                    continue;
                }

                matchedStops++;
                var site = matches[0];
                var beforeAddress = stop.Address;
                var beforeLat = stop.Latitude;
                var beforeLon = stop.Longitude;

                if (!string.IsNullOrWhiteSpace(site.CollectionAddress))
                    stop.Address = MergeAddress(site.CollectionAddress, stop.Address);
                if (stop.Latitude is null && site.Latitude is not null) stop.Latitude = site.Latitude;
                if (stop.Longitude is null && site.Longitude is not null) stop.Longitude = site.Longitude;

                if (!string.Equals(beforeAddress, stop.Address, StringComparison.Ordinal) || beforeLat != stop.Latitude || beforeLon != stop.Longitude)
                {
                    changedStops++;
                    loadChanged = true;
                }
            }

            if (!loadChanged) continue;
            if (registerFallback) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
            else await db.SaveChangesAsync(ct);
        }

        return Ok(new
        {
            planningDate,
            loads = loads.Count,
            matchedStops,
            changedStops,
            unresolved = unresolved.OrderBy(x => x).ToArray(),
            ambiguous = ambiguous.OrderBy(x => x).ToArray()
        });
    }

    internal static IEnumerable<Site> MatchSites(IEnumerable<Site> sites, string value)
    {
        var needle = Normalize(value);
        if (string.IsNullOrWhiteSpace(needle)) return [];

        return sites.Where(site =>
        {
            if (Normalize(site.ExternalCode) == needle || Normalize(site.Name) == needle || Normalize(site.DriverTextName) == needle)
                return true;

            return Aliases(site.Aliases).Any(alias => Normalize(alias) == needle);
        }).DistinctBy(site => site.Id);
    }

    private static IEnumerable<string> Aliases(string? aliases) => string.IsNullOrWhiteSpace(aliases)
        ? []
        : aliases.Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ExtractSiteName(string? stopName)
    {
        if (string.IsNullOrWhiteSpace(stopName)) return string.Empty;
        var value = stopName.Trim();
        foreach (var prefix in new[] { "Collect · ", "Deliver · ", "Collect - ", "Deliver - " })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return value[prefix.Length..].Trim();
        return value;
    }

    private static string? MergeAddress(string masterAddress, string? operationalDetail)
    {
        var master = masterAddress.Trim();
        if (string.IsNullOrWhiteSpace(operationalDetail)) return Clip(master, 500);
        var detail = operationalDetail.Trim();
        if (Normalize(detail).Contains(Normalize(master), StringComparison.Ordinal)) return Clip(detail, 500);
        return Clip($"{master} | {detail}", 500);
    }

    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }
}
