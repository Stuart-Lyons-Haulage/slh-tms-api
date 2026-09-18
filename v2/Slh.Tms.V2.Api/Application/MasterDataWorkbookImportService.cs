using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Data;
using Slh.Tms.V2.Api.Domain;

namespace Slh.Tms.V2.Api.Application;

public sealed record MasterWorkbookImportResult(
    bool Committed,
    IReadOnlyDictionary<string, int> Rows,
    IReadOnlyList<string> Issues);

public sealed class MasterDataWorkbookImportService(MasterDataDbContext db)
{
    private readonly List<string> _issues = [];

    public async Task<MasterWorkbookImportResult> ImportAsync(
        Stream input,
        bool commit,
        CancellationToken ct)
    {
        using var workbook = new XLWorkbook(input);
        var rows = ExpectedSheets.ToDictionary(
            sheet => sheet,
            sheet => CountRows(workbook, sheet),
            StringComparer.OrdinalIgnoreCase);

        ValidateWorkbook(workbook);

        if (!commit)
            return new(false, rows, _issues);

        var customersByCode = await db.Customers.ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, ct);
        var customersByName = await db.Customers.ToDictionaryAsync(x => x.Name, StringComparer.OrdinalIgnoreCase, ct);
        var sitesByCode = await db.Sites.ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, ct);
        var driversByEmployee = (await db.Drivers.Where(x => x.EmployeeNumber != null).ToListAsync(ct))
            .ToDictionary(x => x.EmployeeNumber!, StringComparer.OrdinalIgnoreCase);
        var vehiclesByReg = await db.Vehicles.ToDictionaryAsync(x => x.Registration, StringComparer.OrdinalIgnoreCase, ct);
        var contactsByCode = await db.CustomerContacts.ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, ct);
        var marketContactsByKey = await db.MarketContacts.ToDictionaryAsync(x => x.Key, StringComparer.OrdinalIgnoreCase, ct);
        var cutoffsByCode = await db.SiteCutoffs.ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, ct);
        var timingsByKey = await db.RouteTimings.ToDictionaryAsync(x => x.Key, StringComparer.OrdinalIgnoreCase, ct);
        var fuelByCode = await db.FuelPrices.ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, ct);
        var aliasCandidates = (await db.SiteAliasCandidates.ToListAsync(ct))
            .ToDictionary(x => $"{x.AliasType}|{x.Alias}", StringComparer.OrdinalIgnoreCase);
        var siteAliases = (await db.SiteAliases.ToListAsync(ct))
            .ToDictionary(x => $"{x.SiteId}|{x.Alias}", StringComparer.OrdinalIgnoreCase);

        ImportCustomerContacts(workbook, customersByCode, customersByName, contactsByCode);
        ImportSites(workbook, customersByCode, sitesByCode, siteAliases);
        ImportDrivers(workbook, driversByEmployee);
        ImportVehicles(workbook, vehiclesByReg);
        ImportSiteCutoffs(workbook, sitesByCode, cutoffsByCode);
        ImportRouteTimings(workbook, timingsByKey);
        ImportMarketContacts(workbook, marketContactsByKey);
        ImportAliasCandidates(workbook, "Delivery Aliases", "Delivery", aliasCandidates);
        ImportAliasCandidates(workbook, "Collection Aliases", "Collection", aliasCandidates);
        ImportFuelPrices(workbook, fuelByCode);

        await db.SaveChangesAsync(ct);
        return new(true, rows, _issues);
    }

    private static readonly string[] ExpectedSheets =
    [
        "Sites",
        "Site Cutoffs",
        "Run Times",
        "Vehicles & Fuel",
        "Drivers",
        "Customer Contacts",
        "Market Contacts",
        "Delivery Aliases",
        "Collection Aliases",
        "Fuel Price History"
    ];

    private static int CountRows(XLWorkbook workbook, string sheetName)
    {
        if (!workbook.Worksheets.TryGetWorksheet(sheetName, out var ws))
            return 0;

        var used = ws.RangeUsed();
        return used is null ? 0 : Math.Max(0, used.RowCount() - 1);
    }

    private void ValidateWorkbook(XLWorkbook workbook)
    {
        foreach (var name in ExpectedSheets)
        {
            if (!workbook.Worksheets.TryGetWorksheet(name, out _))
                AddIssue($"Workbook is missing expected sheet '{name}'.");
        }
    }

    private void ImportCustomerContacts(
        XLWorkbook workbook,
        Dictionary<string, Customer> customersByCode,
        Dictionary<string, Customer> customersByName,
        Dictionary<string, CustomerContact> contactsByCode)
    {
        if (!workbook.Worksheets.TryGetWorksheet("Customer Contacts", out var ws))
            return;

        foreach (var row in ReadRows(ws))
        {
            var customerName = row.Get("Customer");
            var contactName = row.Get("Contact Name");
            if (string.IsNullOrWhiteSpace(customerName) || string.IsNullOrWhiteSpace(contactName))
                continue;

            if (!customersByName.TryGetValue(customerName, out var customer))
            {
                var code = MakeCode(customerName, 40);
                if (customersByCode.TryGetValue(code, out var codeMatch))
                {
                    customer = codeMatch;
                    if (!customer.Name.Equals(customerName, StringComparison.OrdinalIgnoreCase))
                        AddIssue($"Customer '{customerName}' resolves to existing code '{code}' owned by '{customer.Name}'. Review before relying on this match.");
                }
                else
                {
                    customer = new Customer { Code = code, Name = customerName, Active = row.Active() };
                    db.Customers.Add(customer);
                    customersByCode[code] = customer;
                }

                customersByName[customerName] = customer;
            }
            else
            {
                customer.Active = customer.Active || row.Active();
            }

            var contactCode = row.Get("ContactID");
            if (string.IsNullOrWhiteSpace(contactCode))
                contactCode = MakeCode($"{customerName}-{contactName}-{row.Get("Email")}", 80);

            if (!contactsByCode.TryGetValue(contactCode, out var contact))
            {
                contact = new CustomerContact
                {
                    Code = contactCode,
                    CustomerId = customer.Id,
                    ContactName = contactName
                };
                db.CustomerContacts.Add(contact);
                contactsByCode[contactCode] = contact;
            }

            contact.CustomerId = customer.Id;
            contact.ContactName = contactName;
            contact.Role = row.Get("Role");
            contact.Email = row.Get("Email");
            contact.Phone = row.Get("Phone");
            contact.Notes = row.Get("Notes");
            contact.Active = row.Active();
        }
    }

    private void ImportSites(
        XLWorkbook workbook,
        Dictionary<string, Customer> customersByCode,
        Dictionary<string, Site> sitesByCode,
        Dictionary<string, SiteAlias> aliases)
    {
        if (!workbook.Worksheets.TryGetWorksheet("Sites", out var ws))
            return;

        foreach (var row in ReadRows(ws))
        {
            var code = row.Get("SiteID");
            var name = row.Get("Site");
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                continue;

            if (!sitesByCode.TryGetValue(code, out var site))
            {
                site = new Site { Code = code, Name = name };
                db.Sites.Add(site);
                sitesByCode[code] = site;
            }

            site.Name = name;
            site.DriverTextName = row.Get("Driver Text Name");
            site.FullAddress = row.Get("Collection Address");
            site.Postcode = ExtractPostcode(site.FullAddress);
            site.MapLink = row.Get("Map Link");
            site.CollectionInstructions = row.Get("Collection Instructions");
            site.DriverInstructions = row.Get("Collection Instructions");
            site.Active = row.Active();

            var customerCode = row.Get("Customer Code");
            if (!string.IsNullOrWhiteSpace(customerCode))
            {
                if (customersByCode.TryGetValue(customerCode, out var customer))
                    site.CustomerId = customer.Id;
                else
                    AddIssue($"Site {code} '{name}' references customer code '{customerCode}' which is not yet canonical.");
            }

            foreach (var aliasText in SplitAliases(row.Get("Aliases")))
            {
                var aliasKey = $"{site.Id}|{aliasText}";
                if (aliases.ContainsKey(aliasKey))
                    continue;

                var alias = new SiteAlias
                {
                    SiteId = site.Id,
                    Alias = aliasText,
                    Source = "Workbook:Sites",
                    Approved = true
                };
                db.SiteAliases.Add(alias);
                aliases[aliasKey] = alias;
            }
        }
    }

    private void ImportDrivers(XLWorkbook workbook, Dictionary<string, Driver> driversByEmployee)
    {
        if (!workbook.Worksheets.TryGetWorksheet("Drivers", out var ws))
            return;

        foreach (var row in ReadRows(ws))
        {
            var employee = row.Get("Employee Number");
            var name = row.Get("Display Name");
            if (string.IsNullOrWhiteSpace(employee) || string.IsNullOrWhiteSpace(name))
                continue;

            if (!driversByEmployee.TryGetValue(employee, out var driver))
            {
                driver = new Driver { EmployeeNumber = employee, DisplayName = name };
                db.Drivers.Add(driver);
                driversByEmployee[employee] = driver;
            }

            driver.DisplayName = name;
            driver.TachoName = row.Get("Tacho Name");
            driver.MobileNumber = row.Get("Phone Number");
            driver.DriverType = row.Get("Driver Type");
            driver.DriverGroup = row.Get("Driver Group");
            driver.Skills = row.Get("Skills");
            driver.Coding = row.Get("Coding");
            driver.AgencyName = row.Get("Agency Name");
            driver.NorthEligible = ParseNullableBool(row.Get("North Eligible"));
            driver.PreloadEligible = ParseNullableBool(row.Get("Preload Eligible"));
            driver.Notes = row.Get("Notes");
            driver.TachoMasterDriverId = row.Get("TachoMasterDriverId");
            driver.DrivingLicenceNumber = row.Get("Driving Licence Number");
            driver.LicenceExpiry = ParseDate(row.Get("Licence Expiry"));
            driver.LicenceStatus = row.Get("Licence Status");
            driver.LastTachoMasterSync = ParseDateTimeOffset(row.Get("Last TachoMaster Sync"));
            driver.Active = row.Active();
        }
    }

    private void ImportVehicles(XLWorkbook workbook, Dictionary<string, Vehicle> vehiclesByReg)
    {
        if (!workbook.Worksheets.TryGetWorksheet("Vehicles & Fuel", out var ws))
            return;

        foreach (var row in ReadRows(ws))
        {
            var registration = row.Get("Registration");
            if (string.IsNullOrWhiteSpace(registration))
                continue;

            registration = registration.Replace(" ", string.Empty).ToUpperInvariant();

            if (!vehiclesByReg.TryGetValue(registration, out var vehicle))
            {
                vehicle = new Vehicle { Registration = registration };
                db.Vehicles.Add(vehicle);
                vehiclesByReg[registration] = vehicle;
            }

            vehicle.FleetNumber = row.Get("VehicleID");
            vehicle.Abbreviation = row.Get("Abbreviation");
            vehicle.Transmission = row.Get("Transmission");
            vehicle.Dvs = row.Get("DVS");
            vehicle.CabMobile = row.Get("Cab Mobile");
            vehicle.FuelPin = row.Get("Fuel PIN");
            vehicle.ShellCard = row.Get("Shell Card");
            vehicle.BpRedCard = row.Get("BP Red Card");
            vehicle.BpPlainCard = row.Get("BP Plain Card");
            vehicle.Notes = row.Get("Notes");
            vehicle.Active = row.Active();
        }
    }

    private void ImportSiteCutoffs(
        XLWorkbook workbook,
        Dictionary<string, Site> sitesByCode,
        Dictionary<string, SiteCutoff> cutoffsByCode)
    {
        if (!workbook.Worksheets.TryGetWorksheet("Site Cutoffs", out var ws))
            return;

        foreach (var row in ReadRows(ws))
        {
            var code = row.Get("SiteCutoffID");
            var siteCode = row.Get("SiteID");
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(siteCode))
                continue;

            if (!sitesByCode.TryGetValue(siteCode, out var site))
            {
                AddIssue($"Cutoff {code} references missing site {siteCode}.");
                continue;
            }

            if (!cutoffsByCode.TryGetValue(code, out var cutoff))
            {
                cutoff = new SiteCutoff { Code = code, SiteId = site.Id };
                db.SiteCutoffs.Add(cutoff);
                cutoffsByCode[code] = cutoff;
            }

            cutoff.SiteId = site.Id;
            cutoff.Plan = row.Get("Plan");
            cutoff.StandardCutoff = ParseTime(row.Get("Standard Cutoff"));
            cutoff.ExtendedCutoff = ParseTime(row.Get("Extended Cutoff"));
            cutoff.Contact = row.Get("Contact");
            cutoff.Notes = row.Get("Notes");
            cutoff.Temperature = row.Get("Temperature");
            cutoff.PalletType = row.Get("Pallet Type");
            cutoff.LastDespatchTime = ParseTime(row.Get("Last Despatch Time"));
            cutoff.PlannedCollectFrom = ParseTime(row.Get("Planned Collect From"));
            cutoff.PlannedCollectTo = ParseTime(row.Get("Planned Collect To"));
            cutoff.DepotDeliveryDeadline = ParseTime(row.Get("Depot Delivery Deadline"));
            cutoff.Active = row.Active();
        }
    }

    private void ImportRouteTimings(XLWorkbook workbook, Dictionary<string, RouteTiming> timingsByKey)
    {
        if (!workbook.Worksheets.TryGetWorksheet("Run Times", out var ws))
            return;

        foreach (var row in ReadRows(ws))
        {
            var route = row.Get("Route");
            if (string.IsNullOrWhiteSpace(route))
                continue;

            var palletType = row.Get("Pallet Type");
            var key = MakeCode($"{route}|{palletType}", 300);

            if (!timingsByKey.TryGetValue(key, out var timing))
            {
                timing = new RouteTiming { Key = key, Route = route };
                db.RouteTimings.Add(timing);
                timingsByKey[key] = timing;
            }

            timing.Route = route;
            timing.PalletType = palletType;
            timing.LastDespatchTime = ParseTime(row.Get("Last Despatch Time"));
            timing.PlannedCollectFrom = ParseTime(row.Get("Planned Collect Time From"));
            timing.PlannedCollectTo = ParseTime(row.Get("Planned Collect Time To"));
            timing.DepotDeliveryDeadline = ParseTime(row.Get("Depot Delivery No Later Than"));
            timing.Active = true;
        }
    }

    private void ImportMarketContacts(XLWorkbook workbook, Dictionary<string, MarketContact> contactsByKey)
    {
        if (!workbook.Worksheets.TryGetWorksheet("Market Contacts", out var ws))
            return;

        foreach (var row in ReadRows(ws))
        {
            var market = row.Get("Market");
            var name = row.Get("Name");
            if (string.IsNullOrWhiteSpace(market) || string.IsNullOrWhiteSpace(name))
                continue;

            var key = MakeCode($"{market}|{name}|{row.Get("Stand Or Location")}|{row.Get("Sender")}", 300);
            if (!contactsByKey.TryGetValue(key, out var contact))
            {
                contact = new MarketContact { Key = key, MarketName = market, Name = name };
                db.MarketContacts.Add(contact);
                contactsByKey[key] = contact;
            }

            contact.MarketName = market;
            contact.Name = name;
            contact.StandOrLocation = row.Get("Stand Or Location");
            contact.Salesman = row.Get("Salesman");
            contact.Sender = row.Get("Sender");
            contact.Active = row.Active();
        }
    }

    private void ImportAliasCandidates(
        XLWorkbook workbook,
        string sheetName,
        string aliasType,
        Dictionary<string, SiteAliasCandidate> candidates)
    {
        if (!workbook.Worksheets.TryGetWorksheet(sheetName, out var ws))
            return;

        foreach (var row in ReadRows(ws))
        {
            var alias = row.Values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Value)).Value;
            if (string.IsNullOrWhiteSpace(alias))
                continue;

            var key = $"{aliasType}|{alias}";
            if (candidates.ContainsKey(key))
                continue;

            var candidate = new SiteAliasCandidate
            {
                Alias = alias,
                AliasType = aliasType,
                Source = $"Workbook:{sheetName}",
                Approved = false,
                Active = true
            };
            db.SiteAliasCandidates.Add(candidate);
            candidates[key] = candidate;
        }
    }

    private void ImportFuelPrices(XLWorkbook workbook, Dictionary<string, FuelPrice> fuelByCode)
    {
        if (!workbook.Worksheets.TryGetWorksheet("Fuel Price History", out var ws))
            return;

        foreach (var row in ReadRows(ws))
        {
            var code = row.Get("FuelPriceID");
            var provider = row.Get("Provider");
            var week = ParseDate(row.Get("Week Commencing"));
            var price = ParseDecimal(row.Get("Price Pence Per Litre"));
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(provider) || week is null || price is null)
                continue;

            if (!fuelByCode.TryGetValue(code, out var fuel))
            {
                fuel = new FuelPrice
                {
                    Code = code,
                    WeekCommencing = week.Value,
                    Provider = provider,
                    PricePencePerLitre = price.Value
                };
                db.FuelPrices.Add(fuel);
                fuelByCode[code] = fuel;
            }

            fuel.WeekCommencing = week.Value;
            fuel.Provider = provider;
            fuel.PricePencePerLitre = price.Value;
            fuel.IsPricingMaximum = ParseBool(row.Get("Is Pricing Maximum"));
            fuel.Source = row.Get("Source");
            fuel.Notes = row.Get("Notes");
            fuel.Active = true;
        }
    }

    private static IEnumerable<WorkbookRow> ReadRows(IXLWorksheet ws)
    {
        var range = ws.RangeUsed();
        if (range is null || range.RowCount() < 2)
            yield break;

        var headerRow = range.FirstRow();
        var headers = headerRow.Cells()
            .Select((cell, index) => new { index = index + 1, name = cell.GetString().Trim() })
            .Where(x => !string.IsNullOrWhiteSpace(x.name))
            .ToArray();

        for (var rowIndex = 2; rowIndex <= range.RowCount(); rowIndex++)
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var hasValue = false;
            foreach (var header in headers)
            {
                var text = range.Cell(rowIndex, header.index).GetString().Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    hasValue = true;
                values[header.name] = string.IsNullOrWhiteSpace(text) ? null : text;
            }

            if (hasValue)
                yield return new WorkbookRow(values);
        }
    }

    private sealed record WorkbookRow(IReadOnlyDictionary<string, string?> Values)
    {
        public string? Get(string key) =>
            Values.TryGetValue(key, out var value) ? value?.Trim() : null;

        public bool Active() =>
            !string.Equals(Get("Active"), "No", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Get("Active"), "False", StringComparison.OrdinalIgnoreCase)
            && Get("Active") != "0";
    }

    private void AddIssue(string issue)
    {
        if (_issues.Count < 200)
            _issues.Add(issue);
    }

    private static string MakeCode(string value, int maxLength)
    {
        var normalized = new string(value
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "UNKNOWN";

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static IEnumerable<string> SplitAliases(string? aliases)
    {
        if (string.IsNullOrWhiteSpace(aliases))
            return [];

        return Regex.Split(aliases, @"[;|\r\n]+")
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ExtractPostcode(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var match = Regex.Match(address.ToUpperInvariant(), @"\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b");
        return match.Success ? Regex.Replace(match.Value, @"\s+", " ").Trim() : null;
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "True", StringComparison.OrdinalIgnoreCase)
        || value == "1";

    private static bool? ParseNullableBool(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ParseBool(value);

    private static TimeOnly? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            || TimeOnly.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out time)
            ? time
            : null;
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || DateOnly.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out date)
            ? date
            : null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            || DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed)
            ? parsed
            : null;
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            || decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed)
            ? parsed
            : null;
    }
}
