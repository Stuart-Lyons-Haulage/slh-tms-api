using System.Text;
using System.Text.Json;
using ExcelDataReader;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/master-data/workbook")]
public sealed class MasterDataWorkbookImportController(TmsDbContext db, StagingService staging, ILogger<MasterDataWorkbookImportController> logger) : ControllerBase
{
    [HttpPost("preview")]
    [RequestSizeLimit(30_000_000)]
    public async Task<IActionResult> Preview([FromForm] IFormFile file, CancellationToken ct)
    {
        return Ok(await ProcessAsync(file, commit: false, ct));
    }

    [HttpPost("commit")]
    [RequestSizeLimit(30_000_000)]
    public async Task<IActionResult> Commit([FromForm] IFormFile file, CancellationToken ct)
    {
        return Ok(await ProcessAsync(file, commit: true, ct));
    }

    private async Task<WorkbookImportResult> ProcessAsync(IFormFile file, bool commit, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new InvalidOperationException("Upload a populated master-data workbook.");

        var workbook = await ReadWorkbookAsync(file, ct);
        var result = new WorkbookImportResult(commit ? "commit" : "preview");

        await ProcessSitesAsync(workbook, result, commit, ct);
        await ProcessCustomerContactsAsync(workbook, result, commit, ct);
        await ProcessMarketContactsAsync(workbook, result, commit, ct);
        await ProcessSiteCutoffsAsync(workbook, result, commit, ct);
        await ProcessRunTimesAsync(workbook, result, commit, ct);
        await ProcessVehiclesAsync(workbook, result, commit, ct);
        await ProcessFuelPricesAsync(workbook, result, commit, ct);
        await ProcessDriversAsync(workbook, result, commit, ct);

        result.Warnings.Add("Drivers are update-only from this workbook. TachoMaster remains the authority for driver identity and live tacho readings.");
        result.Warnings.Add("Sites with weak or conflicting matches are held for review and are not created during commit.");
        return result;
    }

