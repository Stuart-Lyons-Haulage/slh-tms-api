using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace Slh.Tms.Api.Services;

public sealed class AzureMapsRouteClient(HttpClient client, IConfiguration configuration)
{
    private static readonly TokenRequestContext TokenContext = new(["https://atlas.microsoft.com/.default"]);
    public async Task<object> Directions(IReadOnlyList<(decimal Longitude, decimal Latitude)> points, CancellationToken ct)
    {
        if (points.Count < 2) throw new ArgumentException("At least two mapped stops are required.");
        var token = await new DefaultAzureCredential().GetTokenAsync(TokenContext, ct);
        var query = string.Join(':', points.Select(point => $"{point.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{point.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        var endpoint = configuration["Maps:Endpoint"] ?? "https://atlas.microsoft.com";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/route/directions/json?api-version=1.0&query={Uri.EscapeDataString(query)}");
        request.Headers.Authorization = new("Bearer", token.Token);
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return document.RootElement.Clone();
    }

    public async Task<object> SearchAddress(string address, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("An address is required.");
        var token = await new DefaultAzureCredential().GetTokenAsync(TokenContext, ct);
        var endpoint = configuration["Maps:Endpoint"] ?? "https://atlas.microsoft.com";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/search/address/json?api-version=1.0&limit=1&query={Uri.EscapeDataString(address.Trim())}");
        request.Headers.Authorization = new("Bearer", token.Token);
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return document.RootElement.Clone();
    }

    public async Task<TimeSpan> TravelTime((decimal Longitude, decimal Latitude) from, (decimal Longitude, decimal Latitude) to, CancellationToken ct)
    {
        var result = await Directions([from, to], ct);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        if (!document.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0 ||
            !routes[0].TryGetProperty("summary", out var summary) || !summary.TryGetProperty("travelTimeInSeconds", out var seconds))
            throw new InvalidOperationException("Azure Maps did not return a route travel time.");
        return TimeSpan.FromSeconds(seconds.GetDouble());
    }
}
