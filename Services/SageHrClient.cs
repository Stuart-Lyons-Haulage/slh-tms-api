using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

/// <summary>Read-only Sage HR employee client. Credentials are runtime-only Key Vault values.</summary>
public sealed class SageHrClient(HttpClient httpClient, SageHrOptions options, ILogger<SageHrClient> logger)
{
    public bool IsConfigured => options.Enabled && !string.IsNullOrWhiteSpace(options.BaseUrl) && !string.IsNullOrWhiteSpace(options.ApiKey);
    public bool IsEnabled => options.Enabled;
    public string DriverTeamName => options.DriverTeamName;
    public string DriverPositionKeyword => options.DriverPositionKeyword;
    public IReadOnlyList<string> MissingSettings
    {
        get
        {
            var missing = new List<string>();
            if (!options.Enabled) missing.Add("Integrations:SageHr:Enabled");
            if (string.IsNullOrWhiteSpace(options.BaseUrl)) missing.Add("Integrations:SageHr:BaseUrl");
            if (string.IsNullOrWhiteSpace(options.ApiKey)) missing.Add("Integrations:SageHr:ApiKey");
            return missing;
        }
    }

    public async Task<IReadOnlyList<SageHrEmployee>> GetActiveEmployeesAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) throw new InvalidOperationException("Sage HR integration is disabled.");
        if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Sage HR runtime settings are incomplete.");
        httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        var employees = new List<SageHrEmployee>();
        for (var page = 1; page <= 100; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"employees?page={page}&team_history=true&employment_status_history=true&position_history=true");
            request.Headers.Add("X-Auth-Token", options.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SageHrEmployeePage>(stream, SageHrJson.Options, cancellationToken)
                ?? throw new InvalidOperationException("Sage HR returned an empty employee response.");
            employees.AddRange(payload.Data);
            if (payload.Meta?.NextPage is null || payload.Data.Count == 0) break;
        }
        logger.LogInformation("Retrieved {Count} active Sage HR employees.", employees.Count);
        return employees;
    }
}

public sealed record SageHrEmployee(
    long Id,
    [property: JsonPropertyName("employee_number")] string? EmployeeNumber,
    [property: JsonPropertyName("first_name")] string FirstName,
    [property: JsonPropertyName("last_name")] string LastName,
    string? Team,
    string? Position,
    [property: JsonPropertyName("mobile_phone")] string? MobilePhone);
public sealed record SageHrEmployeePage(List<SageHrEmployee> Data, SageHrMeta? Meta);
public sealed record SageHrMeta([property: JsonPropertyName("next_page")] int? NextPage);
internal static class SageHrJson { internal static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true }; }
