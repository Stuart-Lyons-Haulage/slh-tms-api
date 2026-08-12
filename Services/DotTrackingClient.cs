using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

/// <summary>
/// RoadTech Falcon client. The provider credentials are runtime-only values and
/// must never be committed to source control.
/// </summary>
public sealed class DotTrackingClient
{
    private const string ProviderName = "RoadTech Falcon";
    private readonly HttpClient _httpClient;
    private readonly DotTrackingOptions _options;
    private readonly ILogger<DotTrackingClient> _logger;

    public DotTrackingClient(HttpClient httpClient, DotTrackingOptions options, ILogger<DotTrackingClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;

        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(NormaliseBaseUrl(_options.BaseUrl));
        }
    }

    /// <summary>
    /// Reads the latest telemetry for the configured RoadTech company.
    /// RoadTech requires an APIKEY header, a login SID and a POST request to
    /// /api/Falcon/GetCurrentTelemetry.
    /// </summary>
    public async Task<IReadOnlyList<RoadTechTelemetryItem>> GetLatestVehicleEventsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("{Provider} tracking is disabled.", ProviderName);
            return [];
        }

        ValidateConfiguration();

        var sid = await LoginAsync(cancellationToken);
        var results = new List<RoadTechTelemetryItem>();
        var offset = 0;

        for (var page = 0; page < _options.MaxPages; page++)
        {
            var response = await GetTelemetryPageAsync(sid, offset, cancellationToken);
            results.AddRange(response.Data);

            if (!response.MoreData || response.RecordCount == 0)
            {
                break;
            }

            offset += response.RecordCount;
        }

        _logger.LogInformation("{Provider} returned {Count} current telemetry records.", ProviderName, results.Count);
        return results;
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) ||
            string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.Username) ||
            string.IsNullOrWhiteSpace(_options.Password) ||
            string.IsNullOrWhiteSpace(_options.CompanyCode))
        {
            throw new InvalidOperationException(
                "RoadTech tracking is enabled but one or more required runtime settings are missing.");
        }
    }

    private async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        var passwordAttempts = LoginPasswordAttempts(_options.Password);
        List<string> failures = [];
        foreach (var password in passwordAttempts)
        {
            using var request = CreateLoginRequest(password);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                return ExtractSessionId(payload);
            }

            failures.Add(await RoadTechFailureDetail(response, "auth/login", cancellationToken));
            if (response.StatusCode is not (HttpStatusCode.InternalServerError or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest))
            {
                break;
            }
        }

        throw new HttpRequestException(string.Join(" Then ", failures));
    }


    private HttpRequestMessage CreateLoginRequest(string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "auth/login");
        request.Headers.Add("APIKEY", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new RoadTechLoginRequest(
            _options.Username,
            password,
            "SLH TMS API",
            "Azure App Service",
            Environment.MachineName,
            "password"), options: RoadTechJson.Options);
        return request;
    }

    private static IReadOnlyList<string> LoginPasswordAttempts(string password)
    {
        var trimmed = password.Trim();
        if (IsProviderHash(trimmed)) return [trimmed];
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(trimmed));
        return [trimmed, Convert.ToHexString(bytes).ToLowerInvariant()];
    }

    private static bool IsProviderHash(string value) => value.All(Uri.IsHexDigit) && value.Length is 32 or 40 or 64;

    private async Task<RoadTechTelemetryPage> GetTelemetryPageAsync(
        string sid,
        int offset,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "Falcon/GetCurrentTelemetry");
        request.Headers.Add("APIKEY", _options.ApiKey);
        request.Headers.Add("SID", sid);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new RoadTechTelemetryRequest(
            _options.CompanyCode,
            DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            _options.DataMask,
            offset,
            _options.OnlyLive), options: RoadTechJson.Options);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await RoadTechFailureDetail(response, "Falcon/GetCurrentTelemetry", cancellationToken), null, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var page = await JsonSerializer.DeserializeAsync<RoadTechTelemetryPage>(
            stream,
            RoadTechJson.Options,
            cancellationToken);

        return page ?? throw new InvalidOperationException("RoadTech returned an empty telemetry response.");
    }

    private static string ExtractSessionId(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Number)
        {
            return root.GetRawText();
        }

        if (root.ValueKind == JsonValueKind.String)
        {
            return root.GetString() ?? throw new InvalidOperationException("RoadTech returned an empty SID.");
        }

        foreach (var name in new[] { "token", "sid", "SID" })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                return value.ToString();
            }
        }

        throw new InvalidOperationException("RoadTech login response did not contain a SID.");
    }

    private sealed record RoadTechLoginRequest(
        string User,
        string Pass,
        string OsVersion,
        string OsName,
        string PcName,
        string AuthType);

    private sealed record RoadTechTelemetryRequest(
        string CompCode,
        string T,
        int DataMask,
        int Offset,
        int OnlyLive);

    public static string NormaliseBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? $"{trimmed}/" : $"{trimmed}/api/";
    }

    private static async Task<string> RoadTechFailureDetail(HttpResponseMessage response, string endpoint, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
        if (detail is not null && detail.Length > 500) detail = detail[..500];
        return $"RoadTech {endpoint} returned {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}";
    }
}

public sealed class RoadTechTelemetryPage
{
    public bool MoreData { get; init; }
    public int RecordOffset { get; init; }
    public int RecordCount { get; init; }
    public List<RoadTechTelemetryItem> Data { get; init; } = [];
}

internal static class RoadTechJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
