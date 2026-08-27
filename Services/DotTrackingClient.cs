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
    private const string CurrentTelemetryEndpoint = "Falcon/GetCurrentTelemetry";
    private const string HistoricalTelemetryEndpoint = "Falcon/GetHistoricalTelemetry";
    private static readonly TimeSpan CurrentTelemetryBudget = TimeSpan.FromSeconds(8);
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
    /// /api/Falcon/GetCurrentTelemetry. Current telemetry is deliberately bounded
    /// so a slow provider cannot hold wallboard/ETA requests open indefinitely.
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

        using var currentRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        currentRequest.CancelAfter(CurrentTelemetryBudget);
        try
        {
            var sid = await LoginAsync(currentRequest.Token);
            var results = new List<RoadTechTelemetryItem>();
            var offset = 0;

            for (var page = 0; page < _options.MaxPages; page++)
            {
                var response = await GetTelemetryPageAsync(
                    CurrentTelemetryEndpoint,
                    sid,
                    DateOnly.FromDateTime(DateTime.UtcNow),
                    offset,
                    _options.OnlyLive ? 1 : 0,
                    currentRequest.Token);
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
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{ProviderName} current telemetry exceeded the {CurrentTelemetryBudget.TotalSeconds:0}-second live request budget.", exception);
        }
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

    /// <summary>
    /// Replays a complete Falcon operating day through RoadTech's documented
    /// /api/Falcon/GetHistoricalTelemetry endpoint. This is intentionally separate
    /// from GetCurrentTelemetry: a current fleet snapshot is not a movement trail
    /// and therefore cannot reconstruct geofence ENTER/EXIT crossings.
    /// Offset pagination is preserved so later journeys cannot silently disappear.
    /// </summary>
    public async Task<IReadOnlyList<RoadTechTelemetryItem>> GetHistoricalVehicleEventsAsync(
        DateOnly day,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return [];
        ValidateConfiguration();

        var sid = await LoginAsync(cancellationToken);
        var results = new List<RoadTechTelemetryItem>();
        var offset = 0;

        for (var page = 0; page < _options.MaxPages; page++)
        {
            var response = await GetTelemetryPageAsync(
                HistoricalTelemetryEndpoint,
                sid,
                day,
                offset,
                0,
                cancellationToken);
            results.AddRange(response.Data);

            if (!response.MoreData || response.RecordCount == 0)
            {
                break;
            }

            offset += response.RecordCount;
        }

        _logger.LogInformation(
            "{Provider} returned {Count} historical telemetry records for {Day}.",
            ProviderName,
            results.Count,
            day);
        return results;
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
            "1.0",
            "Azure Container App",
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
        string endpoint,
        string sid,
        DateOnly day,
        int offset,
        int onlyLive,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("APIKEY", _options.ApiKey);
        request.Headers.Add("SID", sid);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new RoadTechTelemetryRequest(
            _options.CompanyCode,
            day.ToString("yyyy-MM-dd"),
            _options.DataMask,
            offset,
            onlyLive), options: RoadTechJson.Options);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                await RoadTechFailureDetail(response, endpoint, cancellationToken),
                null,
                response.StatusCode);

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

        if (TryExtractSessionId(root, out var sessionId))
        {
            return sessionId;
        }

        throw new InvalidOperationException("RoadTech login response did not contain a SID.");
    }

    private static bool TryExtractSessionId(JsonElement element, out string sessionId)
    {
        sessionId = string.Empty;

        if (element.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            sessionId = element.ToString();
            return !string.IsNullOrWhiteSpace(sessionId);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("token") ||
                property.NameEquals("Token") ||
                property.NameEquals("sid") ||
                property.NameEquals("Sid") ||
                property.NameEquals("SID") ||
                property.NameEquals("sessionId") ||
                property.NameEquals("SessionId") ||
                property.NameEquals("sessionID") ||
                property.NameEquals("SessionID"))
            {
                sessionId = property.Value.ToString();
                return !string.IsNullOrWhiteSpace(sessionId);
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if ((property.NameEquals("data") ||
                 property.NameEquals("Data") ||
                 property.NameEquals("result") ||
                 property.NameEquals("Result") ||
                 property.NameEquals("session") ||
                 property.NameEquals("Session")) &&
                TryExtractSessionId(property.Value, out sessionId))
            {
                return true;
            }
        }

        return false;
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
