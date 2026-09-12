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
        // These are the governed Lists provisioned in the SLH Hub.  Keep the
        // operational entity name on the left: it is the contract used by SQL.
        Lists["customer"] = "Hub Customers";
        Lists["site"] = "Hub Sites";
        Lists["driver"] = "Hub Drivers";
        Lists["vehicle"] = "Hub Vehicles";
        Lists["trailer"] = "Hub Trailers";
        Lists["marketcontact"] = "TMS Markets";
    }
}

public sealed record SharePointMasterDataSyncResult(int ListsRead, int RowsRead, IReadOnlyList<StageImportRequest> Requests);
public sealed record SharePointMasterDataPublishResult(int ListsWritten, int RowsWritten, IReadOnlyDictionary<string, int> RowsByList);

/// <summary>Safe, supportable failures returned to the portal without exposing credentials or Graph responses.</summary>
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

    public async Task<SharePointMasterDataPublishResult> PublishFromSqlAsync(TmsDbContext db, CancellationToken ct)
    {
        ValidateConfiguration();
        var token = await GetTokenAsync(ct);
        var rows = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["customer"] = (await db.Customers.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct)).Select(x => Fields(
                ("Title", x.Code), ("CustomerKey", x.Code), ("TradingName", x.TradingName ?? x.Name), ("AccountOwner", x.AccountOwner), ("ServiceNotes", x.ServiceNotes), ("Active", x.Active))).ToArray(),
            ["site"] = await BuildSiteRowsAsync(db, ct),
            ["driver"] = (await db.Drivers.AsNoTracking().OrderBy(x => x.EmployeeNumber).ToListAsync(ct)).Select(x => Fields(
                ("Title", x.EmployeeNumber), ("DriverKey", x.EmployeeNumber), ("DriverName", x.DisplayName), ("EmployeeNumber", x.EmployeeNumber), ("LicenceNumber", x.DrivingLicenceNumber), ("Active", x.Active), ("ComplianceStatus", string.IsNullOrWhiteSpace(x.LicenceStatus) ? "Unknown" : x.LicenceStatus))).ToArray(),
            ["vehicle"] = (await db.Vehicles.AsNoTracking().OrderBy(x => x.Registration).ToListAsync(ct)).Select(x => Fields(
                ("Title", x.Registration), ("VehicleKey", x.FleetNumber ?? x.Registration), ("Registration", x.Registration), ("Active", x.Active), ("ComplianceStatus", "Unknown"))).ToArray(),
            ["trailer"] = (await db.Trailers.AsNoTracking().OrderBy(x => x.TrailerNumber).ToListAsync(ct)).Select(x => Fields(
                ("Title", x.TrailerNumber), ("TrailerKey", x.TrailerNumber), ("Registration", x.TrailerNumber), ("TrailerType", x.Type), ("StandardCapacity", x.StandardCapacity), ("EuroCapacity", x.EuroCapacity), ("Active", x.Active))).ToArray(),
            ["marketcontact"] = (await db.MarketContacts.AsNoTracking().OrderBy(x => x.Market).ThenBy(x => x.Name).ToListAsync(ct)).Select(x => Fields(
                ("Title", $"{x.Market} · {x.Name}"), ("MarketKey", $"{x.Market}|{x.Name}"), ("Market", x.Market), ("Name", x.Name), ("StandOrLocation", x.StandOrLocation), ("Salesman", x.Salesman), ("Sender", x.Sender), ("ReadOnlyMapPdfUrl", x.ReadOnlyMapPdfUrl), ("Active", x.Active))).ToArray()
        };

        var rowsByList = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in settings.Lists)
        {
            if (!rows.TryGetValue(mapping.Key, out var sourceRows) || string.IsNullOrWhiteSpace(mapping.Value)) continue;
            var listId = await ResolveListIdAsync(mapping.Value, token, ct);
            var existing = await ReadListAsync(mapping.Key, listId, token, ct);
            var keyField = mapping.Key switch { "customer" => "CustomerKey", "site" => "SiteKey", "driver" => "DriverKey", "vehicle" => "VehicleKey", "trailer" => "TrailerKey", "marketcontact" => "MarketKey", _ => "Title" };
            var existingByKey = existing
                .Where(item => item.TryGetProperty("fields", out var field) && field.TryGetProperty(keyField, out var key) && !string.IsNullOrWhiteSpace(key.ToString()))
                .ToDictionary(item => item.GetProperty("fields").GetProperty(keyField).ToString(), StringComparer.OrdinalIgnoreCase);
            foreach (var source in sourceRows)
            {
                var key = source[keyField]?.ToString();
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (existingByKey.TryGetValue(key, out var current))
                    await UpdateListItemAsync(listId, current.GetProperty("id").ToString(), source, token, ct);
                else
                    await CreateListItemAsync(listId, source, token, ct);
            }
            rowsByList[mapping.Key] = sourceRows.Count;
        }
        return new SharePointMasterDataPublishResult(rowsByList.Count, rowsByList.Values.Sum(), rowsByList);
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
            var listId = await ResolveListIdAsync(mapping.Value, token, ct);
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

    /// <summary>Writes a learned site alias back to the CRM without making the planner wait for it.</summary>
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

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> BuildSiteRowsAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().OrderBy(x => x.ExternalCode).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        var geofences = await db.SiteGeofences.AsNoTracking().Where(g => g.Active && g.SiteId != null).ToDictionaryAsync(g => g.SiteId!.Value, g => g.Id.ToString(), ct);
        return sites.Select(x => Fields(
            ("Title", x.ExternalCode), ("SiteKey", x.ExternalCode), ("CustomerKey", x.CustomerCode), ("SiteName", x.Name), ("BuildingName", x.DriverTextName ?? x.Name), ("Address1", x.CollectionAddress), ("MapLink", x.MapLink), ("Aliases", x.Aliases), ("GeofenceId", geofences.GetValueOrDefault(x.Id)), ("Active", x.Active), ("SyncStatus", "Synced"))).ToArray();
    }

    private async Task<JsonElement[]> ReadListAsync(string entityType, string listId, string token, CancellationToken ct)
    {
        var sitePath = settings.SitePath.Trim('/');
        var siteSelector = string.IsNullOrWhiteSpace(sitePath) ? settings.Hostname : $"{settings.Hostname}:/{sitePath}:";
        var listSelector = Uri.EscapeDataString(listId);
        string? uri = $"https://graph.microsoft.com/v1.0/sites/{siteSelector}/lists/{listSelector}/items?expand=fields&$top=999";
        var rows = new List<JsonElement>();
        while (!string.IsNullOrWhiteSpace(uri))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw Failure("ListReadFailed", $"Microsoft List '{entityType}' could not be read", response.StatusCode, body);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("value", out var value)) rows.AddRange(value.EnumerateArray().Select(x => x.Clone()));
            uri = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink) ? nextLink.GetString() : null;
        }
        return rows.ToArray();
    }

    private async Task<string> ResolveListIdAsync(string listName, string token, CancellationToken ct)
    {
        var sitePath = settings.SitePath.Trim('/');
        var siteSelector = string.IsNullOrWhiteSpace(sitePath) ? settings.Hostname : $"{settings.Hostname}:/{sitePath}:";
        var filter = Uri.EscapeDataString($"displayName eq '{listName.Replace("'", "''")}'");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/sites/{siteSelector}/lists?$filter={filter}&$select=id,displayName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw Failure("ListResolveFailed", $"Microsoft List '{listName}' could not be resolved", response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var match = document.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        return match.ValueKind == JsonValueKind.Undefined ? throw new SharePointMasterDataException("ListNotFound", $"The required Microsoft List '{listName}' was not found on the SLH Hub site. Run the SLH Hub List provisioning script, then try again.") : match.GetProperty("id").GetString()!;
    }

    private async Task CreateListItemAsync(string listId, IReadOnlyDictionary<string, object?> fields, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildListUrl(listId, "items"))
        {
            Content = JsonContent.Create(new { fields })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw Failure("ListCreateFailed", "A Microsoft List item could not be created", response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task UpdateListItemAsync(string listId, string itemId, IReadOnlyDictionary<string, object?> fields, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PATCH"), BuildListUrl(listId, $"items/{Uri.EscapeDataString(itemId)}/fields"))
        {
            Content = JsonContent.Create(fields)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw Failure("ListUpdateFailed", "A Microsoft List item could not be updated", response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private string BuildListUrl(string listId, string suffix)
    {
        var sitePath = settings.SitePath.Trim('/');
        var siteSelector = string.IsNullOrWhiteSpace(sitePath) ? settings.Hostname : $"{settings.Hostname}:/{sitePath}:";
        return $"https://graph.microsoft.com/v1.0/sites/{siteSelector}/lists/{Uri.EscapeDataString(listId)}/{suffix}";
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
                break;
            case "site":
                Set("externalCode", Text("SiteKey"));
                Set("customerCode", Text("CustomerKey"));
                Set("name", Text("SiteName") ?? Text("BuildingName") ?? Text("SiteKey"));
                Set("driverTextName", Text("BuildingName") ?? Text("SiteName"));
                Set("collectionAddress", string.Join(", ", new[] { Text("Address1"), Text("Address2"), Text("Town"), Text("County"), Text("Postcode") }.Where(value => !string.IsNullOrWhiteSpace(value))));
                Set("mapLink", Text("MapLink"));
                Set("aliases", Text("Aliases"));
                break;
            case "driver":
                Set("employeeNumber", Text("EmployeeNumber") ?? Text("DriverKey"));
                Set("displayName", Text("DriverName") ?? Text("DriverKey"));
                Set("drivingLicenceNumber", Text("LicenceNumber"));
                break;
            case "vehicle":
                Set("registration", Text("Registration") ?? Text("VehicleKey"));
                Set("fleetNumber", Text("VehicleKey"));
                break;
            case "trailer":
                Set("trailerNumber", Text("Registration") ?? Text("TrailerKey"));
                Set("type", Text("TrailerType"));
                Set("standardCapacity", Text("Capacity"));
                break;
            case "marketcontact":
                Set("market", Text("Market"));
                Set("name", Text("Name"));
                Set("standOrLocation", Text("StandOrLocation"));
                Set("salesman", Text("Salesman"));
                Set("sender", Text("Sender"));
                Set("readOnlyMapPdfUrl", Text("ReadOnlyMapPdfUrl"));
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
        if (!response.IsSuccessStatusCode) throw Failure("GraphAuthenticationFailed", "Microsoft Graph rejected the SLH SharePoint integration credential", response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString() ?? throw new SharePointMasterDataException("GraphAuthenticationFailed", "Microsoft Graph returned no access token.");
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(settings.TenantId) || string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret) || string.IsNullOrWhiteSpace(settings.Hostname) || string.IsNullOrWhiteSpace(settings.SitePath) || settings.Lists.Count == 0)
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
        return new SharePointMasterDataException(code, $"{action} (Microsoft Graph {(int)status}). {guidance}");
    }
}