    private async Task ProcessSitesAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => SheetIs(sheet.Key, "sites")).SelectMany(sheet => sheet.Value).ToList();
        if (rows.Count == 0) return;

        var liveSites = await db.Sites.ToListAsync(ct);
        foreach (var row in rows)
        {
            var externalCode = row.Text("siteid", "site id", "sitecode", "site code", "externalcode", "external code");
            var name = row.Text("site", "sitename", "site name", "name", "delivery name", "customer delivery name");
            var driverText = row.Text("driver text name", "drivertextname", "driver name", "driver facing name");
            var address = row.Text("collection address", "collection addresses", "address", "delivery address");
            var mapLink = row.Text("map link", "maplink", "google maps", "maps");
            var aliases = row.Text("aliases", "alias", "delivery names", "collection names");
            var instructions = row.Text("collection notes", "collection instructions", "instructions", "notes");
            var active = row.Bool("active") ?? true;

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(externalCode)) continue;

            var identity = new IncomingSiteIdentity(externalCode, name, driverText, address, aliases, mapLink);
            var resolution = SiteMasterIdentityResolver.Resolve(identity, liveSites);
            var detail = new WorkbookRowResult("Sites", row.RowNumber, name ?? externalCode ?? "site", resolution.Outcome, resolution.Reason, resolution.Confidence);
            if (resolution.PossibleDuplicates.Count > 0)
                detail.RelatedRecords.AddRange(resolution.PossibleDuplicates.Select(site => $"{site.ExternalCode} - {site.Name}"));

            if (commit && resolution.Matched)
            {
                var site = resolution.Site!;
                site.CustomerCode = row.Text("customer code", "customercode") ?? site.CustomerCode;
                site.Name = string.IsNullOrWhiteSpace(name) ? site.Name : name.Trim();
                site.DriverTextName = driverText ?? site.DriverTextName;
                site.CollectionAddress = address ?? site.CollectionAddress;
                site.CollectionInstructions = instructions ?? site.CollectionInstructions;
                site.MapLink = mapLink ?? site.MapLink;
                site.Aliases = SiteMasterIdentityResolver.MergeAliases(site.Aliases, aliases, name, driverText);
                site.Active = active;
                await MasterDetailStore.SaveAsync(db, "site", site.ExternalCode, SiteMasterIdentityResolver.ToJson(new
                {
                    externalCode = site.ExternalCode,
                    name = site.Name,
                    driverTextName = site.DriverTextName,
                    collectionAddress = site.CollectionAddress,
                    collectionInstructions = site.CollectionInstructions,
                    mapLink = site.MapLink,
                    aliases = site.Aliases,
                    active = site.Active,
                    addressCheck = row.Text("address check", "address status", "credential check"),
                    sourceWorkbookSheet = row.SheetName
                }), "SLH master workbook safe import", User.Identity?.Name, ct);
                detail.ActionTaken = "updated existing live site";
            }
            else if (commit && resolution.CanCreate)
            {
                var site = new Site
                {
                    ExternalCode = string.IsNullOrWhiteSpace(externalCode) ? $"SITE-{Guid.NewGuid():N}"[..13].ToUpperInvariant() : externalCode.Trim(),
                    Name = name!.Trim(),
                    DriverTextName = driverText,
                    CollectionAddress = address,
                    CollectionInstructions = instructions,
                    MapLink = mapLink,
                    Aliases = SiteMasterIdentityResolver.MergeAliases(aliases, name, driverText),
                    Active = active,
                    CustomerCode = row.Text("customer code", "customercode")
                };
                db.Sites.Add(site);
                liveSites.Add(site);
                await MasterDetailStore.SaveAsync(db, "site", site.ExternalCode, SiteMasterIdentityResolver.ToJson(new
                {
                    externalCode = site.ExternalCode,
                    name = site.Name,
                    driverTextName = site.DriverTextName,
                    collectionAddress = site.CollectionAddress,
                    collectionInstructions = site.CollectionInstructions,
                    mapLink = site.MapLink,
                    aliases = site.Aliases,
                    active = site.Active,
                    addressCheck = row.Text("address check", "address status", "credential check"),
                    sourceWorkbookSheet = row.SheetName
                }), "SLH master workbook safe import", User.Identity?.Name, ct);
                detail.ActionTaken = "created new site";
            }
            else if (resolution.RequiresReview)
            {
                detail.ActionTaken = commit ? "held for review - not written" : "would hold for review";
            }
            else detail.ActionTaken = commit ? "no write" : "would update/create";

            result.Rows.Add(detail);
        }

        if (commit) await db.SaveChangesAsync(ct);
    }

    private async Task ProcessRunTimesAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("run", StringComparison.OrdinalIgnoreCase) && sheet.Key.Contains("time", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        foreach (var row in rows)
        {
            var route = row.Text("alltimes", "route", "route combination", "routecombination", "run", "run name");
            if (string.IsNullOrWhiteSpace(route)) continue;
            var palletType = row.Text("pallet type", "pallettype");
            var payload = new
            {
                routeCombination = route,
                palletType,
                lastDespatch = row.Time("last despatch time", "lastdespatchtime", "last dispatch time"),
                collectFrom = row.Time("planned collect time from", "planned collect from", "collectfrom"),
                collectTo = row.Time("planned collect time to", "planned collect to", "collectto"),
                depotDeadline = row.Time("depot delivery - no later than", "depot delivery no later than", "depot deadline", "depotdelivery")
            };
            var key = $"{route}:{palletType}";
            if (commit)
                await MasterDetailStore.SaveAsync(db, "sitetimingrule", key, SiteMasterIdentityResolver.ToJson(payload), "SLH master workbook run times", User.Identity?.Name, ct);
            result.Rows.Add(new WorkbookRowResult("Run Times", row.RowNumber, route, commit ? "imported" : "ready", commit ? "Route timing rule saved to masterdetail:sitetimingrule." : "Route timing rule is ready to import.", 90) { ActionTaken = commit ? "upserted timing rule" : "would upsert timing rule" });
        }
    }

    private async Task ProcessSiteCutoffsAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("cutoff", StringComparison.OrdinalIgnoreCase) || sheet.Key.Contains("cut off", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        var liveSites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        foreach (var row in rows)
        {
            var siteCode = row.Text("siteid", "site id", "sitecode", "site code", "externalcode");
            var siteName = row.Text("site", "sitename", "site name", "name");
            var identity = new IncomingSiteIdentity(siteCode, siteName, row.Text("driver text name", "drivertextname"), row.Text("collection address", "address"), row.Text("aliases", "alias"), row.Text("map link", "maplink"));
            var resolution = SiteMasterIdentityResolver.Resolve(identity, liveSites);
            var key = $"{siteCode ?? siteName}:{row.Text("plan", "plantype", "plan type")}:{row.Text("temperature", "temp")}:{row.Text("pallet type", "pallettype")}";
            var detail = new WorkbookRowResult("Site Cutoffs", row.RowNumber, siteName ?? siteCode ?? "cutoff", resolution.Matched ? "matched" : "review", resolution.Matched ? "Cut-off matched to live Site Master." : "Cut-off could not be confidently matched to a live site; not imported on commit.", resolution.Matched ? 90 : 40);
            if (commit && resolution.Matched)
            {
                await MasterDetailStore.SaveAsync(db, "sitecutoff", key, SiteMasterIdentityResolver.ToJson(new
                {
                    siteId = resolution.Site!.ExternalCode,
                    siteName = resolution.Site.Name,
                    plan = row.Text("plan", "plan type", "plantype"),
                    standardCutoff = row.Time("standard cutoff", "standardcutoff"),
                    extendedCutoff = row.Time("extended cutoff", "extendedcutoff"),
                    contact = row.Text("contact"),
                    notes = row.Text("notes"),
                    temperature = row.Text("temperature", "temp"),
                    palletType = row.Text("pallet type", "pallettype"),
                    lastDespatch = row.Time("last despatch time", "lastdespatchtime"),
                    collectFrom = row.Time("planned collect time from", "planned collect from", "collectfrom"),
                    collectTo = row.Time("planned collect time to", "planned collect to", "collectto"),
                    depotDeadline = row.Time("depot delivery - no later than", "depot deadline", "depotdelivery")
                }), "SLH master workbook site cutoffs", User.Identity?.Name, ct);
                detail.ActionTaken = "upserted site cutoff detail";
            }
            else detail.ActionTaken = commit ? "held for review - not written" : "would import if site match is confirmed";
            result.Rows.Add(detail);
        }
    }

    private async Task ProcessVehiclesAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("vehicle", StringComparison.OrdinalIgnoreCase) || sheet.Key.Contains("fuel", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        foreach (var row in rows)
        {
            var registration = row.Text("registration", "reg", "vehicle registration");
            if (string.IsNullOrWhiteSpace(registration)) continue;
            var payload = new Dictionary<string, object?>
            {
                ["registration"] = registration.Replace(" ", string.Empty).ToUpperInvariant(),
                ["abbreviation"] = row.Text("abbreviation", "reg last 3", "last 3"),
                ["transmission"] = row.Text("transmission"),
                ["dvsCompliant"] = row.Bool("dvs"),
                ["cabMobile"] = row.Text("cab mobile", "cab phone", "cabmobile"),
                ["fuelPin"] = row.Text("fuel pin", "fuelpin"),
                ["shellCard"] = row.Text("shell card", "shellcard"),
                ["bpRedCard"] = row.Text("bp red card", "bpredcard"),
                ["bpPlainCard"] = row.Text("bp plain card", "bpplaincard"),
                ["notes"] = row.Text("notes"),
                ["active"] = row.Bool("active") ?? true
            };
            if (commit)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
                await staging.PromoteDirect("vehicle", doc.RootElement, ct);
            }
            result.Rows.Add(new WorkbookRowResult("Vehicles & Fuel", row.RowNumber, registration, commit ? "imported" : "ready", "Vehicle will upsert by normalised registration.", 95) { ActionTaken = commit ? "upserted vehicle/fuel" : "would upsert vehicle/fuel" });
        }
    }

    private async Task ProcessCustomerContactsAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("customer", StringComparison.OrdinalIgnoreCase) && sheet.Key.Contains("contact", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        foreach (var row in rows)
        {
            var email = row.Text("email", "email address");
            var name = row.Text("name", "contact", "contact name");
            var customerCode = (row.Text("customer code", "customercode") ?? row.Text("customer", "customer name") ?? "UNKNOWN").Trim().ToUpperInvariant().Replace(" ", "-");
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(email)) continue;
            var payload = new Dictionary<string, object?> { ["customerCode"] = customerCode, ["customerName"] = row.Text("customer", "customer name") ?? customerCode, ["name"] = name ?? email!, ["email"] = email, ["mobileNumber"] = row.Text("mobile", "phone", "telephone"), ["active"] = row.Bool("active") ?? true };
            if (commit)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
                await staging.PromoteDirect("customercontact", doc.RootElement, ct);
            }
            result.Rows.Add(new WorkbookRowResult("Customer Contacts", row.RowNumber, name ?? email!, commit ? "imported" : "ready", "Customer contact ready for upsert.", 85) { ActionTaken = commit ? "upserted customer contact" : "would upsert customer contact" });
        }
    }

    private async Task ProcessMarketContactsAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("market", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        foreach (var row in rows)
        {
            var market = row.Text("market", "market name") ?? "General";
            var name = row.Text("name", "seller", "seller name", "contact name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var payload = new Dictionary<string, object?> { ["market"] = market, ["name"] = name, ["standOrLocation"] = row.Text("stand", "stall", "stall number", "location"), ["salesman"] = row.Text("salesman"), ["sender"] = row.Text("sender", "email sender"), ["readOnlyMapPdfUrl"] = row.Text("map", "map pdf", "readonlymappdfurl"), ["active"] = row.Bool("active") ?? true };
            if (commit)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
                await staging.PromoteDirect("marketcontact", doc.RootElement, ct);
            }
            result.Rows.Add(new WorkbookRowResult("Market Contacts", row.RowNumber, $"{market} - {name}", commit ? "imported" : "ready", "Market contact ready for upsert.", 85) { ActionTaken = commit ? "upserted market contact" : "would upsert market contact" });
        }
    }

    private async Task ProcessFuelPricesAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("fuel price", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        foreach (var row in rows)
        {
            var provider = row.Text("provider", "supplier", "fuel provider");
            var week = row.Date("week commencing", "date", "effective date");
            var price = row.Decimal("price", "price pence per litre", "pence per litre");
            if (string.IsNullOrWhiteSpace(provider) || week is null || price is null) continue;
            var payload = new Dictionary<string, object?> { ["provider"] = provider, ["weekCommencing"] = week.Value.ToString("yyyy-MM-dd"), ["pricePencePerLitre"] = price.Value, ["isPricingMaximum"] = row.Bool("is pricing maximum", "pricing maximum", "use this max") ?? false, ["source"] = row.Text("source"), ["notes"] = row.Text("notes") };
            if (commit)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
                await staging.PromoteDirect("fuelprice", doc.RootElement, ct);
            }
            result.Rows.Add(new WorkbookRowResult("Fuel Price History", row.RowNumber, $"{provider} {week:yyyy-MM-dd}", commit ? "imported" : "ready", "Fuel price ready for provider/week upsert.", 85) { ActionTaken = commit ? "upserted fuel price" : "would upsert fuel price" });
        }
    }

    private async Task ProcessDriversAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("driver", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        if (rows.Count == 0) return;
        var drivers = await db.Drivers.ToListAsync(ct);
        foreach (var row in rows)
        {
            var tachoId = row.Text("tachomasterdriverid", "tacho master driver id", "member code", "tacho member");
            var employee = row.Text("employee number", "employee no", "driverid", "driver id", "payroll number");
            var name = row.Text("display name", "driver", "driver name", "name");
            if (string.IsNullOrWhiteSpace(tachoId) && string.IsNullOrWhiteSpace(employee) && string.IsNullOrWhiteSpace(name)) continue;

            var driver = drivers.FirstOrDefault(item => !string.IsNullOrWhiteSpace(tachoId) && string.Equals(item.TachoMasterDriverId, tachoId, StringComparison.OrdinalIgnoreCase))
                ?? drivers.FirstOrDefault(item => !string.IsNullOrWhiteSpace(employee) && string.Equals(item.EmployeeNumber, employee, StringComparison.OrdinalIgnoreCase))
                ?? drivers.FirstOrDefault(item => !string.IsNullOrWhiteSpace(name) && string.Equals(SiteMasterIdentityResolver.Normalise(item.DisplayName), SiteMasterIdentityResolver.Normalise(name), StringComparison.OrdinalIgnoreCase));

            if (driver is null)
            {
                result.Rows.Add(new WorkbookRowResult("Drivers", row.RowNumber, name ?? employee ?? tachoId!, "skipped", "No existing live driver matched. Workbook driver rows are update-only and cannot create drivers.", 30) { ActionTaken = "not written" });
                continue;
            }

            if (commit)
            {
                driver.MobileNumber = row.Text("phone number", "mobile", "mobile number") ?? driver.MobileNumber;
                driver.DriverType = row.Text("driver type", "type") ?? driver.DriverType;
                driver.DriverGroup = row.Text("driver group", "group") ?? driver.DriverGroup;
                driver.Skills = row.Text("skills", "driver skills") ?? driver.Skills;
                var payload = new
                {
                    employeeNumber = driver.EmployeeNumber,
                    displayName = driver.DisplayName,
                    phoneNumber = driver.MobileNumber,
                    email = row.Text("email", "email address"),
                    coding = row.Text("coding", "code", "driver code"),
                    driverType = driver.DriverType,
                    driverGroup = driver.DriverGroup,
                    skills = driver.Skills,
                    northEligible = row.Bool("north eligible", "northeligible"),
                    preloadEligible = row.Bool("preload eligible", "preloadeligible"),
                    notes = row.Text("notes"),
                    sourceWorkbookSheet = row.SheetName
                };
                await MasterDetailStore.SaveAsync(db, "driver", driver.EmployeeNumber, SiteMasterIdentityResolver.ToJson(payload), "SLH master workbook driver overlay", User.Identity?.Name, ct);
            }
            result.Rows.Add(new WorkbookRowResult("Drivers", row.RowNumber, driver.DisplayName, commit ? "updated" : "matched", "Matched existing live driver; operational overlay only.", 90) { ActionTaken = commit ? "updated driver overlay" : "would update driver overlay" });
        }
        if (commit) await db.SaveChangesAsync(ct);
    }

    private static async Task<Workbook> ReadWorkbookAsync(IFormFile file, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await using var stream = file.OpenReadStream();
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var sheets = new Dictionary<string, List<WorkbookRow>>(StringComparer.OrdinalIgnoreCase);
        do
        {
            var sheetName = reader.Name ?? "Sheet";
            var rawRows = new List<List<string?>>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var values = new List<string?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    values.Add(reader.GetValue(i)?.ToString()?.Trim());
                rawRows.Add(values);
            }
            var parsed = ParseRows(sheetName, rawRows);
            if (parsed.Count > 0) sheets[sheetName] = parsed;
        } while (reader.NextResult());
        return new Workbook(sheets);
    }

    private static List<WorkbookRow> ParseRows(string sheetName, List<List<string?>> rawRows)
    {
        var headerIndex = rawRows.FindIndex(row => row.Count(value => !string.IsNullOrWhiteSpace(value)) >= 2 && LooksLikeHeader(sheetName, row));
        if (headerIndex < 0) return [];
        var headers = rawRows[headerIndex].Select((value, index) => string.IsNullOrWhiteSpace(value) ? $"column{index}" : Canonical(value)).ToList();
        var rows = new List<WorkbookRow>();
        for (var r = headerIndex + 1; r < rawRows.Count; r++)
        {
            var raw = rawRows[r];
            if (raw.All(string.IsNullOrWhiteSpace)) continue;
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < Math.Min(headers.Count, raw.Count); c++)
                if (!string.IsNullOrWhiteSpace(headers[c])) values[headers[c]] = raw[c];
            rows.Add(new WorkbookRow(sheetName, r + 1, values));
        }
        return rows;
    }

    private static bool LooksLikeHeader(string sheetName, List<string?> row)
    {
        var text = string.Join("|", row.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Canonical));
        return text.Contains("siteid") || text.Contains("vehicleid") || text.Contains("registration") || text.Contains("driverid") || text.Contains("alltimes") || text.Contains("pallettype") || text.Contains("market") || text.Contains("customer") || text.Contains("provider");
    }

    private static string Canonical(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static bool SheetIs(string sheet, string expected) => Canonical(sheet) == Canonical(expected);
}

