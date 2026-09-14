using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public sealed class SharePointMasterDataOptions
{
    public bool Enabled { get; set; }
    public string TenantId { get; set; } = "5aec48a1-c3c7-4cfd-a073-b38ae50041b1";
    public string ClientId { get; set; } = "e52218ab-0a5a-459a-84b2-423a83152582";
    public string ClientSecret { get; set; } = string.Empty;
    public string Hostname { get; set; } = "stuartlyonshaulage.sharepoint.com";
    public string SitePath { get; set; } = "/";
    public Dictionary<string, string> Lists { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SharePointMasterDataOptions()
    {
        Lists["customer"] = "Hub Customers";
        Lists["customercontact"] = "Hub Customer Contacts";
        Lists["site"] = "Hub Sites";
        Lists["driver"] = "Hub Drivers";
        Lists["vehicle"] = "Hub Vehicles";
        Lists["trailer"] = "Hub Trailers";
        Lists["fuelcard"] = "Fuel Cards";
        Lists["marketcontact"] = "TMS Markets";
        Lists["emailroute"] = "Order Email Routes";
    }
}

public sealed record SharePointMasterDataSyncResult(int ListsRead, int RowsRead, IReadOnlyList<StageImportRequest> Requests);
public sealed record SharePointMasterDataPublishResult(int ListsWritten, int RowsWritten, IReadOnlyDictionary<string, int> RowsByList);

public sealed class SharePointMasterDataException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed class SharePointMasterDataSyncService(
    HttpClient http,
    SharePointMasterDataOptions options,
    ILogger<SharePointMasterDataSyncService> logger)
{
    private readonly SharePointMasterDataOptions settings = options;
    public bool IsEnabled => settings.Enabled;

    public async Task<SharePointMasterDataPublishResult> PublishFromSqlAsync(TmsDbContext db, CancellationToken ct)
    {
        ValidateConfiguration();
        var token = await GetTokenAsync(ct);
        var rows = await BuildAllRowsAsync(db, ct);
        var rowsByList = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in settings.Lists)
        {
            if (!rows.TryGetValue(mapping.Key, out var sourceRows) || string.IsNullOrWhiteSpace(mapping.Value)) continue;
            rowsByList[mapping.Key] = await PublishRowsAsync(mapping.Key, mapping.Value, sourceRows, token, ct);
        }

        return new SharePointMasterDataPublishResult(rowsByList.Count, rowsByList.Values.Sum(), rowsByList);
    }

    /// <summary>
    /// Seeds only the governed Customer Contacts List. This is intentionally separate from the
    /// original all-master bootstrap so adding CRM contacts later cannot overwrite deliberate
    /// edits already made to Customers, Sites, Drivers or Fleet Lists.
    /// </summary>
    public async Task<int> PublishCustomerContactsFromSqlAsync(TmsDbContext db, CancellationToken ct)
    {
        ValidateConfiguration();
        if (!settings.Lists.TryGetValue("customercontact", out var listName) || string.IsNullOrWhiteSpace(listName))
            throw new SharePointMasterDataException("ListConfigurationMissing", "The governed Customer Contacts List is not configured.");
        var token = await GetTokenAsync(ct);
        var rows = await BuildCustomerContactRowsAsync(db, ct);
        return await PublishRowsAsync("customercontact", listName, rows, token, ct);
    }

    public async Task<SharePointMasterDataSyncResult> ReadAsync(CancellationToken ct)
    {
        ValidateConfiguration();
        var token = await GetTokenAsync(ct);
        var requests = new List<StageImportRequest>();
        var listsRead = 0;

        foreach (var mapping in settings.Lists)
        {
            if (string.IsNullOrWhiteSpace(mapping.Value)) continue;
            listsRead++;
            var listId = await ResolveForEntityAsync(mapping.Key, mapping.Value, token, ct);
            await EnsureColumnsAsync(mapping.Key, listId, token, ct);
            var rows = await ReadListAsync(mapping.Key, listId, token, ct);
            foreach (var row in rows)
            {
                var itemId = row.TryGetProperty("id", out var id) ? id.ToString() : Guid.NewGuid().ToString("N");
                var fields = row.TryGetProperty("fields", out var f) ? f : row;
                var sourceVersion = row.TryGetProperty("eTag", out var eTag) ? eTag.GetString() : null;
                requests.Add(new StageImportRequest(
                    mapping.Key,
                    $"sharepoint:{mapping.Key}:{itemId}:{sourceVersion ?? "unversioned"}",
                    NormalizeFields(mapping.Key, fields),
                    "Microsoft Lists / SharePoint"));
            }
        }

        logger.LogInformation("Read {RowsRead} master-data rows from {ListsRead} Microsoft Lists.", requests.Count, listsRead);
        return new SharePointMasterDataSyncResult(listsRead, requests.Count, requests);
    }

    public async Task PublishSiteAliasesAsync(string siteKey, string? aliases, CancellationToken ct)
    {
        ValidateConfiguration();
        var token = await GetTokenAsync(ct);
        if (!settings.Lists.TryGetValue("site", out var listName) || string.IsNullOrWhiteSpace(listName))
            throw new SharePointMasterDataException("ListConfigurationMissing", "The Hub Sites List is not configured for SharePoint CRM sync.");
        var listId = await ResolveListIdAsync(listName, token, ct);
        var existing = await ReadListAsync("site", listId, token, ct);
        var current = existing.FirstOrDefault(item => item.TryGetProperty("fields", out var fields)
            && fields.TryGetProperty("SiteKey", out var key)
            && string.Equals(key.ToString(), siteKey, StringComparison.OrdinalIgnoreCase));
        if (current.ValueKind == JsonValueKind.Undefined)
            throw new SharePointMasterDataException("SiteNotFoundInCrm", $"Site '{siteKey}' is not yet in Hub Sites. Run the initial CRM publish before alias sync can update it.");
        await UpdateListItemAsync(listId, current.GetProperty("id").ToString(), Fields(("Aliases", aliases), ("SyncStatus", "Synced")), token, ct);
    }

    private async Task<Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>> BuildAllRowsAsync(TmsDbContext db, CancellationToken ct)
    {
        var drivers = await db.Drivers.AsNoTracking().OrderBy(x => x.EmployeeNumber).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var vehicles = await db.Vehicles.AsNoTracking().OrderBy(x => x.Registration).ToListAsync(ct);

        return new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["customer"] = (await db.Customers.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct)).Select(x => Fields(
                ("Title", x.Code), ("CustomerKey", x.Code), ("TradingName", x.TradingName ?? x.Name),
                ("AccountOwner", x.AccountOwner), ("ServiceNotes", x.ServiceNotes), ("DefaultSiteCode", x.DefaultSiteCode), ("Active", x.Active))).ToArray(),
            ["customercontact"] = await BuildCustomerContactRowsAsync(db, ct),
            ["site"] = await BuildSiteRowsAsync(db, ct),
            ["driver"] = await BuildDriverRowsAsync(db, drivers, ct),
            ["vehicle"] = vehicles.Select(x => Fields(
                ("Title", x.Registration), ("VehicleKey", x.FleetNumber ?? x.Registration), ("Registration", x.Registration),
                ("FleetNumber", x.FleetNumber), ("Abbreviation", x.Abbreviation), ("Transmission", x.Transmission),
                ("DvsCompliant", x.DvsCompliant), ("FuelProvider", x.FuelProvider), ("CabMobile", x.CabMobile),
                ("FuelPinSecretName", x.FuelPinSecretName), ("FuelCardLastFour", x.FuelCardLastFour),
                ("ShellCard", x.ShellCard), ("BpRedCard", x.BpRedCard), ("BpPlainCard", x.BpPlainCard),
                ("Notes", x.Notes), ("FleetioId", x.FleetioId), ("FleetioName", x.FleetioName),
                ("FleetioStatus", x.FleetioStatus), ("TmsVehicleId", x.Id.ToString()), ("Active", x.Active),
                ("ComplianceStatus", x.FleetioVor == true ? "VOR" : "Unknown"))).ToArray(),
            ["fuelcard"] = vehicles.Select(x => Fields(
                ("Title", x.Registration), ("VehicleKey", x.FleetNumber ?? x.Registration), ("Registration", x.Registration),
                ("FuelProvider", x.FuelProvider), ("FuelPinSecretName", x.FuelPinSecretName),
                ("FuelCardLastFour", x.FuelCardLastFour), ("ShellCard", x.ShellCard),
                ("BpRedCard", x.BpRedCard), ("BpPlainCard", x.BpPlainCard), ("Active", x.Active))).ToArray(),
            ["trailer"] = (await db.Trailers.AsNoTracking().OrderBy(x => x.TrailerNumber).ToListAsync(ct)).Select(x => Fields(
                ("Title", x.TrailerNumber), ("TrailerKey", x.TrailerNumber), ("Registration", x.TrailerNumber),
                ("TrailerType", x.Type), ("StandardCapacity", x.StandardCapacity), ("EuroCapacity", x.EuroCapacity),
                ("Notes", x.Notes), ("Active", x.Active))).ToArray(),
            ["marketcontact"] = (await db.MarketContacts.AsNoTracking().OrderBy(x => x.Market).ThenBy(x => x.Name).ToListAsync(ct)).Select(x => Fields(
                ("Title", $"{x.Market} · {x.Name}"), ("Market", x.Market), ("Name", x.Name),
                ("StandOrLocation", x.StandOrLocation), ("Salesman", x.Salesman), ("Sender", x.Sender),
                ("ReadOnlyMapPdfUrl", x.ReadOnlyMapPdfUrl), ("Active", x.Active))).ToArray(),
            ["emailroute"] = await BuildEmailRouteRowsAsync(db, ct)
        };
    }

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> BuildDriverRowsAsync(
        TmsDbContext db,
        IReadOnlyList<Driver> drivers,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var vehicles = await db.Vehicles.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var allocations = await db.Loads.AsNoTracking()
            .Where(x => x.DriverId != null && x.Status != LoadStatus.Cancelled && x.Status != LoadStatus.Completed)
            .OrderBy(x => x.PlanningDate < today ? 1 : 0)
            .ThenBy(x => x.PlanningDate)
            .ThenBy(x => x.Reference)
            .ToListAsync(ct);

        var allocatedByDriver = allocations
            .Where(x => x.DriverId is not null && x.VehicleId is not null && vehicles.ContainsKey(x.VehicleId.Value))
            .GroupBy(x => x.DriverId!.Value)
            .ToDictionary(
                group => group.Key,
                group => vehicles[group.First().VehicleId!.Value].Registration,
                EqualityComparer<Guid>.Default);

        return drivers.Select(x => Fields(
            ("Title", x.DisplayName), ("DriverKey", x.TachoCardNumber ?? x.EmployeeNumber),
            ("DriverName", x.DisplayName), ("EmployeeNumber", x.EmployeeNumber), ("TachoName", x.TachoName),
            ("Email", x.Email), ("MobileNumber", x.MobileNumber), ("GradeCode", x.GradeCode),
            ("AllocatedVehicle", allocatedByDriver.GetValueOrDefault(x.Id)),
            ("DriverType", x.DriverType), ("DriverGroup", x.DriverGroup),
            ("Skills", x.Skills), ("AgencyName", x.AgencyName), ("Coding", x.Coding), ("Notes", x.Notes),
            ("LicenceNumber", x.DrivingLicenceNumber), ("LicenceExpiry", x.LicenceExpiry),
            ("TachoCardNumber", x.TachoCardNumber), ("TachoMasterDriverId", x.TachoMasterDriverId),
            ("LastTachoSyncUtc", x.LastTachoSyncUtc), ("Active", x.Active),
            ("ComplianceStatus", string.IsNullOrWhiteSpace(x.LicenceStatus) ? "Unknown" : x.LicenceStatus))).ToArray();
    }

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> BuildCustomerContactRowsAsync(TmsDbContext db, CancellationToken ct)
    {
        var contacts = await db.CustomerContacts.AsNoTracking()
            .OrderBy(x => x.CustomerCode).ThenBy(x => x.Name).ThenBy(x => x.Email)
            .ToListAsync(ct);
        return contacts.Select(x => Fields(
            ("Title", $"{x.CustomerCode} · {x.Name}"),
            ("ContactKey", x.Id.ToString()),
            ("CustomerKey", x.CustomerCode),
            ("ContactName", x.Name),
            ("Email", x.Email),
            ("MobileNumber", x.MobileNumber),
            ("ReceivesEtaUpdates", x.ReceivesEtaUpdates),
            ("Active", x.Active))).ToArray();
    }

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> BuildSiteRowsAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().OrderBy(x => x.ExternalCode).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        var geofences = await db.SiteGeofences.AsNoTracking().Where(g => g.Active && g.SiteId != null)
            .GroupBy(g => g.SiteId!.Value)
            .ToDictionaryAsync(group => group.Key, group => group.OrderBy(g => g.Id).First().Id.ToString(), ct);
        return sites.Select(x => Fields(
            ("Title", x.ExternalCode), ("SiteKey", x.ExternalCode), ("CustomerKey", x.CustomerCode),
            ("SiteName", x.Name), ("BuildingName", x.DriverTextName ?? x.Name), ("Address1", x.CollectionAddress),
            ("MapLink", x.MapLink), ("Aliases", x.Aliases), ("GeofenceId", geofences.GetValueOrDefault(x.Id)),
            ("OperationalRegion", x.OperationalRegion), ("Active", x.Active), ("SyncStatus", "Synced"))).ToArray();
    }

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> BuildEmailRouteRowsAsync(TmsDbContext db, CancellationToken ct)
    {
        try
        {
            var routes = await db.CustomerEmailRoutes.AsNoTracking()
                .OrderBy(x => x.SenderEmail).ThenBy(x => x.SenderDomain).ThenBy(x => x.CustomerCode)
                .ToListAsync(ct);
            return routes.Select(x => Fields(
                ("Title", x.SenderEmail ?? $"@{x.SenderDomain}"), ("RouteKey", x.Id.ToString()),
                ("CustomerKey", x.CustomerCode), ("SiteKey", x.DefaultSiteCode),
                ("SenderEmail", x.SenderEmail), ("SenderDomain", x.SenderDomain),
                ("SubjectContains", x.SubjectContains), ("ParserType", x.ParserType),
                ("RequiresReview", x.RequiresReview), ("Active", x.Active))).ToArray();
        }
        catch (Exception ex) when (ex.GetBaseException().Message.Contains("CustomerEmailRoutes", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
    }

    private async Task<int> PublishRowsAsync(
        string entityType,
        string listName,
        IReadOnlyList<Dictionary<string, object?>> sourceRows,
        string token,
        CancellationToken ct)
    {
        var listId = await ResolveForEntityAsync(entityType, listName, token, ct);
        await EnsureColumnsAsync(entityType, listId, token, ct);
        var existing = await ReadListAsync(entityType, listId, token, ct);
        var keyField = KeyField(entityType);
        var existingByKey = existing
            .Where(item => item.TryGetProperty("fields", out var fields)
                && fields.TryGetProperty(keyField, out var key)
                && !string.IsNullOrWhiteSpace(key.ToString()))
            .GroupBy(item => item.GetProperty("fields").GetProperty(keyField).ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var existingDriversByEmployee = entityType.Equals("driver", StringComparison.OrdinalIgnoreCase)
            ? existing.Where(item => item.TryGetProperty("fields", out var fields)
                    && fields.TryGetProperty("EmployeeNumber", out var employee)
                    && !string.IsNullOrWhiteSpace(employee.ToString()))
                .GroupBy(item => item.GetProperty("fields").GetProperty("EmployeeNumber").ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var distinctRows = sourceRows
            .Where(row => !string.IsNullOrWhiteSpace(row.GetValueOrDefault(keyField)?.ToString()))
            .GroupBy(row => row[keyField]!.ToString()!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        await Parallel.ForEachAsync(distinctRows,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (source, itemCt) =>
            {
                var key = source[keyField]!.ToString()!;
                var exists = existingByKey.TryGetValue(key, out var current)
                    || (entityType.Equals("driver", StringComparison.OrdinalIgnoreCase)
                        && source.TryGetValue("EmployeeNumber", out var employee)
                        && employee is not null
                        && existingDriversByEmployee.TryGetValue(employee.ToString()!, out current));
                if (exists)
                    await UpdateListItemAsync(listId, current.GetProperty("id").ToString(), source, token, itemCt);
                else
                    await CreateListItemAsync(listId, source, token, itemCt);
            });

        await RetireRowsOutsideSnapshotAsync(entityType, listId, keyField,
            distinctRows.Select(row => row[keyField]!.ToString()!).ToHashSet(StringComparer.OrdinalIgnoreCase), token, ct);
        return distinctRows.Length;
    }

    private static string KeyField(string entityType) => entityType.ToLowerInvariant() switch
    {
        "customer" => "CustomerKey",
        "customercontact" => "ContactKey",
        "site" => "SiteKey",
        "driver" => "DriverKey",
        "vehicle" => "VehicleKey",
        "trailer" => "TrailerKey",
        "fuelcard" => "VehicleKey",
        "emailroute" => "RouteKey",
        "marketcontact" => "Title",
        _ => "Title"
    };

    private async Task<JsonElement[]> ReadListAsync(string entityType, string listId, string token, CancellationToken ct)
    {
        var siteSelector = SiteSelector();
        var listSelector = Uri.EscapeDataString(listId);
        string? uri = $"https://graph.microsoft.com/v1.0/sites/{siteSelector}/lists/{listSelector}/items?expand=fields&$top=999";
        var rows = new List<JsonElement>();
        while (!string.IsNullOrWhiteSpace(uri))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw Failure("ListReadFailed", $"Microsoft List '{entityType}' could not be read", response.StatusCode, body);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("value", out var value))
                rows.AddRange(value.EnumerateArray().Select(x => x.Clone()));
            uri = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink) ? nextLink.GetString() : null;
        }
        return rows.ToArray();
    }

    private async Task<string> ResolveForEntityAsync(string entityType, string listName, string token, CancellationToken ct)
    {
        if (entityType.Equals("customercontact", StringComparison.OrdinalIgnoreCase)
            || entityType.Equals("emailroute", StringComparison.OrdinalIgnoreCase))
            return await ResolveOrProvisionManagedListAsync(entityType, listName, token, ct);
        return await ResolveListIdAsync(listName, token, ct);
    }

    private async Task<string> ResolveListIdAsync(string listName, string token, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"displayName eq '{listName.Replace("'", "''")}'");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/sites/{SiteSelector()}/lists?$filter={filter}&$select=id,displayName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw Failure("ListResolveFailed", $"Microsoft List '{listName}' could not be resolved", response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var match = document.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        return match.ValueKind == JsonValueKind.Undefined
            ? throw new SharePointMasterDataException("ListNotFound", $"The required Microsoft List '{listName}' was not found on the SLH Hub site.")
            : match.GetProperty("id").GetString()!;
    }

    private async Task<string> ResolveOrProvisionManagedListAsync(string entityType, string listName, string token, CancellationToken ct)
    {
        try { return await ResolveListIdAsync(listName, token, ct); }
        catch (SharePointMasterDataException ex) when (ex.Code == "ListNotFound")
        {
            using var create = new HttpRequestMessage(HttpMethod.Post,
                $"https://graph.microsoft.com/v1.0/sites/{SiteSelector()}/lists")
            {
                Content = JsonContent.Create(new { displayName = listName, list = new { template = "genericList" } })
            };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(create, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw Failure("ListProvisionFailed", $"Microsoft List '{listName}' could not be provisioned", response.StatusCode, body);
            using var document = JsonDocument.Parse(body);
            var listId = document.RootElement.GetProperty("id").GetString()!;
            logger.LogInformation("Provisioned governed Microsoft List {ListName} for {EntityType}.", listName, entityType);
            return listId;
        }
    }

    private async Task EnsureColumnsAsync(string entityType, string listId, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildListUrl(listId, "columns?$select=name"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw Failure("ListSchemaReadFailed", $"Microsoft List '{entityType}' columns could not be read", response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var existing = document.RootElement.GetProperty("value").EnumerateArray()
            .Select(column => column.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        foreach (var column in ColumnsFor(entityType).Where(column => !existing.Contains(column["name"].ToString()!)))
        {
            using var create = new HttpRequestMessage(HttpMethod.Post, BuildListUrl(listId, "columns"))
            {
                Content = JsonContent.Create(column)
            };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var createResponse = await http.SendAsync(create, ct);
            var createBody = await createResponse.Content.ReadAsStringAsync(ct);
            if (!createResponse.IsSuccessStatusCode)
                throw Failure("ListSchemaProvisionFailed", $"Column '{column["name"]}' could not be provisioned on Microsoft List '{entityType}'", createResponse.StatusCode, createBody);
        }
    }

    private async Task RetireRowsOutsideSnapshotAsync(string entityType, string listId, string keyField,
        IReadOnlySet<string> sourceKeys, string token, CancellationToken ct)
    {
        var current = await ReadListAsync(entityType, listId, token, ct);
        var retainedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retire = new List<JsonElement>();
        foreach (var item in current)
        {
            if (!item.TryGetProperty("fields", out var fields)
                || !fields.TryGetProperty(keyField, out var keyValue)
                || string.IsNullOrWhiteSpace(keyValue.ToString()))
            {
                retire.Add(item);
                continue;
            }
            var key = keyValue.ToString();
            if (!sourceKeys.Contains(key) || !retainedKeys.Add(key)) retire.Add(item);
        }

        await Parallel.ForEachAsync(retire,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (item, itemCt) =>
            {
                if (item.TryGetProperty("id", out var id))
                    await UpdateListItemAsync(listId, id.ToString(), Fields(("Active", false)), token, itemCt);
            });
    }

    private static IReadOnlyList<Dictionary<string, object>> ColumnsFor(string entityType) => entityType.ToLowerInvariant() switch
    {
        "customer" =>
        [
            TextColumn("CustomerKey", true), TextColumn("TradingName"), TextColumn("CustomerAliases"),
            TextColumn("AccountOwner"), TextColumn("ServiceNotes", multiline: true), TextColumn("DefaultSiteCode"), BooleanColumn("Active")
        ],
        "customercontact" =>
        [
            TextColumn("ContactKey", true), TextColumn("CustomerKey", true), TextColumn("ContactName"),
            TextColumn("Email"), TextColumn("MobileNumber"), BooleanColumn("ReceivesEtaUpdates"), BooleanColumn("Active")
        ],
        "site" =>
        [
            TextColumn("SiteKey", true), TextColumn("CustomerKey"), TextColumn("SiteName"), TextColumn("BuildingName"),
            TextColumn("Address1"), TextColumn("Address2"), TextColumn("Town"), TextColumn("County"), TextColumn("Postcode"),
            TextColumn("MapLink"), TextColumn("Aliases", multiline: true), TextColumn("GeofenceId"),
            TextColumn("OperationalRegion"), BooleanColumn("Active"), TextColumn("SyncStatus")
        ],
        "driver" =>
        [
            TextColumn("DriverKey", true), TextColumn("DriverName"), TextColumn("EmployeeNumber"), TextColumn("TachoName"),
            TextColumn("Email"), TextColumn("MobileNumber"), TextColumn("GradeCode"), TextColumn("AllocatedVehicle"), TextColumn("DriverType"), TextColumn("DriverGroup"), TextColumn("Skills", multiline: true),
            TextColumn("AgencyName"), TextColumn("Coding"), TextColumn("Notes", multiline: true), TextColumn("LicenceNumber"),
            TextColumn("LicenceExpiry"), TextColumn("TachoCardNumber", true), TextColumn("TachoMasterDriverId", true),
            TextColumn("LastTachoSyncUtc"), BooleanColumn("Active"), TextColumn("ComplianceStatus")
        ],
        "vehicle" =>
        [
            TextColumn("VehicleKey", true), TextColumn("Registration"), TextColumn("VehicleType"), TextColumn("FleetNumber"),
            TextColumn("Abbreviation"), TextColumn("Transmission"), BooleanColumn("DvsCompliant"), TextColumn("FuelProvider"),
            TextColumn("CabMobile"), TextColumn("FuelPinSecretName"), TextColumn("FuelCardLastFour"), TextColumn("ShellCard"),
            TextColumn("BpRedCard"), TextColumn("BpPlainCard"), TextColumn("Notes", multiline: true), TextColumn("FleetioId"),
            TextColumn("FleetioName"), TextColumn("FleetioStatus"), TextColumn("TmsVehicleId"), BooleanColumn("Active"), TextColumn("ComplianceStatus")
        ],
        "trailer" =>
        [
            TextColumn("TrailerKey", true), TextColumn("Registration"), TextColumn("TrailerType"),
            NumberColumn("StandardCapacity"), NumberColumn("EuroCapacity"), TextColumn("Notes", multiline: true), BooleanColumn("Active")
        ],
        "fuelcard" =>
        [
            TextColumn("VehicleKey", true), TextColumn("Registration"), TextColumn("FuelProvider"), TextColumn("FuelPinSecretName"),
            TextColumn("FuelCardLastFour"), TextColumn("ShellCard"), TextColumn("BpRedCard"), TextColumn("BpPlainCard"), BooleanColumn("Active")
        ],
        "marketcontact" =>
        [
            TextColumn("Market"), TextColumn("Name"), TextColumn("StandOrLocation"), TextColumn("Salesman"),
            TextColumn("Sender"), TextColumn("ReadOnlyMapPdfUrl"), BooleanColumn("Active")
        ],
        "emailroute" =>
        [
            TextColumn("RouteKey", true), TextColumn("CustomerKey"), TextColumn("SiteKey"),
            TextColumn("SenderEmail"), TextColumn("SenderDomain"), TextColumn("SubjectContains"),
            TextColumn("ParserType"), BooleanColumn("RequiresReview"), BooleanColumn("Active")
        ],
        _ => []
    };

    private static Dictionary<string, object> TextColumn(string name, bool indexed = false, bool multiline = false) => new()
    {
        ["name"] = name,
        ["indexed"] = indexed,
        ["text"] = new { allowMultipleLines = multiline, maxLength = multiline ? 4000 : 320 }
    };

    private static Dictionary<string, object> BooleanColumn(string name) => new()
    {
        ["name"] = name,
        ["boolean"] = new Dictionary<string, object>()
    };

    private static Dictionary<string, object> NumberColumn(string name) => new()
    {
        ["name"] = name,
        ["number"] = new Dictionary<string, object> { ["decimalPlaces"] = "none" }
    };

    private async Task<IReadOnlySet<string>> CreateListItemAsync(string listId, IReadOnlyDictionary<string, object?> fields, string token, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildListUrl(listId, "items"))
            {
                Content = JsonContent.Create(new { fields = payload })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return payload.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 8)
            {
                await DelayForThrottleAsync(response, attempt, ct);
                continue;
            }
            throw Failure("ListCreateFailed", "A complete Microsoft List item could not be created", response.StatusCode, body);
        }
    }

    private async Task<IReadOnlySet<string>> UpdateListItemAsync(string listId, string itemId, IReadOnlyDictionary<string, object?> fields, string token, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(new HttpMethod("PATCH"), BuildListUrl(listId, $"items/{Uri.EscapeDataString(itemId)}/fields"))
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return payload.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 8)
            {
                await DelayForThrottleAsync(response, attempt, ct);
                continue;
            }
            throw Failure("ListUpdateFailed", "A complete Microsoft List item could not be updated", response.StatusCode, body);
        }
    }

    private async Task DelayForThrottleAsync(HttpResponseMessage response, int attempt, CancellationToken ct)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryDate) retryAfter = retryDate - DateTimeOffset.UtcNow;
        var fallbackSeconds = Math.Min(30, Math.Pow(2, attempt + 1));
        var delay = retryAfter is { } requested && requested > TimeSpan.Zero ? requested : TimeSpan.FromSeconds(fallbackSeconds);
        if (delay > TimeSpan.FromSeconds(60)) delay = TimeSpan.FromSeconds(60);
        logger.LogWarning("Microsoft Graph throttled the SharePoint publish; retrying in {DelaySeconds:n0}s (attempt {Attempt}/8).", delay.TotalSeconds, attempt + 1);
        await Task.Delay(delay, ct);
    }

    private string BuildListUrl(string listId, string suffix) =>
        $"https://graph.microsoft.com/v1.0/sites/{SiteSelector()}/lists/{Uri.EscapeDataString(listId)}/{suffix}";

    private string SiteSelector()
    {
        var sitePath = settings.SitePath.Trim('/');
        return string.IsNullOrWhiteSpace(sitePath) ? settings.Hostname : $"{settings.Hostname}:/{sitePath}:";
    }

    private static Dictionary<string, object?> Fields(params (string Name, object? Value)[] values) => values
        .Where(pair => pair.Value is not null)
        .ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static JsonElement NormalizeFields(string entityType, JsonElement fields)
    {
        var values = JsonNode.Parse(fields.GetRawText())?.AsObject() ?? new JsonObject();
        string? Text(string name)
        {
            var value = values[name];
            if (value is null) return null;
            var raw = value.ToJsonString();
            return raw == "null" ? null : raw.StartsWith('"') ? JsonSerializer.Deserialize<string>(raw) : raw;
        }
        void Set(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values[name] = value;
        }

        switch (entityType.ToLowerInvariant())
        {
            case "customer":
                Set("code", Text("CustomerKey"));
                Set("name", Text("TradingName") ?? Text("CustomerKey"));
                Set("tradingName", Text("TradingName"));
                Set("accountOwner", Text("AccountOwner"));
                Set("serviceNotes", Text("ServiceNotes"));
                Set("defaultSiteCode", Text("DefaultSiteCode"));
                Set("active", Text("Active"));
                break;
            case "customercontact":
                Set("id", Text("ContactKey"));
                Set("customerCode", Text("CustomerKey"));
                Set("name", Text("ContactName") ?? Text("Title"));
                Set("email", Text("Email"));
                Set("mobileNumber", Text("MobileNumber"));
                Set("receivesEtaUpdates", Text("ReceivesEtaUpdates"));
                Set("active", Text("Active"));
                break;
            case "site":
                Set("externalCode", Text("SiteKey"));
                Set("customerCode", Text("CustomerKey"));
                Set("name", Text("SiteName") ?? Text("BuildingName") ?? Text("SiteKey"));
                Set("driverTextName", Text("BuildingName") ?? Text("SiteName"));
                Set("collectionAddress", string.Join(", ", new[] { Text("Address1"), Text("Address2"), Text("Town"), Text("County"), Text("Postcode") }.Where(value => !string.IsNullOrWhiteSpace(value))));
                Set("mapLink", Text("MapLink"));
                Set("aliases", Text("Aliases"));
                Set("operationalRegion", Text("OperationalRegion"));
                Set("active", Text("Active"));
                break;
            case "driver":
                Set("employeeNumber", Text("EmployeeNumber") ?? Text("DriverKey"));
                Set("displayName", Text("DriverName") ?? Text("DriverKey"));
                Set("tachoName", Text("TachoName"));
                Set("email", Text("Email"));
                Set("mobileNumber", Text("MobileNumber"));
                Set("gradeCode", Text("GradeCode"));
                Set("allocatedVehicle", Text("AllocatedVehicle"));
                Set("driverType", Text("DriverType"));
                Set("driverGroup", Text("DriverGroup"));
                Set("skills", Text("Skills"));
                Set("agencyName", Text("AgencyName"));
                Set("coding", Text("Coding"));
                Set("notes", Text("Notes"));
                Set("drivingLicenceNumber", Text("LicenceNumber"));
                Set("licenceExpiry", Text("LicenceExpiry"));
                Set("tachoCardNumber", Text("TachoCardNumber"));
                Set("tachoMasterDriverId", Text("TachoMasterDriverId"));
                Set("lastTachoSyncUtc", Text("LastTachoSyncUtc"));
                Set("active", Text("Active"));
                break;
            case "vehicle":
                Set("registration", Text("Registration") ?? Text("VehicleKey"));
                Set("fleetNumber", Text("FleetNumber") ?? Text("VehicleKey"));
                Set("abbreviation", Text("Abbreviation"));
                Set("transmission", Text("Transmission"));
                Set("dvsCompliant", Text("DvsCompliant"));
                Set("fuelProvider", Text("FuelProvider"));
                Set("cabMobile", Text("CabMobile"));
                Set("fuelPinSecretName", Text("FuelPinSecretName"));
                Set("fuelCardLastFour", Text("FuelCardLastFour"));
                Set("shellCard", Text("ShellCard"));
                Set("bpRedCard", Text("BpRedCard"));
                Set("bpPlainCard", Text("BpPlainCard"));
                Set("notes", Text("Notes"));
                Set("fleetioId", Text("FleetioId"));
                Set("fleetioName", Text("FleetioName"));
                Set("fleetioStatus", Text("FleetioStatus"));
                Set("active", Text("Active"));
                break;
            case "trailer":
                Set("trailerNumber", Text("Registration") ?? Text("TrailerKey"));
                Set("type", Text("TrailerType"));
                Set("standardCapacity", Text("StandardCapacity"));
                Set("euroCapacity", Text("EuroCapacity"));
                Set("notes", Text("Notes"));
                Set("active", Text("Active"));
                break;
            case "marketcontact":
                Set("market", Text("Market"));
                Set("name", Text("Name"));
                Set("standOrLocation", Text("StandOrLocation"));
                Set("salesman", Text("Salesman"));
                Set("sender", Text("Sender"));
                Set("readOnlyMapPdfUrl", Text("ReadOnlyMapPdfUrl"));
                Set("active", Text("Active"));
                break;
            case "emailroute":
                Set("id", Text("RouteKey"));
                Set("customerCode", Text("CustomerKey"));
                Set("defaultSiteCode", Text("SiteKey"));
                Set("senderEmail", Text("SenderEmail"));
                Set("senderDomain", Text("SenderDomain"));
                Set("subjectContains", Text("SubjectContains"));
                Set("parserType", Text("ParserType"));
                Set("requiresReview", Text("RequiresReview"));
                Set("active", Text("Active"));
                break;
        }

        using var document = JsonDocument.Parse(values.ToJsonString());
        return document.RootElement.Clone();
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            })
        };
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw Failure("GraphAuthenticationFailed", "Microsoft Graph rejected the SLH SharePoint integration credential", response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()
            ?? throw new SharePointMasterDataException("GraphAuthenticationFailed", "Microsoft Graph returned no access token.");
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(settings.TenantId) || string.IsNullOrWhiteSpace(settings.ClientId)
            || string.IsNullOrWhiteSpace(settings.ClientSecret) || string.IsNullOrWhiteSpace(settings.Hostname)
            || string.IsNullOrWhiteSpace(settings.SitePath) || settings.Lists.Count == 0)
            throw new SharePointMasterDataException("SharePointConfigurationMissing", "The SLH SharePoint integration is not fully configured. Add the Microsoft Graph app credential before publishing or syncing Lists.");
    }

    private SharePointMasterDataException Failure(string code, string action, System.Net.HttpStatusCode status, string body)
    {
        var guidance = status switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Check the Microsoft Graph app secret and tenant ID.",
            System.Net.HttpStatusCode.Forbidden => "Grant the Microsoft Graph app access to the SLH Hub site and its Lists.",
            System.Net.HttpStatusCode.NotFound => "Check that the SLH Hub site and required List exist.",
            _ => "Check the SLH SharePoint integration log for the Graph response."
        };
        logger.LogError("{Action} failed. GraphStatus={GraphStatus}; GraphBody={GraphBody}", action, (int)status, body);
        var graphDetail = body.Length > 800 ? body[..800] : body;
        return new SharePointMasterDataException(code, $"{action} (Microsoft Graph {(int)status}). {guidance} Graph detail: {graphDetail}");
    }
}
