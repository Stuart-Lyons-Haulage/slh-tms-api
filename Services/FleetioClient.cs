using System.Net.Http.Headers;
using System.Text.Json;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

public sealed class FleetioClient(HttpClient httpClient, FleetioOptions options, ILogger<FleetioClient> logger)
{
    public bool IsConfigured => options.IsConfigured;
    public string[] MissingSettings => options.MissingSettings;

    public async Task<FleetioVehicleSummary> GetVehicleSummaryAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Fleetio runtime settings are incomplete.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/vehicles?per_page=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", options.ApiKey);
        request.Headers.Add("Account-Token", options.AccountToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Fleetio vehicles returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}", null, response.StatusCode);

        var count = 0;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Array) count = document.RootElement.GetArrayLength();
        }
        catch (JsonException exception)
        {
            logger.LogDebug(exception, "Fleetio vehicle response was not a plain array; status check will still report connected.");
        }

        return new FleetioVehicleSummary(true, count);
    }
}

public sealed record FleetioVehicleSummary(bool Connected, int SampleVehicleCount);
