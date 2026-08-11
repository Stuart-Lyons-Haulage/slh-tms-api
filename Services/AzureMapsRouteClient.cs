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
}
