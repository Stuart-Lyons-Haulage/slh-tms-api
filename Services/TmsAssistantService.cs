using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Assistant;

namespace Slh.Tms.Api.Services;

public sealed class TmsAssistantService(HttpClient httpClient, TmsDbContext db, AssistantOptions options, ILogger<TmsAssistantService> logger)
{
    public async Task<AssistantSnapshot> GetSnapshot(DateOnly planningDate, CancellationToken ct)
    {
        var orders = await SafeRead(db.TransportOrders.AsNoTracking()
            .Where(x => x.CollectionDate == planningDate && x.Status != OrderStatus.Cancelled)
            .OrderBy(x => x.Reference).Take(500), "orders", ct);
        var loads = await SafeRead(db.Loads.AsNoTracking().Include(x => x.Stops)
            .Where(x => x.PlanningDate == planningDate && x.Status != LoadStatus.Cancelled)
            .OrderBy(x => x.Reference).Take(500), "loads", ct);
        if (orders.Count == 0) orders = await PlanningRegisterStore.ReadOrdersAsync(db, planningDate, planningDate, ct);
        if (loads.Count == 0) loads = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct);
        await LoadCommercialStore.EnrichAsync(db, loads, ct);
        var drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).Take(1000).ToListAsync(ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).Take(1000).ToListAsync(ct);
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).Take(2000).ToListAsync(ct);

        var plannedOrderIds = loads.SelectMany(x => x.Stops).Where(x => x.OrderId != null).Select(x => x.OrderId!.Value).ToHashSet();
        var unplanned = orders.Where(x => !plannedOrderIds.Contains(x.Id)).ToList();
        var suggestions = new List<AssistantSuggestion>();
        var now = DateTimeOffset.UtcNow;

        if (unplanned.Count > 0)
            suggestions.Add(new("orders-unplanned", "high", "Plan approved orders", $"{unplanned.Count} approved order{(unplanned.Count == 1 ? " is" : "s are")} not yet on a load for {planningDate:dd MMM}.", "Planner", false));
        var unallocated = loads.Where(x => x.DriverId is null || x.VehicleId is null).ToList();
        if (unallocated.Count > 0)
            suggestions.Add(new("loads-unallocated", "high", "Complete load allocations", $"{unallocated.Count} load{(unallocated.Count == 1 ? " is" : "s are")} missing a driver or vehicle.", "Planner", false));
        var noMapPoints = loads.Where(x => x.Stops.Count == 0 || x.Stops.Any(stop => stop.Latitude is null || stop.Longitude is null)).ToList();
        if (noMapPoints.Count > 0)
            suggestions.Add(new("loads-unmapped", "medium", "Finish route points", $"{noMapPoints.Count} load{(noMapPoints.Count == 1 ? " has" : "s have")} missing stop coordinates, so routing and ETA checks are incomplete.", "Planner", false));
        var unpriced = loads.Count(x => x.RevenueAmount is null);
        if (unpriced > 0)
            suggestions.Add(new("loads-unpriced", "high", "Complete agreed rates", $"{unpriced} load{(unpriced == 1 ? " has" : "s have")} no revenue rate, so margin and forecast knowledge is incomplete.", "Reporting", false));
        var negativeMargin = loads.Count(x => x.RevenueAmount is not null && (x.RevenueAmount.Value + (x.FuelSurchargeAmount ?? 0)) - (x.ActualCostAmount ?? x.EstimatedCostAmount ?? 0) < 0);
        if (negativeMargin > 0)
            suggestions.Add(new("loads-negative-margin", "high", "Review loss-making loads", $"{negativeMargin} load{(negativeMargin == 1 ? " is" : "s are")} currently showing a negative margin.", "Reporting", false));
        var distance = loads.Sum(x => x.EstimatedDistanceMiles ?? 0);
        var emptyMiles = loads.Sum(x => x.EmptyMiles ?? 0);
        if (distance > 0 && emptyMiles / distance >= 0.2m)
            suggestions.Add(new("loads-empty-miles", "medium", "Reduce empty running", $"Empty mileage is {Math.Round(emptyMiles / distance * 100, 1)}% of recorded miles for the day. Review return-load suggestions and route pairing.", "Reporting", false));
        var missingDriverMobiles = drivers.Count(x => string.IsNullOrWhiteSpace(x.MobileNumber));
        if (missingDriverMobiles > 0)
            suggestions.Add(new("drivers-mobile", "medium", "Complete driver contact data", $"{missingDriverMobiles} active driver{(missingDriverMobiles == 1 ? " has" : "s have")} no dispatch mobile number.", "Drivers", false));
        var missingTachoNames = drivers.Count(x => string.IsNullOrWhiteSpace(x.TachoName));
        if (missingTachoNames > 0)
            suggestions.Add(new("drivers-tacho", "medium", "Match TachoMaster names", $"{missingTachoNames} active driver{(missingTachoNames == 1 ? " has" : "s have")} no TachoMaster matching name.", "Drivers", false));
        var licenceDue = drivers.Count(x => x.LicenceExpiry is null || x.LicenceExpiry <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));
        if (licenceDue > 0)
            suggestions.Add(new("drivers-licence", "high", "Review driver licences", $"{licenceDue} active driver licence record{(licenceDue == 1 ? " needs" : "s need")} checking or expires within 30 days.", "Drivers", false));
        var vehicleRisk = vehicles.Count(x => x.FleetioVor == true || x.FleetioMotDueUtc <= now.AddDays(30) || x.FleetioPmiDueUtc <= now.AddDays(30));
        if (vehicleRisk > 0)
            suggestions.Add(new("fleet-compliance", "high", "Protect fleet availability", $"{vehicleRisk} vehicle{(vehicleRisk == 1 ? " is" : "s are")} VOR or has MOT/PMI due within 30 days.", "Vehicles", false));
        var missingMaps = sites.Count(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && string.IsNullOrWhiteSpace(x.MapLink));
        if (missingMaps > 0)
            suggestions.Add(new("sites-map-link", "low", "Create missing site map links", $"{missingMaps} site{(missingMaps == 1 ? " has" : "s have")} an address but no driver map link. This can be fixed safely.", "Sites", true));
        var untidyRegistrations = vehicles.Count(x => x.Registration != NormaliseRegistration(x.Registration));
        if (untidyRegistrations > 0)
            suggestions.Add(new("vehicles-registration", "low", "Normalise vehicle registrations", $"{untidyRegistrations} registration{(untidyRegistrations == 1 ? " needs" : "s need")} safe spacing/case normalisation.", "Vehicles", true));

        if (suggestions.Count == 0)
            suggestions.Add(new("ready", "info", "Plan looks ready", "No blocking planning or master-data validation issue was found by the current safety rules.", "Planner", false));

        return new AssistantSnapshot(planningDate, DateTimeOffset.UtcNow, options.IsConfigured ? "OpenAI + SLH safety rules" : "SLH safety rules", options.IsConfigured,
            new AssistantMetrics(orders.Count, unplanned.Count, loads.Count, unallocated.Count, drivers.Count, vehicles.Count, vehicleRisk, unpriced, negativeMargin, emptyMiles), suggestions);
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
                instructions = "You are the SLH transport planning assistant. Give concise, practical UK road-haulage advice using only the supplied operational snapshot. Never claim to change data. Never recommend bypassing legal driver-hours, vehicle compliance, staging review, or human approval. Put safety and compliance first. If data is missing, say exactly what a planner should verify.",
                input = $"Planner question: {message.Trim()}\nOperational snapshot JSON: {JsonSerializer.Serialize(snapshot)}"
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 60)));
            using var response = await httpClient.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var answer = document.RootElement.TryGetProperty("output", out var output)
                ? string.Join("\n", output.EnumerateArray().SelectMany(item => item.TryGetProperty("content", out var content) ? content.EnumerateArray() : []).Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "output_text").Select(item => item.GetProperty("text").GetString()).Where(value => !string.IsNullOrWhiteSpace(value)))
                : string.Empty;
            return new AssistantAdvice(string.IsNullOrWhiteSpace(answer) ? fallback : answer, snapshot.Source, snapshot.Suggestions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "OpenAI planning advice was unavailable; returning deterministic SLH guidance.");
            return new AssistantAdvice(fallback, "SLH safety rules (AI temporarily unavailable)", snapshot.Suggestions);
        }
    }

    public async Task<SafeFixResult> ApplySafeFixes(CancellationToken ct)
    {
        var changes = new List<string>();
        var skipped = new List<string>();
        var vehicles = await db.Vehicles.ToListAsync(ct);
        var existingRegistrations = vehicles.GroupBy(x => NormaliseRegistration(x.Registration)).ToDictionary(x => x.Key, x => x.Count());
        foreach (var vehicle in vehicles.Where(x => x.Active))
        {
            var normalised = NormaliseRegistration(vehicle.Registration);
            if (vehicle.Registration == normalised) continue;
            if (existingRegistrations[normalised] > 1) { skipped.Add($"{vehicle.Registration}: normalised registration would conflict with another vehicle."); continue; }
            changes.Add($"Vehicle {vehicle.Registration} normalised to {normalised}.");
            vehicle.Registration = normalised;
        }

        var sites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        foreach (var site in sites.Where(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && string.IsNullOrWhiteSpace(x.MapLink)))
        {
            site.MapLink = $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(site.CollectionAddress!.Trim())}";
            changes.Add($"Created a driver map link for {site.Name}.");
        }

        var contacts = await db.CustomerContacts.Where(x => x.Active && x.Email != null).ToListAsync(ct);
        foreach (var contact in contacts)
        {
            var normalised = contact.Email!.Trim().ToLowerInvariant();
            if (normalised == contact.Email) continue;
            contact.Email = normalised;
            changes.Add($"Normalised the ETA email for {contact.CustomerCode} / {contact.Name}.");
        }

        if (changes.Count > 0)
        {
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "assistantfix",
                IdempotencyKey = $"assistantfix:{Guid.NewGuid():N}",
                PayloadJson = JsonSerializer.Serialize(new { changes, skipped, appliedAtUtc = DateTimeOffset.UtcNow }),
                Source = "SLH Assistant safe validation fixes",
                Status = StagingStatus.Promoted,
                ReviewedAtUtc = DateTimeOffset.UtcNow,
                ReviewNote = $"Applied {changes.Count} deterministic low-risk validation fixes from the portal."
            });
        }
        await db.SaveChangesAsync(ct);
        return new SafeFixResult(changes.Count, skipped.Count, changes.Take(100).ToList(), skipped.Take(100).ToList());
    }

    public static string NormaliseRegistration(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private async Task<List<T>> SafeRead<T>(IQueryable<T> query, string area, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Assistant skipped unavailable {Area} data while building its snapshot.", area);
            return [];
        }
    }
    private static string SafetyIdentifier(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];
    private static string RuleBasedAnswer(string message, AssistantSnapshot snapshot)
    {
        var priorities = snapshot.Suggestions.Where(x => x.Severity is "high" or "medium").Take(4).ToList();
        var prefix = message.Contains("ready", StringComparison.OrdinalIgnoreCase) || message.Contains("today", StringComparison.OrdinalIgnoreCase)
            ? $"For {snapshot.PlanningDate:dd MMM}, there are {snapshot.Metrics.UnplannedOrders} unplanned orders and {snapshot.Metrics.UnallocatedLoads} incomplete load allocations."
            : "Here is the safest operational priority order from the live TMS data.";
        return priorities.Count == 0 ? $"{prefix} No blocking issue is currently flagged. Refresh live tracking and confirm late changes before dispatch."
            : $"{prefix} {string.Join(" ", priorities.Select((item, index) => $"{index + 1}) {item.Title}: {item.Detail}"))}";
    }
}

public sealed record AssistantSuggestion(string Id, string Severity, string Title, string Detail, string Area, bool AutoFixAvailable);
public sealed record AssistantMetrics(int Orders, int UnplannedOrders, int Loads, int UnallocatedLoads, int ActiveDrivers, int ActiveVehicles, int VehicleComplianceRisks, int UnpricedLoads, int NegativeMarginLoads, decimal EmptyMiles);
public sealed record AssistantSnapshot(DateOnly PlanningDate, DateTimeOffset GeneratedAtUtc, string Source, bool AiConfigured, AssistantMetrics Metrics, IReadOnlyList<AssistantSuggestion> Suggestions);
public sealed record AssistantAdvice(string Answer, string Source, IReadOnlyList<AssistantSuggestion> Suggestions);
public sealed record SafeFixResult(int Applied, int Skipped, IReadOnlyList<string> Changes, IReadOnlyList<string> SkippedReasons);
