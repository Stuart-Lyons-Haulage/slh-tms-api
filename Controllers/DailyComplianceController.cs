using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/daily-compliance")]
[Authorize]
public sealed class DailyComplianceController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    TachoMasterOptions tachoOptions,
    FleetioOptions fleetioOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<DailyComplianceController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [HttpGet("report")]
    public async Task<IActionResult> Report([FromQuery] DateOnly date, CancellationToken ct)
        => Ok(await BuildReport(date, ct));

    [HttpGet("export.csv")]
    public async Task<IActionResult> Export([FromQuery] DateOnly date, CancellationToken ct)
    {
        var report = await BuildReport(date, ct);
        var csv = new StringBuilder();
        csv.AppendLine("Date,Asset Type,Asset,Run,Driver,Employment Type,Tacho Duty Start,Pre-use Other Work Minutes,First Movement,Fleetio Form,Fleetio Submitted,Fleetio User,Fleetio Driver Match,Failed Items,Status,Reason");
        foreach (var row in report.Rows)
        {
            csv.AppendLine(string.Join(',', new[]
            {
                report.Date.ToString("yyyy-MM-dd"), row.AssetType, row.AssetName, row.RunReference, row.DriverName,
                row.EmploymentType, row.TachoDutyStartUtc?.ToString("O") ?? string.Empty,
                row.TachoPreUseOtherWorkMinutes?.ToString() ?? string.Empty, row.FirstMovementUtc?.ToString("O") ?? string.Empty,
                row.FleetioForm ?? string.Empty, row.FleetioSubmittedAtUtc?.ToString("O") ?? string.Empty,
                row.FleetioUser ?? string.Empty, row.FleetioDriverMatched?.ToString() ?? string.Empty,
                row.FleetioFailedItems?.ToString() ?? string.Empty, row.Status, row.Reason
            }.Select(Csv)));
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"daily-compliance-{date:yyyy-MM-dd}.csv");
    }

    private async Task<ComplianceReport> BuildReport(DateOnly date, CancellationToken ct)
    {
        var loads = await ReadLoads(date, ct);
        var drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.DisplayName).ToListAsync(ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Registration).ToListAsync(ct);
        var trailers = await db.Trailers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.TrailerNumber).ToListAsync(ct);
        var mappings = await SafeMappings(ct);

        IReadOnlyList<TachoDriverDutyStatus> duties = [];
        string? tachoError = null;
        try { duties = await tachoMaster.GetDriverDutyStatusesAsync(date, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            tachoError = ex.GetBaseException().Message;
            logger.LogWarning(ex, "Daily compliance TachoMaster duty read failed for {Date}", date);
        }

        var startLocal = date.ToDateTime(TimeOnly.MinValue);
        var endLocal = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var dayStartUtc = new DateTimeOffset(startLocal, London.GetUtcOffset(startLocal)).ToUniversalTime();
        var dayEndUtc = new DateTimeOffset(endLocal, London.GetUtcOffset(endLocal)).ToUniversalTime();
        var tracking = await db.VehicleTrackingEvents.AsNoTracking()
            .Where(x => x.EventTimeUtc >= dayStartUtc && x.EventTimeUtc < dayEndUtc)
            .OrderBy(x => x.EventTimeUtc)
            .ToListAsync(ct);

        var preUse = tachoOptions.IsConfigured ? await ReadTachoPreUseAsync(date, ct) : [];
        var fleetioCache = new Dictionary<string, IReadOnlyList<FleetioInspectionEvidence>>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ComplianceRow>();

        foreach (var load in loads.Where(x => x.DriverId != null && x.VehicleId != null).OrderBy(x => x.Reference))
        {
            var driver = drivers.FirstOrDefault(x => x.Id == load.DriverId);
            var vehicle = vehicles.FirstOrDefault(x => x.Id == load.VehicleId);
            if (driver is null || vehicle is null) continue;

            var duty = FindDuty(duties, driver, vehicle);
            var firstMovement = FirstMovement(tracking, vehicle, duty?.DutyStartUtc, dayEndUtc);
            var preUseMinutes = duty is null ? null : FindPreUse(preUse, duty, vehicle)?.PreDriveOtherWorkMinutes;
            var employment = EmploymentType(driver);
            var vehicleFleetioId = !string.IsNullOrWhiteSpace(vehicle.FleetioId) ? vehicle.FleetioId : MappingExternalKey(mappings, "Vehicle", vehicle.Id);
            var vehicleInspection = await InspectionFor(vehicleFleetioId, driver, duty?.DutyStartUtc, firstMovement, dayStartUtc, dayEndUtc, fleetioCache, ct);
            rows.Add(BuildRow("Vehicle", vehicle.Id, vehicle.Registration, load.Reference, driver, employment, duty, firstMovement, preUseMinutes, vehicleInspection));

            if (load.TrailerId is Guid trailerId)
            {
                var trailer = trailers.FirstOrDefault(x => x.Id == trailerId);
                if (trailer is not null)
                {
                    var trailerFleetioId = MappingExternalKey(mappings, "Trailer", trailer.Id);
                    var trailerInspection = await InspectionFor(trailerFleetioId, driver, duty?.DutyStartUtc, firstMovement, dayStartUtc, dayEndUtc, fleetioCache, ct);
                    rows.Add(BuildRow("Trailer", trailer.Id, trailer.TrailerNumber, load.Reference, driver, employment, duty, firstMovement, preUseMinutes, trailerInspection));
                }
            }
        }

        foreach (var duty in duties)
        {
            var vehicle = vehicles.FirstOrDefault(v => VehicleKeys(v).Contains(Normalise(duty.VehicleCode), StringComparer.OrdinalIgnoreCase));
            if (vehicle is null) continue;
            var driver = drivers.FirstOrDefault(d => DriverMatches(d, duty));
            if (driver is null) continue;
            if (rows.Any(row => row.AssetType == "Vehicle" && row.AssetId == vehicle.Id && row.DriverId == driver.Id && row.TachoDutyStartUtc is not null && Math.Abs((row.TachoDutyStartUtc.Value - duty.DutyStartUtc).TotalMinutes) <= 5)) continue;

            var firstMovement = FirstMovement(tracking, vehicle, duty.DutyStartUtc, dayEndUtc);
            var preUseMinutes = FindPreUse(preUse, duty, vehicle)?.PreDriveOtherWorkMinutes;
            var fleetioId = !string.IsNullOrWhiteSpace(vehicle.FleetioId) ? vehicle.FleetioId : MappingExternalKey(mappings, "Vehicle", vehicle.Id);
            var inspection = await InspectionFor(fleetioId, driver, duty.DutyStartUtc, firstMovement, dayStartUtc, dayEndUtc, fleetioCache, ct);
            rows.Add(BuildRow("Vehicle", vehicle.Id, vehicle.Registration, "Unplanned vehicle use", driver, EmploymentType(driver), duty, firstMovement, preUseMinutes, inspection));
        }

        rows = rows
            .GroupBy(x => $"{x.AssetType}|{x.AssetId}|{x.DriverId}|{x.TachoDutyStartUtc:O}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(row => row.RunReference).First())
            .OrderBy(row => row.Status == "Non-compliant" ? 0 : row.Status == "Review" ? 1 : row.Status == "Paper evidence required" ? 2 : 3)
            .ThenBy(row => row.AssetType)
            .ThenBy(row => row.AssetName)
            .ToList();

        return new ComplianceReport(
            date,
            DateTimeOffset.UtcNow,
            new CompliancePolicy(15, true, true, true,
                "SLH policy requires a fresh Fleetio pre-use walkround whenever a new driver takes control of a vehicle or trailer. Agency paper checks remain visible as an exception until Fleetio adoption."),
            new SourceStatus(
                tachoError is null ? "Available" : $"Partial: {tachoError}",
                fleetioOptions.IsConfigured ? "Available" : "Not configured",
                "Available from stored DOT/Falcon movement",
                "Available from TMS planning register"),
            new ComplianceSummary(
                rows.Count,
                rows.Count(x => x.Status == "Compliant"),
                rows.Count(x => x.Status is "Paper evidence required" or "Review"),
                rows.Count(x => x.Status == "Non-compliant"),
                rows.Count(x => x.AssetType == "Vehicle"),
                rows.Count(x => x.AssetType == "Trailer"),
                rows.Count(x => x.FleetioInspectionId is not null),
                rows.Count(x => x.TachoPreUseOtherWorkMinutes >= 15)),
            rows);
    }

    private ComplianceRow BuildRow(string assetType, Guid assetId, string assetName, string runReference, Driver driver, string employment,
        TachoDriverDutyStatus? duty, DateTimeOffset? firstMovement, int? preUseMinutes, FleetioInspectionMatch inspection)
    {
        var tachoOk = preUseMinutes >= 15;
        var fleetioOk = inspection.Evidence is not null && inspection.DriverMatched;
        var status = fleetioOk && tachoOk
            ? "Compliant"
            : employment == "Agency" && !fleetioOk
                ? "Paper evidence required"
                : fleetioOk || tachoOk ? "Review" : "Non-compliant";

        var reason = status switch
        {
            "Compliant" => "Fleetio pre-use walkround and at least 15 minutes Tacho other-work before first drive are confirmed.",
            "Paper evidence required" => "Agency driver has no matching Fleetio walkround. Verify the paper check and keep the driver visible for Fleetio adoption.",
            "Review" when !fleetioOk && inspection.Evidence is not null => "A Fleetio walkround exists in the pre-use window, but it was not submitted by the matched driver.",
            "Review" when !fleetioOk => "Fleetio walkround is missing for this driver/asset handover.",
            "Review" => "Fleetio is confirmed but Tacho does not show 15 minutes of pre-drive other work.",
            _ => "Fleetio walkround is missing and Tacho pre-drive other work is below the 15 minute SLH standard."
        };

        return new ComplianceRow(assetType, assetId, assetName, runReference, driver.Id, driver.DisplayName, employment,
            duty?.DutyStartUtc, preUseMinutes, firstMovement, inspection.Evidence?.Id, inspection.Evidence?.Form,
            inspection.Evidence?.SubmittedAtUtc, inspection.Evidence?.User, inspection.DriverMatched,
            inspection.Evidence?.FailedItems, status, reason);
    }

    private async Task<List<Load>> ReadLoads(DateOnly date, CancellationToken ct)
    {
        try { return await db.Loads.AsNoTracking().Where(x => x.PlanningDate == date && x.Status != LoadStatus.Cancelled).ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Dedicated Loads table unavailable for daily compliance; using planning register.");
            db.ChangeTracker.Clear();
            return (await PlanningRegisterStore.ReadLoadsAsync(db, date, ct)).Where(x => x.Status != LoadStatus.Cancelled).ToList();
        }
    }

    private async Task<List<IntegrationMapping>> SafeMappings(CancellationToken ct)
    {
        try { return await db.IntegrationMappings.AsNoTracking().Where(x => x.Active && x.Provider == "Fleetio").ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fleetio mappings unavailable for daily compliance.");
            return [];
        }
    }

    private async Task<FleetioInspectionMatch> InspectionFor(string? fleetioId, Driver driver, DateTimeOffset? dutyStartUtc,
        DateTimeOffset? firstMovementUtc, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc,
        Dictionary<string, IReadOnlyList<FleetioInspectionEvidence>> cache, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fleetioId) || !fleetioOptions.IsConfigured) return new(null, false);
        if (!cache.TryGetValue(fleetioId, out var inspections))
        {
            inspections = await ReadFleetioInspections(fleetioId, dayStartUtc, dayEndUtc, ct);
            cache[fleetioId] = inspections;
        }

        var windowStart = dutyStartUtc?.AddMinutes(-30) ?? dayStartUtc;
        var windowEnd = firstMovementUtc?.AddMinutes(15) ?? dutyStartUtc?.AddHours(2) ?? dayEndUtc;
        var candidates = inspections.Where(x => x.SubmittedAtUtc >= windowStart && x.SubmittedAtUtc <= windowEnd).OrderBy(x => x.SubmittedAtUtc).ToList();
        var matched = candidates.FirstOrDefault(x => UserMatches(x.User, x.UserEmployeeNumber, driver));
        return matched is not null ? new(matched, true) : new(candidates.FirstOrDefault(), false);
    }

    private async Task<IReadOnlyList<FleetioInspectionEvidence>> ReadFleetioInspections(string fleetioId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        try
        {
            var http = httpClientFactory.CreateClient();
            var result = new List<FleetioInspectionEvidence>();
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);

            for (var page = 0; page < 100; page++)
            {
                var uri = $"{fleetioOptions.BaseUrl.TrimEnd('/')}/submitted_inspection_forms?filter[vehicle_id][eq]={Uri.EscapeDataString(fleetioId)}&filter[submitted_at][gte]={Uri.EscapeDataString(fromUtc.ToString("O"))}&filter[submitted_at][lt]={Uri.EscapeDataString(toUtc.ToString("O"))}&sort[submitted_at]=desc&per_page=100";
                if (!string.IsNullOrWhiteSpace(cursor)) uri += $"&start_cursor={Uri.EscapeDataString(cursor)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Authorization", $"Token {fleetioOptions.ApiKey}");
                request.Headers.TryAddWithoutValidation("Account-Token", fleetioOptions.AccountToken);
                request.Headers.TryAddWithoutValidation("X-Api-Version", fleetioOptions.ApiVersion);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Fleetio submitted inspections returned {StatusCode} for asset {FleetioId}.", response.StatusCode, fleetioId);
                    break;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = document.RootElement;
                IEnumerable<JsonElement> records = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : Try(root, "records", out var recordsElement) && recordsElement.ValueKind == JsonValueKind.Array
                        ? recordsElement.EnumerateArray()
                        : Try(root, "data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array
                            ? dataElement.EnumerateArray()
                            : Try(root, "results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array
                                ? resultsElement.EnumerateArray()
                                : [];

                result.AddRange(records.Select(item => new FleetioInspectionEvidence(
                    Text(item, "id") ?? string.Empty,
                    NestedText(item, "inspection_form", "title") ?? Text(item, "inspection_form_title") ?? "Pre-use inspection",
                    Date(item, "submitted_at", "created_at") ?? DateTimeOffset.MinValue,
                    NestedText(item, "user", "name") ?? Text(item, "user"),
                    NestedText(item, "user", "employee_number"),
                    Int(item, "failed_items")))
                    .Where(item => item.SubmittedAtUtc >= fromUtc && item.SubmittedAtUtc < toUtc));

                var nextCursor = root.ValueKind == JsonValueKind.Object ? Text(root, "next_cursor", "nextCursor") : null;
                if (string.IsNullOrWhiteSpace(nextCursor) || !seenCursors.Add(nextCursor)) break;
                cursor = nextCursor;
            }

            return result
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.SubmittedAtUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fleetio inspection evidence unavailable for asset {FleetioId}.", fleetioId);
            return [];
        }
    }

    private async Task<List<PreUseEvidence>> ReadTachoPreUseAsync(DateOnly date, CancellationToken ct)
    {
        var result = new List<PreUseEvidence>();
        try
        {
            var http = httpClientFactory.CreateClient();
            http.BaseAddress = new Uri(NormaliseBaseUrl(tachoOptions.BaseUrl));
            var sid = await LoginTacho(http, ct);
            var offset = 0;
            for (var page = 0; page < tachoOptions.MaxPages; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "Duty/GetDutyTransactions");
                request.Headers.Add("APIKEY", tachoOptions.ApiKey);
                request.Headers.Add("SID", sid);
                request.Content = JsonContent.Create(new
                {
                    From = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("O"),
                    To = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("O"),
                    Offset = offset,
                    WithWtd = true
                });
                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = document.RootElement;
                var dutyPage = Try(root, "DutyNew", out var dutyNew) ? dutyNew : root;
                var records = Try(dutyPage, "Data", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToList() : [];

                foreach (var duty in records)
                {
                    var vehicleCode = Normalise(Text(duty, "VehCode") ?? string.Empty);
                    var memberCode = Int(duty, "MemCode") ?? 0;
                    var dutyStart = Date(duty, "DutyStart") ?? DateTimeOffset.MinValue;
                    if (vehicleCode.Length == 0 || memberCode == 0 || dutyStart == DateTimeOffset.MinValue) continue;
                    var segments = Try(duty, "Wtd", out var wtd) && wtd.ValueKind == JsonValueKind.Array
                        ? wtd.EnumerateArray().Select(item => new ActivitySegment(Text(item, "WtdEvent") ?? string.Empty, Date(item, "TimeStart"), Date(item, "TimeEnd")))
                            .Where(item => item.StartUtc is not null && item.EndUtc is not null && item.EndUtc >= item.StartUtc).OrderBy(item => item.StartUtc).ToList()
                        : [];
                    var firstDrive = segments.Where(item => item.Kind.Contains("drive", StringComparison.OrdinalIgnoreCase)).Select(item => item.StartUtc).FirstOrDefault();
                    var cutOff = firstDrive ?? segments.Select(item => item.EndUtc).Where(item => item is not null).Max();
                    var otherWorkMinutes = cutOff is null ? 0 : segments
                        .Where(item => item.StartUtc < cutOff && item.Kind.Contains("work", StringComparison.OrdinalIgnoreCase))
                        .Sum(item => (int)Math.Max(0, Math.Round((item.EndUtc!.Value - item.StartUtc!.Value).TotalMinutes)));
                    result.Add(new PreUseEvidence(vehicleCode, memberCode, dutyStart, firstDrive, otherWorkMinutes));
                }

                var moreData = Try(dutyPage, "MoreData", out var more) && more.ValueKind == JsonValueKind.True;
                var recordCount = Try(dutyPage, "RecordCount", out var countElement) && countElement.TryGetInt32(out var count) ? count : records.Count;
                if (!moreData || recordCount == 0) break;
                offset += recordCount;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Detailed Tacho pre-use activity unavailable for {Date}.", date);
        }
        return result;
    }

    private async Task<string> LoginTacho(HttpClient http, CancellationToken ct)
    {
        foreach (var password in PasswordAttempts(tachoOptions.Password))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/login");
            request.Headers.Add("APIKEY", tachoOptions.ApiKey);
            request.Content = JsonContent.Create(new { User = tachoOptions.Username, Pass = password, OsVersion = "1.0", OsName = "Azure Container App", PcName = Environment.MachineName, AuthType = "password" });
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) continue;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            if (root.ValueKind is JsonValueKind.String or JsonValueKind.Number) return root.ToString();
            foreach (var name in new[] { "sid", "SID", "token", "Token" })
                if (Try(root, name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return value.ToString();
        }
        throw new HttpRequestException("TachoMaster login failed while reading pre-use activity.");
    }

    private static TachoDriverDutyStatus? FindDuty(IEnumerable<TachoDriverDutyStatus> duties, Driver driver, Vehicle vehicle)
        => duties.Where(duty => DriverMatches(driver, duty) || VehicleKeys(vehicle).Contains(Normalise(duty.VehicleCode), StringComparer.OrdinalIgnoreCase)).OrderBy(duty => duty.DutyStartUtc).FirstOrDefault();

    private static PreUseEvidence? FindPreUse(IEnumerable<PreUseEvidence> items, TachoDriverDutyStatus duty, Vehicle vehicle)
        => items.Where(item => item.MemberCode == duty.MemberCode)
            .Where(item => VehicleKeys(vehicle).Contains(item.VehicleCode, StringComparer.OrdinalIgnoreCase))
            .OrderBy(item => Math.Abs((item.DutyStartUtc - duty.DutyStartUtc).TotalMinutes))
            .FirstOrDefault(item => Math.Abs((item.DutyStartUtc - duty.DutyStartUtc).TotalMinutes) <= 5);

    private static DateTimeOffset? FirstMovement(IEnumerable<VehicleTrackingEvent> tracking, Vehicle vehicle, DateTimeOffset? dutyStart, DateTimeOffset dayEnd)
    {
        var ids = VehicleKeys(vehicle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var from = dutyStart ?? dayEnd.AddDays(-1);
        return tracking.Where(item => item.EventTimeUtc >= from && item.EventTimeUtc < dayEnd)
            .Where(item => ids.Contains(Normalise(item.VehicleIdentifier)))
            .Where(item => item.IsMoving == true || item.SpeedKph > 0)
            .Select(item => (DateTimeOffset?)item.EventTimeUtc).FirstOrDefault();
    }

    private static bool DriverMatches(Driver driver, TachoDriverDutyStatus duty)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var memberCode) && memberCode > 0 && memberCode == duty.MemberCode) return true;
        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber) && !string.IsNullOrWhiteSpace(duty.EmployeeNumber) && string.Equals(Normalise(driver.EmployeeNumber), Normalise(duty.EmployeeNumber), StringComparison.OrdinalIgnoreCase)) return true;
        var names = new[] { driver.DisplayName, driver.TachoName }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => Normalise(value!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains(Normalise(duty.DriverName));
    }

    private static bool UserMatches(string? fleetioUser, string? fleetioEmployeeNumber, Driver driver)
    {
        if (!string.IsNullOrWhiteSpace(fleetioEmployeeNumber) && !string.IsNullOrWhiteSpace(driver.EmployeeNumber) &&
            string.Equals(Normalise(fleetioEmployeeNumber), Normalise(driver.EmployeeNumber), StringComparison.OrdinalIgnoreCase))
            return true;

        var user = Normalise(fleetioUser ?? string.Empty);
        if (user.Length == 0) return false;
        return new[] { driver.DisplayName, driver.TachoName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Normalise(value!))
            .Where(value => value.Length > 0)
            .Any(name => user == name || user.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(user, StringComparison.OrdinalIgnoreCase));
    }

    private static string EmploymentType(Driver driver)
    {
        if (new[] { driver.DriverType, driver.DriverGroup, driver.AgencyName }.Any(value => value?.Contains("agency", StringComparison.OrdinalIgnoreCase) == true)) return "Agency";
        if (driver.DriverType?.Contains("subcontract", StringComparison.OrdinalIgnoreCase) == true) return "Subcontractor";
        return "Employed";
    }

    private static IEnumerable<string> VehicleKeys(Vehicle vehicle)
        => new[] { vehicle.Registration, vehicle.Abbreviation, vehicle.FleetNumber }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => Normalise(value!)).Where(value => value.Length > 0);
    private static string? MappingExternalKey(IEnumerable<IntegrationMapping> mappings, string entityType, Guid entityId)
        => mappings.FirstOrDefault(mapping => mapping.TmsEntityId == entityId && mapping.TmsEntityType.Equals(entityType, StringComparison.OrdinalIgnoreCase))?.ExternalKey;
    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NormaliseBaseUrl(string value) { var trimmed = value.Trim().TrimEnd('/'); return trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? $"{trimmed}/" : $"{trimmed}/api/"; }
    private static IReadOnlyList<string> PasswordAttempts(string password) { var trimmed = password.Trim(); return trimmed.All(Uri.IsHexDigit) && trimmed.Length is 32 or 40 or 64 ? [trimmed] : [trimmed, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant()]; }
    private static bool Try(JsonElement element, string name, out JsonElement value) { if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; } value = default; return false; }
    private static string? Text(JsonElement element, params string[] names) { foreach (var name in names) if (Try(element, name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined) return value.ToString().Trim(); return null; }
    private static string? NestedText(JsonElement element, string parent, string child) => Try(element, parent, out var nested) && nested.ValueKind == JsonValueKind.Object ? Text(nested, child) : null;
    private static DateTimeOffset? Date(JsonElement element, params string[] names) => DateTimeOffset.TryParse(Text(element, names), out var value) ? value : null;
    private static int? Int(JsonElement element, params string[] names) => int.TryParse(Text(element, names), out var value) ? value : null;
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record FleetioInspectionEvidence(string Id, string Form, DateTimeOffset SubmittedAtUtc, string? User, string? UserEmployeeNumber, int? FailedItems);
    private sealed record FleetioInspectionMatch(FleetioInspectionEvidence? Evidence, bool DriverMatched);
    private sealed record ActivitySegment(string Kind, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc);
    private sealed record PreUseEvidence(string VehicleCode, int MemberCode, DateTimeOffset DutyStartUtc, DateTimeOffset? FirstDriveUtc, int PreDriveOtherWorkMinutes);
    public sealed record CompliancePolicy(int MinimumPreUseOtherWorkMinutes, bool EmployedFleetioMandatory, bool AgencyPaperException, bool DriverChangeRequiresNewCheck, string Note);
    public sealed record SourceStatus(string TachoMaster, string Fleetio, string DotFalcon, string Tms);
    public sealed record ComplianceSummary(int AssetsOperated, int Green, int Amber, int Red, int Vehicles, int Trailers, int FleetioChecks, int TachoPreUseConfirmed);
    public sealed record ComplianceRow(string AssetType, Guid AssetId, string AssetName, string RunReference, Guid DriverId, string DriverName, string EmploymentType,
        DateTimeOffset? TachoDutyStartUtc, int? TachoPreUseOtherWorkMinutes, DateTimeOffset? FirstMovementUtc, string? FleetioInspectionId, string? FleetioForm,
        DateTimeOffset? FleetioSubmittedAtUtc, string? FleetioUser, bool? FleetioDriverMatched, int? FleetioFailedItems, string Status, string Reason);
    public sealed record ComplianceReport(DateOnly Date, DateTimeOffset GeneratedAtUtc, CompliancePolicy Policy, SourceStatus SourceStatus, ComplianceSummary Summary, IReadOnlyList<ComplianceRow> Rows);
}