public sealed record Workbook(Dictionary<string, List<WorkbookRow>> Sheets);

public sealed record WorkbookImportResult(string Mode)
{
    public List<WorkbookRowResult> Rows { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public object Summary => Rows.GroupBy(row => row.Section).ToDictionary(group => group.Key, group => new
    {
        total = group.Count(),
        matched = group.Count(row => row.Status is "matched" or "updated"),
        imported = group.Count(row => row.Status is "imported" or "updated"),
        ready = group.Count(row => row.Status == "ready"),
        review = group.Count(row => row.Status is "review" or "conflict"),
        skipped = group.Count(row => row.Status == "skipped"),
        newRows = group.Count(row => row.Status == "new")
    });
}

public sealed record WorkbookRowResult(string Section, int RowNumber, string Key, string Status, string Reason, int Confidence)
{
    public string? ActionTaken { get; set; }
    public List<string> RelatedRecords { get; init; } = [];
}

public sealed record WorkbookRow(string SheetName, int RowNumber, Dictionary<string, string?> Values)
{
    public string? Text(params string[] names)
    {
        foreach (var name in names)
            if (Values.TryGetValue(new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()), out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return null;
    }

    public bool? Bool(params string[] names)
    {
        var value = Text(names);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out var parsed)) return parsed;
        return value.Trim().ToLowerInvariant() switch
        {
            "yes" or "y" or "1" or "active" or "ok" => true,
            "no" or "n" or "0" or "inactive" => false,
            _ => null
        };
    }

    public string? Time(params string[] names)
    {
        var value = Text(names);
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TimeOnly.TryParse(value, out var time) ? time.ToString("HH:mm:ss") : value;
    }

    public DateOnly? Date(params string[] names)
    {
        var value = Text(names);
        return DateOnly.TryParse(value, out var date) ? date : null;
    }

    public decimal? Decimal(params string[] names)
    {
        var value = Text(names);
        return decimal.TryParse(value, out var number) ? number : null;
    }
}
