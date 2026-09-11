using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Slh.Tms.MasterDataSync;

public sealed class GraphSharePointClient(HttpClient http, IOptions<SyncOptions> options, ILogger<GraphSharePointClient> logger)
{
    private readonly SyncOptions settings = options.Value;

    public async Task<IReadOnlyList<SharePointItem>> ReadListAsync(MasterListDefinition definition, CancellationToken ct)
    {
        Validate();
        var token = await GetTokenAsync(ct);
        var siteSelector = string.IsNullOrWhiteSpace(settings.SitePath)
            ? settings.Hostname
            : $"{settings.Hostname}:/{settings.SitePath.Trim('/')}:";
        var listId = await ResolveListIdAsync(siteSelector, definition.ListName, token, ct);
        var uri = $"https://graph.microsoft.com/v1.0/sites/{siteSelector}/lists/{Uri.EscapeDataString(listId)}/items?expand=fields&$top=999";
        var rows = new List<SharePointItem>();

        while (!string.IsNullOrWhiteSpace(uri))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Graph read failed for {definition.ListName}: {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            foreach (var item in document.RootElement.GetProperty("value").EnumerateArray())
                rows.Add(SharePointItem.FromGraph(item));
            uri = document.RootElement.TryGetProperty("@odata.nextLink", out var next)
                ? next.GetString()
                : null;
        }

        logger.LogInformation("Graph returned {Count} items for {ListName}.", rows.Count, definition.ListName);
        return rows;
    }

    private async Task<string> ResolveListIdAsync(string siteSelector, string listName, string token, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"displayName eq '{listName.Replace("'", "''")}'");
        var uri = $"https://graph.microsoft.com/v1.0/sites/{siteSelector}/lists?$filter={filter}&$select=id,displayName";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Graph list lookup failed for {listName}: {(int)response.StatusCode}.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var match = document.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        if (match.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"SharePoint list '{listName}' was not found.");
        return match.GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"SharePoint list '{listName}' returned no ID.");
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/token")
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
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Microsoft Graph authentication failed.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Microsoft Graph returned no access token.");
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(settings.TenantId) ||
            string.IsNullOrWhiteSpace(settings.ClientId) ||
            string.IsNullOrWhiteSpace(settings.ClientSecret) ||
            string.IsNullOrWhiteSpace(settings.Hostname))
            throw new InvalidOperationException("MasterDataSync Graph settings are incomplete.");
    }
}

public sealed record SharePointItem(int Id, string? ETag, IReadOnlyDictionary<string, string?> Fields)
{
    public static SharePointItem FromGraph(JsonElement item)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("fields", out var fieldsElement))
            foreach (var property in fieldsElement.EnumerateObject())
                fields[property.Name] = Scalar(property.Value);

        var etag = item.TryGetProperty("eTag", out var tag) ? tag.GetString() : null;
        return new SharePointItem(item.GetProperty("id").GetInt32(), etag, fields);
    }

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.ToString()
    };
}
