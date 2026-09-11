using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Slh.Tms.MasterDataSync;

public sealed class TmsCacheInvalidationClient(HttpClient http, IOptions<SyncOptions> options)
{
    private readonly SyncOptions settings = options.Value;

    public async Task InvalidateAsync(string listKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.TmsCacheInvalidateUrl))
            throw new InvalidOperationException("TmsCacheInvalidateUrl is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TmsCacheInvalidateUrl);
        if (!string.IsNullOrWhiteSpace(settings.TmsCacheInvalidateToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.TmsCacheInvalidateToken);
        request.Content = JsonContent.Create(new { list = listKey, invalidatedAtUtc = DateTime.UtcNow });
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"TMS cache invalidation failed with {(int)response.StatusCode}.");
    }
}
