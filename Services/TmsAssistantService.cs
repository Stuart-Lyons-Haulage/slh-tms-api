using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Assistant;

namespace Slh.Tms.Api.Services;

public sealed class TmsAssistantService(
    HttpClient httpClient,
    TmsDbContext db,
    AzureMapsRouteClient maps,
    AssistantOptions options,
    ILogger<TmsAssistantService> logger)
{
    public async Task<AssistantSnapshot> GetSnapshot(DateOnly planningDate, CancellationToken ct)
    {
        var orders = await ReadOrders(planningDate, ct);
        var loads = await ReadLoads(planningDate, ct);
        var drivers = await SafeRead(db.Drivers.AsNoTracking().Where(x => x.Active).Take(1000), "drivers", ct);
        var vehicles = await SafeRead(db.Vehicles.AsNoTracking().Where(x => x.Active).Take(1000), "vehicles", ct);
        var sites = await SafeRead(db.Sites.AsNoTracking().Where(x => x.Active).Take(2000), "sites", ct);
        await SafeEnrichSites(sites, ct);

        var plannedOrderIds = loads
            .SelectMany(x => x.Stops ?? [])
            .Where(x => x.OrderId is not null)
            .Select(x => x.OrderId!.Value)
            .ToHashSet();
        var unplanned = orders.Where(x => !plannedOrderIds.Contains(x.Id)).ToList();
        var unallocated = loads.Where(x => x.DriverId is null || x.VehicleId is null).ToList();
        var suggestions = new List<AssistantSuggestion>();
        var now = DateTimeOffset.UtcNow;

        if (unplanned.Count > 0)
            suggestions.Add(new("orders-unplanned", "high", "Plan approved orders", $"{unplanned.Count} approved order{(unplanned.Count == 1 ? " is" : "s are")} not yet on a run for {planningDate:dd MMM}.", "Planner", false));

        if (unallocated.Count > 0)
            suggestions.Add(new("loads-unallocated", "high", "Complete run allocations", $"{unallocated.Count} run{(unallocated.Count == 1 ? " is" : "s are")} missing a driver or vehicle.", "Planner", false));

        var noMapPoints = loads.Where(x => x.Stops.Count == 0 || x.Stops.Any(stop => stop.Latitude is null || stop.Longitude is null)).ToList();
        if (noMapPoints.Count > 0)
            suggestions.Add(new("loads-unmapped", "medium", "Finish route points", $"{noMapPoints.Count} run{(noMapPoints.Count == 1 ? " has" : "s have")} missing stop coordinates, so routing and ETA checks are incomplete.", "Planner", false));

        var emptyMiles = loads.Sum(x => x.EmptyMiles ?? 0);
        var distance = loads.Sum(x => x.EstimatedDistanceMiles ?? 0);
        if (distance > 0 && emptyMiles / distance >= 0.2m)
            suggestions.Add(new("loads-empty-miles", "medium", "Reduce empty running", $"Empty mileage is {Math.Round(emptyMiles / distance * 100, 1)}% of recorded miles for the day. Review return-load suggestions and route pairing.", "Planner", false));

        var missingDriverMobiles = drivers.Count(x => string.IsNullOrWhiteSpace(x.MobileNumber));
        if (missingDriverMobiles > 0)
            suggestions.Add(new("drivers-mobile", "medium", "Complete driver contact data", $"{missingDriverMobiles} active driver{(missingDriverMobiles == 1 ? " has" : "s have")} no dispatch mobile number.", "Drivers", false));

        var missingTachoNames = drivers.Count(x => string.IsNullOrWhiteSpace(x.TachoName));
        if (missingTachoNames > 0)
            suggestions.Add(new("drivers-tacho", "medium", "Match TachoMaster names", $"{missingTachoNames} active driver{(missingTachoNames == 1 ? " has" : "s have")} no TachoMaster matching name.", "Drivers", false));

        var licenceDue = drivers.Count(x => x.LicenceExpiry is null || x.LicenceExpiry <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));
        if (licenceDue > 0)
            suggestions.Add(new("drivers-licence", "high", "Review driver licences", $"{licenceDue} active driver licence record{(licenceDue == 1 ? " needs" : "s need")} checking or expires within 30 days.", "Drivers", false));

        var vehicleRisk = vehicles.Count(x =>
            x.FleetioVor == true ||
            x.FleetioMotDueUtc <= now.AddDays(30) ||
            x.FleetioPmiDueUtc <= now.AddDays(30) ||
            (x.FleetioStatus ?? string.Empty).Contains("out of service", StringComparison.OrdinalIgnoreCase));
        if (vehicleRisk > 0)
            suggestions.Add(new("fleet-compliance", "high", "Protect fleet availability", $"{vehicleRisk} vehicle{(vehicleRisk == 1 ? " needs" : "s need")} Fleetio/compliance review before allocation.", "Vehicles", false));

        var missingMapLinks = sites.Count(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && string.IsNullOrWhiteSpace(x.MapLink));
        if (missingMapLinks > 0)
            suggestions.Add(new("sites-map-link", "low", "Create missing site map links", $"{missingMapLinks} site{(missingMapLinks == 1 ? " has" : "s have")} an address but no driver map link. This can be fixed safely.", "Sites", true));

        var missingMapPoints = sites.Count(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && (x.Latitude is null || x.Longitude is null));
        if (missingMapPoints > 0)
            suggestions.Add(new("sites-map-point", "medium", "Add missing map points", $"{missingMapPoints} site{(missingMapPoints == 1 ? " has" : "s have")} an address but no latitude/longitude. The Assistant can use Azure Maps to add these coordinates safely.", "Sites", true));

        var geofenceLinkGaps = await GeofenceLinkGaps(ct);
        if (geofenceLinkGaps.SitesMissingGeofence > 0)
            suggestions.Add(new("geofences-sites-sync", geofenceLinkGaps.AutoFixAvailable ? "high" : "medium", "Sync geofences with sites", $"{geofenceLinkGaps.SitesMissingGeofence} active site{(geofenceLinkGaps.SitesMissingGeofence == 1 ? " is" : "s are")} missing a confirmed geofence link. The Assistant can safely apply unique name-confirmed matches; ambiguous links remain review-only.", "Sites", geofenceLinkGaps.AutoFixAvailable));

        var duplicateSiteGroups = FindDuplicateSiteGroups(sites);
        if (duplicateSiteGroups.Count > 0)
        {
            var examples = string.Join("; ", duplicateSiteGroups.Take(3).Select(group => string.Join(" / ", group.Select(site => $"{site.Name} ({site.ExternalCode})"))));
            suggestions.Add(new("sites-duplicates", "high", "Review likely duplicate sites", $"{duplicateSiteGroups.Count} likely duplicate site group{(duplicateSiteGroups.Count == 1 ? " was" : "s were")} found: {examples}.", "Sites", true));
        }

        var untidyRegistrations = vehicles.Count(x => x.Registration != NormaliseRegistration(x.Registration));
        if (untidyRegistrations > 0)
            suggestions.Add(new("vehicles-registration", "low", "Normalise vehicle registrations", $"{untidyRegistrations} registration{(untidyRegistrations == 1 ? " needs" : "s need")} safe spacing/case normalisation.", "Vehicles", true));

        if (suggestions.Count == 0)
            suggestions.Add(new("ready", "info", "Plan looks ready", "No blocking planning or master-data validation issue was found by the current safety rules.", "Planner", false));

        // Costing/margin metrics remain in the DTO as zero-value compatibility fields for older portal bundles.
        return new AssistantSnapshot(
            planningDate,
            DateTimeOffset.UtcNow,
            options.IsConfigured ? "OpenAI + SLH safety rules" : "SLH safety rules",
            options.IsConfigured,
            new AssistantMetrics(
                orders.Count,
                unplanned.Count,
                loads.Count,
                unallocated.Count,
                drivers.Count,
                vehicles.Count,
                vehicleRisk,
                0,
                0,
                emptyMiles,
                missingMapPoints,
                duplicateSiteGroups.Count),
            suggestions);
    }

    public async Task<AssistantAdvice> Advise(DateOnly planningDate, string message, string userKey, CancellationToken ct)
    {
        var snapshot = await GetSnapshot(planningDate, ct);
        var fallback = RuleBasedAnswer(message, snapshot);
        if (!options.IsConfigured) return new AssistantAdvice(fallback, snapshot.Source, snapshot.Suggestions);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            request.Content = JsonContent.Create(new
            {
                model = options.Model,
                store = false,
                safety_identifier = SafetyIdentifier(userKey),
                reasoning = new { effort = "low" },
                text = new { verbosity = "low" },
                instructions = "You are the SLH transport planning assistant. Give concise, practical UK road-haulage advice using only the supplied operational snapshot. Never claim to change data. Never recommend bypassing legal driver-hours, vehicle compliance, staging review, or human approval. Do not discuss rates, margins or costings. Put safety and compliance first. If data is missing, say exactly what a planner should verify.",
                input = $"Planner question: {message.Trim()}\nOperational snapshot JSON: {JsonSerializer.Serialize(snapshot)}"
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 60)));
            using var response = await httpClient.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var answer = document.RootElement.TryGetProperty("output", out var output)
                ? string.Join("\n", output.EnumerateArray()
                    .SelectMany(item => item.TryGetProperty("content", out var content) ? content.EnumerateArray() : [])
                    .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "output_text")
                    .Select(item => item.GetProperty("text").GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                : string.Empty;
            return new AssistantAdvice(string.IsNullOrWhiteSpace(answer) ? fallback : answer, snapshot.Source, snapshot.Suggestions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "OpenAI planning advice was unavailable; returning deterministic SLH guidance.");
            return new AssistantAdvice(fallback, "SLH safety rules (AI temporarily unavailable)", snapshot.Suggestions);
        }
    }

    public static string NormaliseRegistration(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private async Task<List<TransportOrder>> ReadOrders(DateOnly date, CancellationToken ct)
    {
        var primary = await SafeRead(db.TransportOrders.AsNoTracking()
            .Where(x => x.CollectionDate == date && x.Status != OrderStatus.Cancelled)
            .OrderBy(x => x.Reference).Take(500), "orders", ct);
        if (primary.Count > 0) return primary;
        try { return await PlanningRegisterStore.ReadOrdersAsync(db, date, date, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant planning-register order fallback was unavailable.");
            return [];
        }
    }

    private async Task<List<Load>> ReadLoads(DateOnly date, CancellationToken ct)
    {
        var primary = await SafeRead(db.Loads.AsNoTracking().Include(x => x.Stops)
            .Where(x => x.PlanningDate == date && x.Status != LoadStatus.Cancelled)
            .OrderBy(x => x.Reference).Take(500), "loads", ct);
        if (primary.Count > 0) return primary;
        try { return await PlanningRegisterStore.ReadLoadsAsync(db, date, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant planning-register load fallback was unavailable.");
            return [];
        }
    }

    private async Task SafeEnrichSites(List<Site> sites, CancellationToken ct)
    {
        if (sites.Count == 0) return;
        try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant site-detail enrichment was unavailable; base site records will be used.");
        }
    }

    private async Task<(int SitesMissingGeofence, bool AutoFixAvailable)> GeofenceLinkGaps(CancellationToken ct)
    {
        try
        {
            var statuses = await SiteGeofenceMasterSync.GetStatusAsync(db, ct);
            var missing = statuses.Count(status => status.NeedsReview);
            return (missing, missing > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant geofence link scan was unavailable.");
            db.ChangeTracker.Clear();
            return (0, false);
        }
    }

    private async Task<List<T>> SafeRead<T>(IQueryable<T> query, string area, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant skipped unavailable {Area} data while building its snapshot.", area);
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private static List<List<Site>> FindDuplicateSiteGroups(IReadOnlyCollection<Site> sites)
    {
        var pairs = new Dictionary<string, HashSet<Site>>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            var name = NormaliseSiteValue(site.Name);
            var address = NormaliseSiteValue(site.CollectionAddress);
            foreach (var key in new[] { name.Length >= 5 ? $"name:{name}" : "", address.Length >= 8 ? $"address:{address}" : "" }.Where(value => value.Length > 0))
            {
                if (!pairs.TryGetValue(key, out var group)) pairs[key] = group = [];
                group.Add(site);
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return pairs.Values
            .Where(group => group.Count > 1)
            .Select(group => group.OrderBy(site => site.ExternalCode).ToList())
            .Where(group => seen.Add(string.Join('|', group.Select(site => site.Id).OrderBy(id => id))))
            .ToList();
    }

    private static string NormaliseSiteValue(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string SafetyIdentifier(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];

    private static string RuleBasedAnswer(string message, AssistantSnapshot snapshot)
    {
        var priorities = snapshot.Suggestions.Where(x => x.Severity is "high" or "medium").Take(4).ToList();
        var prefix = message.Contains("ready", StringComparison.OrdinalIgnoreCase) || message.Contains("today", StringComparison.OrdinalIgnoreCase)
            ? $"For {snapshot.PlanningDate:dd MMM}, there are {snapshot.Metrics.UnplannedOrders} unplanned orders and {snapshot.Metrics.UnallocatedLoads} incomplete run allocations."
            : "Here is the safest operational priority order from the live TMS data.";
        return priorities.Count == 0
            ? $"{prefix} No blocking issue is currently flagged. Refresh live tracking and confirm late changes before dispatch."
            : $"{prefix} {string.Join(" ", priorities.Select((item, index) => $"{index + 1}) {item.Title}: {item.Detail}"))}";
    }
}

public sealed record AssistantSuggestion(string Id, string Severity, string Title, string Detail, string Area, bool AutoFixAvailable);
public sealed record AssistantMetrics(int Orders, int UnplannedOrders, int Loads, int UnallocatedLoads, int ActiveDrivers, int ActiveVehicles, int VehicleComplianceRisks, int UnpricedLoads, int NegativeMarginLoads, decimal EmptyMiles, int MissingSiteMapPoints, int DuplicateSiteGroups);
public sealed record AssistantSnapshot(DateOnly PlanningDate, DateTimeOffset GeneratedAtUtc, string Source, bool AiConfigured, AssistantMetrics Metrics, IReadOnlyList<AssistantSuggestion> Suggestions);
public sealed record AssistantAdvice(string Answer, string Source, IReadOnlyList<AssistantSuggestion> Suggestions);
public sealed record SafeFixResult(int Applied, int Skipped, IReadOnlyList<string> Changes, IReadOnlyList<string> SkippedReasons);
