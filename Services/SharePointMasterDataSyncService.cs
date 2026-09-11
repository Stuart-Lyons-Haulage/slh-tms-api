using System.Net.Http.Headers;
using System.Text.Json;
using Slh.Tms.Api.Contracts;

namespace Slh.Tms.Api.Services;

public sealed class SharePointMasterDataOptions
{
    public string TenantId { get; set; } = "5aec48a1-c3c7-4cfd-a073-b38ae50041b1";
    public string ClientId { get; set; } = "e52218ab-0a5a-459a-84b2-423a83152582";
    public string ClientSecret { get; set; } = string.Empty;
    public string Hostname { get; set; } = "stuartlyonshaulage.sharepoint.com";
    public string SitePath { get; set; } = "/";
    public Dictionary<string, string> Lists { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SharePointMasterDataOptions()
    {
        Lists["customer"] = "Customers";
        Lists["site"] = "Sites";
        Lists["driver"] = "Drivers";
        Lists["vehicle"] = "Vehicles";
        Lists["sitetimingrule"] = "Run Timings";
        Lists["sitegeofence"] = "Geofences";
    }
}

public sealed record SharePointMasterDataSyncResult(int ListsRead, int RowsRead, IReadOnlyList<StageImportRequest> Requests);

public sealed class SharePointMasterDataSyncService(
    HttpClient http,
    SharePointMasterDataOptions options,
    ILogger<SharePointMasterDataSyncService> logger)
{
    private readonly SharePointMasterDataOptions settings = options;

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
            var rows = await ReadListAsync(mapping.Key, mapping.Value, token, ct);
            foreach (var row in rows)
            {
                var itemId = row.TryGetProperty("id", out var id) ? id.ToString() : Guid.NewGuid().ToString("N");
                var fields = row.TryGetProperty("fields", out var f) ? f : row;
                requests.Add(new StageImportRequest(mapping.Key, $"sharepoint:{mapping.Key}:{itemId}", fields, "Microsoft Lists / SharePoint"));
            }
        }
        logger.LogInformation("Read {RowsRead} master-data rows from {ListsRead} Microsoft Lists.", requests.Count, listsRead);
        return new SharePointMasterDataSyncResult(listsRead, requests.Count, requests);
    }

    private async Task<JsonElement[]> ReadListAsync(string entityType, string listId, string token, CancellationToken ct)
    {
        var sitePath = settings.SitePath.Trim('/');
        var siteSelector = string.IsNullOrWhiteSpace(sitePath) ? settings.Hostname : $"{settings.Hostname}:/{sitePath}:";
        var listSelector = Uri.EscapeDataString(listId);
        var uri = $"https://graph.microsoft.com/v1.0/sites/{siteSelector}/lists/{listSelector}/items?expand=fields&$top=999";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Microsoft List '{entityType}' could not be read ({(int)response.StatusCode}).");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("value", out var value) ? value.EnumerateArray().Select(x => x.Clone()).ToArray() : [];
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
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Microsoft Graph authentication failed.");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Microsoft Graph returned no access token.");
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(settings.TenantId) || string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret) || string.IsNullOrWhiteSpace(settings.Hostname) || string.IsNullOrWhiteSpace(settings.SitePath) || settings.Lists.Count == 0)
            throw new InvalidOperationException("SharePoint master-data sync is not configured. Set TenantId, ClientId, ClientSecret, Hostname, SitePath and Lists.");
    }
}
