using System.Net.Http.Headers;
using System.Text.Json;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

/// <summary>Read-only Sage HR employee client. Credentials are runtime-only Key Vault values.</summary>
public sealed class SageHrClient(HttpClient httpClient, SageHrOptions options, ILogger<SageHrClient> logger)
{
    public async Task<JsonDocument> GetActiveEmployeesAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) throw new InvalidOperationException("Sage HR integration is disabled.");
        if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Sage HR runtime settings are incomplete.");
        httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        using var request = new HttpRequestMessage(HttpMethod.Get, "employees?team_history=true&employment_status_history=true&position_history=true");
        request.Headers.Add("X-Auth-Token", options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        logger.LogInformation("Retrieved Sage HR employee data.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
