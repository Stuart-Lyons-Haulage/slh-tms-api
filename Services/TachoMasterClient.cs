using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class TachoMasterClient
{
    private readonly HttpClient httpClient;
    private readonly TachoMasterOptions options;
    private readonly ILogger<TachoMasterClient> logger;

    public TachoMasterClient(HttpClient httpClient, TachoMasterOptions options, ILogger<TachoMasterClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger;
        this.httpClient.Timeout = TimeSpan.FromSeconds(30);
        this.httpClient.BaseAddress = new Uri(NormaliseBaseUrl(options.BaseUrl));
    }

    public bool IsConfigured => options.IsConfigured;
    public IReadOnlyList<string> MissingSettings => new[]
    {
        !options.Enabled ? "TachoMaster enabled flag" : null,
        string.IsNullOrWhiteSpace(options.BaseUrl) ? "TachoMaster base URL" : null,
        string.IsNullOrWhiteSpace(options.ApiKey) ? "TachoMaster API key" : null,
        string.IsNullOrWhiteSpace(options.Username) ? "TachoMaster username" : null,
        string.IsNullOrWhiteSpace(options.Password) ? "TachoMaster password" : null
    }.Where(value => value is not null).Select(value => value!).ToList();

    public async Task<IReadOnlyDictionary<string, string>> GetCurrentDriverNamesByVehicleAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured) return new Dictionary<string, string>();

        var sid = await LoginAsync(cancellationToken);
        var duties = await GetDutiesAsync(sid, date, cancellationToken);
        var currentDuties = duties
            .Where(duty => !string.IsNullOrWhiteSpace(duty.VehCode) && duty.MemCode > 0)
            .Where(duty => duty.DutyStart.Date == date.ToDateTime(TimeOnly.MinValue).Date)
            .GroupBy(duty => NormaliseIdentifier(duty.VehCode))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(duty => duty.DutyStart).First());
        if (currentDuties.Count == 0) return new Dictionary<string, string>();

        var members = await GetMembersAsync(sid, cancellationToken);
        var names = members
            .Where(member => currentDuties.Values.Any(duty => duty.MemCode == member.MemCode))
            .ToDictionary(member => member.MemCode, DriverName);

        var result = currentDuties
            .Where(item => names.ContainsKey(item.Value.MemCode) && !string.IsNullOrWhiteSpace(names[item.Value.MemCode]))
            .ToDictionary(item => item.Key, item => names[item.Value.MemCode]);
        logger.LogDebug("TachoMaster matched {Count} current vehicle duty records to drivers.", result.Count);
        return result;
    }

    private async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        foreach (var password in PasswordAttempts(options.Password))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/login");
            request.Headers.Add("APIKEY", options.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = JsonContent.Create(new
            {
                User = options.Username,
                Pass = password,
                AppName = "SLH TMS API",
                OsName = "Azure Container App",
                PcName = Environment.MachineName,
                AuthType = "password"
            });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                return ExtractSessionId(payload);
            }

            if (response.StatusCode is not (HttpStatusCode.InternalServerError or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest))
                break;
        }

        throw new HttpRequestException("TachoMaster login failed.");
    }

    private async Task<List<TachoDuty>> GetDutiesAsync(string sid, DateOnly date, CancellationToken cancellationToken)
    {
        var result = new List<TachoDuty>();
        var offset = 0;
        for (var page = 0; page < options.MaxPages; page++)
        {
            using var request = CreateRequest(HttpMethod.Post, "Duty/GetDutyTransactions", sid);
            request.Content = JsonContent.Create(new
            {
                From = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("O"),
                To = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("O"),
                Offset = offset,
                WithWtd = false
            });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var envelope = await JsonSerializer.DeserializeAsync<TachoDutyEnvelope>(stream, JsonOptions, cancellationToken) ?? new TachoDutyEnvelope();
            var pageData = envelope.DutyNew ?? new TachoPage<TachoDuty>();
            result.AddRange(pageData.Data);
            if (!pageData.MoreData || pageData.RecordCount == 0) break;
            offset += pageData.RecordCount;
        }

        return result;
    }

    private async Task<List<TachoMember>> GetMembersAsync(string sid, CancellationToken cancellationToken)
    {
        var result = new List<TachoMember>();
        var offset = 0;
        for (var page = 0; page < options.MaxPages; page++)
        {
            using var request = CreateRequest(HttpMethod.Post, "Member/GetMembersLong", sid);
            request.Content = JsonContent.Create(new { Offset = offset, OnlyLiveMembers = true });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var pageData = await JsonSerializer.DeserializeAsync<TachoPage<TachoMember>>(stream, JsonOptions, cancellationToken) ?? new TachoPage<TachoMember>();
            result.AddRange(pageData.Data);
            if (!pageData.MoreData || pageData.RecordCount == 0) break;
            offset += pageData.RecordCount;
        }

        return result;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string sid)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("APIKEY", options.ApiKey);
        request.Headers.Add("SID", sid);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string DriverName(TachoMember member)
    {
        var given = string.IsNullOrWhiteSpace(member.GivenNames) ? member.CName : member.GivenNames;
        var surname = string.IsNullOrWhiteSpace(member.Surname) ? member.SName : member.Surname;
        return string.Join(' ', new[] { given, surname }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
    }

    private static IReadOnlyList<string> PasswordAttempts(string password)
    {
        var trimmed = password.Trim();
        if (trimmed.All(Uri.IsHexDigit) && trimmed.Length is 32 or 40 or 64) return [trimmed];
        return [trimmed, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant()];
    }

    private static string ExtractSessionId(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind is JsonValueKind.Number or JsonValueKind.String) return root.ToString();
        foreach (var name in new[] { "sid", "SID", "token", "Token" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return value.ToString();
        throw new InvalidOperationException("TachoMaster login response did not contain a SID.");
    }

    private static string NormaliseBaseUrl(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        return trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? $"{trimmed}/" : $"{trimmed}/api/";
    }

    private static string NormaliseIdentifier(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class TachoDutyEnvelope { public TachoPage<TachoDuty>? DutyNew { get; set; } }
    private sealed class TachoPage<T> { public bool MoreData { get; set; } public int RecordCount { get; set; } public List<T> Data { get; set; } = []; }
    private sealed class TachoDuty { public int MemCode { get; set; } public string VehCode { get; set; } = string.Empty; public DateTime DutyStart { get; set; } }
    private sealed class TachoMember { public int MemCode { get; set; } public string? CName { get; set; } public string? SName { get; set; } public string? GivenNames { get; set; } public string? Surname { get; set; } }
}
