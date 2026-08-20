using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class TachoMasterClient
{
    private readonly HttpClient httpClient;
    private readonly TachoMasterOptions options;
    private readonly ILogger<TachoMasterClient> logger;
    private readonly DotTrackingClient? dotTrackingClient;

    public TachoMasterClient(HttpClient httpClient, TachoMasterOptions options, ILogger<TachoMasterClient> logger, DotTrackingClient? dotTrackingClient = null)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger;
        this.dotTrackingClient = dotTrackingClient;
        this.httpClient.Timeout = TimeSpan.FromSeconds(30);
        this.httpClient.BaseAddress = new Uri(NormaliseBaseUrl(options.BaseUrl));
    }

    public bool IsConfigured => options.IsConfigured;
    public bool UsesSharedRoadTechCredentials => options.UsesSharedRoadTechCredentials;
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
        var statuses = await GetCurrentDriverStatusesByVehicleAsync(date, cancellationToken);
        return statuses.ToDictionary(item => item.Key, item => item.Value.DriverName);
    }

    public async Task<IReadOnlyList<TachoDriverProfile>> GetDriverProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured) return [];
        var sid = await LoginAsync(cancellationToken);
        var membersTask = GetMembersAsync(sid, cancellationToken);
        var metricsTask = TryGetMemberMetricsAsync(sid, cancellationToken);
        await Task.WhenAll(membersTask, metricsTask);
        var metrics = (await metricsTask).GroupBy(item => item.MemCode)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.DateTimeWhenValid).First());
        return (await membersTask).GroupBy(item => item.MemCode).Select(group => group.First()).Select(member =>
        {
            metrics.TryGetValue(member.MemCode, out var metric);
            return new TachoDriverProfile(
                member.MemCode,
                DriverName(member),
                member.CardNoShort,
                member.EmployeeNumber,
                metric?.DateTimeWhenValid,
                metric?.DailyDriverPeriodsAvaiable,
                metric?.DriveAvailableToday,
                metric?.DriveAvailableTomorrow,
                metric?.DriveAvailableWeek,
                metric?.DriveAvailableFortnight,
                metric?.LongDaysWorkedThisWeek,
                metric?.ShortDailyRestTakenThisWeek,
                metric?.WorkAvaiableWeek);
        }).Where(profile => !string.IsNullOrWhiteSpace(profile.DriverName)).OrderBy(profile => profile.DriverName).ToList();
    }

    /// <summary>
    /// Returns every TachoMaster duty transaction for the requested date. This is intentionally
    /// different from GetCurrentDriverStatusesByVehicleAsync, which collapses to the latest driver
    /// identity for each vehicle and is appropriate only for live operational identity.
    /// </summary>
    public async Task<IReadOnlyList<TachoDriverDutyStatus>> GetDriverDutyStatusesAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured) return [];

        var sid = await LoginAsync(cancellationToken);
        var dutiesTask = GetDutiesAsync(sid, date, cancellationToken);
        var membersTask = GetMembersAsync(sid, cancellationToken);
        var metricsTask = TryGetMemberMetricsAsync(sid, cancellationToken);
        await Task.WhenAll(dutiesTask, membersTask, metricsTask);

        var members = (await membersTask).GroupBy(member => member.MemCode)
            .ToDictionary(group => group.Key, group => group.First());
        var metrics = (await metricsTask).GroupBy(metric => metric.MemCode)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(metric => metric.DateTimeWhenValid).First());
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var dayEnd = dayStart.AddDays(1);
        var result = new List<TachoDriverDutyStatus>();

        foreach (var duty in (await dutiesTask)
            .Where(item => item.MemCode > 0)
            .Where(item => item.DutyStart < dayEnd && (item.DutyEnd is null || item.DutyEnd >= dayStart))
            .OrderBy(item => item.DutyStart))
        {
            if (!members.TryGetValue(duty.MemCode, out var member)) continue;
            var name = DriverName(member);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var breaks = (duty.Wtd ?? [])
                .Where(item => string.Equals(item.WtdEvent, "wtdBreak", StringComparison.OrdinalIgnoreCase))
                .Where(item => item.TimeEnd >= item.TimeStart)
                .ToList();
            metrics.TryGetValue(duty.MemCode, out var metric);

            result.Add(new TachoDriverDutyStatus(
                string.IsNullOrWhiteSpace(duty.VehCode) ? string.Empty : NormaliseIdentifier(duty.VehCode),
                duty.MemCode,
                name,
                member.CardNoShort,
                member.EmployeeNumber,
                duty.DutyStart,
                duty.DutyEnd,
                duty.TimeWork,
                duty.TimeRest,
                duty.TimeAvailable,
                duty.TimeDrive,
                breaks.Count,
                breaks.Count == 0 ? null : (int)breaks.Sum(item => (item.TimeEnd - item.TimeStart).TotalMinutes),
                metric?.DateTimeWhenValid,
                metric?.DailyDriverPeriodsAvaiable,
                metric?.DriveAvailableToday,
                metric?.DriveAvailableTomorrow,
                metric?.DriveAvailableWeek,
                metric?.DriveAvailableFortnight,
                metric?.LongDaysWorkedThisWeek,
                metric?.ShortDailyRestTakenThisWeek,
                metric?.WorkAvaiableWeek));
        }

        logger.LogInformation("TachoMaster returned {DutyCount} complete duty transaction(s) for {Date}.", result.Count, date);
        return result;
    }

    public async Task<IReadOnlyDictionary<string, TachoVehicleDriverStatus>> GetCurrentDriverStatusesByVehicleAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured) return new Dictionary<string, TachoVehicleDriverStatus>();

        var sid = await LoginAsync(cancellationToken);
        var dutiesTask = GetDutiesAsync(sid, date, cancellationToken);
        var membersTask = GetMembersAsync(sid, cancellationToken);
        var metricsTask = TryGetMemberMetricsAsync(sid, cancellationToken);
        await Task.WhenAll(dutiesTask, membersTask, metricsTask);

        var duties = await dutiesTask;
        var memberList = await membersTask;
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var dayEnd = dayStart.AddDays(1);
        var currentDuties = duties
            .Where(duty => !string.IsNullOrWhiteSpace(duty.VehCode) && duty.MemCode > 0)
            .Where(duty => duty.DutyStart < dayEnd && (duty.DutyEnd is null || duty.DutyEnd >= dayStart))
            .GroupBy(duty => NormaliseIdentifier(duty.VehCode))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(duty => duty.DutyStart).ToList());

        var members = memberList.GroupBy(member => member.MemCode).ToDictionary(group => group.Key, group => group.First());
        var metrics = (await metricsTask).GroupBy(metric => metric.MemCode).ToDictionary(group => group.Key, group => group.OrderByDescending(metric => metric.DateTimeWhenValid).First());
        var result = new Dictionary<string, TachoVehicleDriverStatus>();
        foreach (var (vehicle, vehicleDuties) in currentDuties)
        {
            var latest = vehicleDuties[0];
            if (!members.TryGetValue(latest.MemCode, out var member)) continue;
            var name = DriverName(member);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var driverDuties = vehicleDuties.Where(duty => duty.MemCode == latest.MemCode).ToList();
            var wtdBreaks = driverDuties.SelectMany(duty => duty.Wtd ?? [])
                .Where(item => string.Equals(item.WtdEvent, "wtdBreak", StringComparison.OrdinalIgnoreCase))
                .Where(item => item.TimeEnd >= item.TimeStart)
                .ToList();
            metrics.TryGetValue(latest.MemCode, out var metric);
            result[vehicle] = new TachoVehicleDriverStatus(
                vehicle,
                latest.MemCode,
                name,
                member.CardNoShort,
                member.EmployeeNumber,
                driverDuties.Min(duty => duty.DutyStart),
                driverDuties.All(duty => duty.DutyEnd is not null) ? driverDuties.Max(duty => duty.DutyEnd) : null,
                driverDuties.Sum(duty => duty.TimeWork),
                driverDuties.Sum(duty => duty.TimeRest),
                driverDuties.Sum(duty => duty.TimeAvailable),
                driverDuties.Sum(duty => duty.TimeDrive),
                wtdBreaks.Count,
                wtdBreaks.Count == 0 ? null : (int)wtdBreaks.Sum(item => (item.TimeEnd - item.TimeStart).TotalMinutes),
                metric?.DateTimeWhenValid,
                metric?.DailyDriverPeriodsAvaiable,
                metric?.DriveAvailableToday,
                metric?.DriveAvailableTomorrow,
                metric?.DriveAvailableWeek,
                metric?.DriveAvailableFortnight,
                metric?.LongDaysWorkedThisWeek,
                metric?.ShortDailyRestTakenThisWeek,
                metric?.WorkAvaiableWeek);
        }

        var falconDrivers = await TryGetFalconDriverStatusesAsync(memberList, cancellationToken);
        var falconOnly = 0;
        var overlaps = 0;
        var mismatches = 0;
        foreach (var (vehicle, falcon) in falconDrivers)
        {
            if (!result.TryGetValue(vehicle, out var duty))
            {
                result[vehicle] = falcon;
                falconOnly++;
                continue;
            }

            overlaps++;
            if (!SameIdentity(duty, falcon))
            {
                mismatches++;
                logger.LogWarning(
                    "Live driver identity mismatch for vehicle {Vehicle}: TachoMaster duty={DutyDriver} card={DutyCard}; Falcon={FalconDriver} card={FalconCard}. Falcon identity retained and Tacho compliance figures suppressed until the identities agree.",
                    vehicle,
                    duty.DriverName,
                    duty.CardNumber,
                    falcon.DriverName,
                    falcon.CardNumber);

                // Tracking owns current driver identity. If the live Falcon identity does not
                // agree with the Tacho duty identity, do not apply another driver's hours/breaks
                // to the vehicle ETA. The Falcon-only record deliberately carries no compliance
                // metrics until TachoMaster resolves to the same driver/card.
                result[vehicle] = falcon;
            }
        }

        logger.LogInformation(
            "Tacho continuous enrichment produced {Total} vehicle identities: {DutyCount} TachoMaster duty record(s), {FalconOnly} Falcon-only identity record(s), {OverlapCount} overlap(s), {MismatchCount} live mismatch(es).",
            result.Count,
            currentDuties.Count,
            falconOnly,
            overlaps,
            mismatches);
        return result;
    }

    private async Task<IReadOnlyDictionary<string, TachoVehicleDriverStatus>> TryGetFalconDriverStatusesAsync(
        IReadOnlyList<TachoMember> members,
        CancellationToken cancellationToken)
    {
        if (dotTrackingClient is null) return new Dictionary<string, TachoVehicleDriverStatus>();
        try
        {
            var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-5);
            var telemetry = await dotTrackingClient.GetLatestVehicleEventsAsync(cancellationToken);
            var records = telemetry.Select(DotTelemetryRecord.FromProvider)
                .Where(record => !string.IsNullOrWhiteSpace(record.VehicleIdentifier))
                .Where(record => record.EventTimeUtc >= freshAfter)
                .Where(record => !string.IsNullOrWhiteSpace(record.DriverName) || !string.IsNullOrWhiteSpace(record.DriverCardNumber))
                .GroupBy(record => NormaliseIdentifier(record.VehicleIdentifier))
                .Select(group => group.OrderByDescending(record => record.EventTimeUtc).First())
                .ToList();

            var result = new Dictionary<string, TachoVehicleDriverStatus>();
            foreach (var record in records)
            {
                var member = FindMemberByCard(members, record.DriverCardNumber);
                var resolvedName = !string.IsNullOrWhiteSpace(record.DriverName)
                    ? record.DriverName!.Trim()
                    : member is null ? null : DriverName(member);
                if (string.IsNullOrWhiteSpace(resolvedName)) continue;

                var vehicle = NormaliseIdentifier(record.VehicleIdentifier);
                result[vehicle] = new TachoVehicleDriverStatus(
                    vehicle,
                    member?.MemCode ?? 0,
                    resolvedName,
                    member?.CardNoShort ?? record.DriverCardNumber,
                    member?.EmployeeNumber,
                    record.EventTimeUtc,
                    null,
                    0,
                    0,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Falcon live driver enrichment was unavailable.");
            return new Dictionary<string, TachoVehicleDriverStatus>();
        }
    }

    private static bool SameIdentity(TachoVehicleDriverStatus duty, TachoVehicleDriverStatus falcon)
    {
        var dutyCard = NormaliseCard(duty.CardNumber);
        var falconCard = NormaliseCard(falcon.CardNumber);
        if (dutyCard.Length >= 8 && falconCard.Length >= 8)
            return string.Equals(dutyCard, falconCard, StringComparison.OrdinalIgnoreCase)
                || dutyCard.EndsWith(falconCard, StringComparison.OrdinalIgnoreCase)
                || falconCard.EndsWith(dutyCard, StringComparison.OrdinalIgnoreCase);

        return string.Equals(NormaliseName(duty.DriverName), NormaliseName(falcon.DriverName), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseName(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static TachoMember? FindMemberByCard(IEnumerable<TachoMember> members, string? cardNumber)
    {
        var liveCard = NormaliseCard(cardNumber);
        if (liveCard.Length < 8) return null;
        return members.FirstOrDefault(member =>
        {
            var memberCard = NormaliseCard(member.CardNoShort);
            if (memberCard.Length < 8) return false;
            return string.Equals(memberCard, liveCard, StringComparison.OrdinalIgnoreCase)
                || (memberCard.Length >= 8 && liveCard.EndsWith(memberCard, StringComparison.OrdinalIgnoreCase))
                || (liveCard.Length >= 8 && memberCard.EndsWith(liveCard, StringComparison.OrdinalIgnoreCase));
        });
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
                OsVersion = "1.0",
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
                WithWtd = true
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

    private async Task<List<TachoMemberMetric>> GetMemberMetricsAsync(string sid, CancellationToken cancellationToken)
    {
        var result = new List<TachoMemberMetric>();
        var offset = 0;
        for (var page = 0; page < options.MaxPages; page++)
        {
            using var request = CreateRequest(HttpMethod.Post, "Member/GetMemberMetrics", sid);
            request.Content = JsonContent.Create(new { Offset = offset });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var pageData = await JsonSerializer.DeserializeAsync<TachoPage<TachoMemberMetric>>(stream, JsonOptions, cancellationToken) ?? new TachoPage<TachoMemberMetric>();
            result.AddRange(pageData.Data);
            if (!pageData.MoreData || pageData.RecordCount == 0) break;
            offset += pageData.RecordCount;
        }

        return result;
    }

    private async Task<List<TachoMemberMetric>> TryGetMemberMetricsAsync(string sid, CancellationToken cancellationToken)
    {
        try
        {
            return await GetMemberMetricsAsync(sid, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster member metrics were unavailable; returning driver and duty data without remaining-time figures.");
            return [];
        }
    }

    private async Task<List<TachoMember>> GetMembersAsync(string sid, CancellationToken cancellationToken)
    {
        var result = new List<TachoMember>();
        var offset = 0;
        for (var page = 0; page < options.MaxPages; page++)
        {
            using var request = CreateRequest(HttpMethod.Post, "Member/GetMembersLong", sid);
            request.Content = JsonContent.Create(new { Offset = offset, OnlyLiveMembers = false });
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

    private static string NormaliseCard(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new NumberOrStringConverter() }
    };

    private sealed class NumberOrStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected text or number, received {reader.TokenType}.")
        };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    private sealed class TachoDutyEnvelope { public TachoPage<TachoDuty>? DutyNew { get; set; } }
    private sealed class TachoPage<T> { public bool MoreData { get; set; } public int RecordCount { get; set; } public List<T> Data { get; set; } = []; }
    private sealed class TachoDuty
    {
        public int MemCode { get; set; }
        public string VehCode { get; set; } = string.Empty;
        public DateTimeOffset DutyStart { get; set; }
        public DateTimeOffset? DutyEnd { get; set; }
        public int TimeWork { get; set; }
        public int TimeRest { get; set; }
        public int TimeAvailable { get; set; }
        public int TimeDrive { get; set; }
        public List<TachoWtdEvent>? Wtd { get; set; }
    }
    private sealed class TachoWtdEvent { public string? WtdEvent { get; set; } public DateTimeOffset TimeStart { get; set; } public DateTimeOffset TimeEnd { get; set; } }
    private sealed class TachoMember
    {
        public int MemCode { get; set; }
        public string? CName { get; set; }
        public string? SName { get; set; }
        public string? GivenNames { get; set; }
        public string? Surname { get; set; }
        public string? CardNoShort { get; set; }
        public string? EmployeeNumber { get; set; }
    }
    private sealed class TachoMemberMetric
    {
        public int MemCode { get; set; }
        public DateTimeOffset DateTimeWhenValid { get; set; }
        public int DailyDriverPeriodsAvaiable { get; set; }
        public int DriveAvailableToday { get; set; }
        public int DriveAvailableTomorrow { get; set; }
        public int DriveAvailableWeek { get; set; }
        public int DriveAvailableFortnight { get; set; }
        public int LongDaysWorkedThisWeek { get; set; }
        public int ShortDailyRestTakenThisWeek { get; set; }
        public int WorkAvaiableWeek { get; set; }
    }
}

public sealed record TachoVehicleDriverStatus(
    string VehicleCode,
    int MemberCode,
    string DriverName,
    string? CardNumber,
    string? EmployeeNumber,
    DateTimeOffset DutyStartUtc,
    DateTimeOffset? DutyEndUtc,
    int WorkMinutes,
    int RestMinutes,
    int AvailableMinutes,
    int DriveMinutes,
    int BreakCount,
    int? BreakMinutes,
    DateTimeOffset? MetricsValidAtUtc,
    int? DailyDriverPeriodsAvailable,
    int? DriveAvailableTodayMinutes,
    int? DriveAvailableTomorrowMinutes,
    int? DriveAvailableWeekMinutes,
    int? DriveAvailableFortnightMinutes,
    int? LongDaysWorkedThisWeek,
    int? ShortDailyRestTakenThisWeek,
    int? WorkAvailableWeekMinutes);

public sealed record TachoDriverDutyStatus(
    string VehicleCode,
    int MemberCode,
    string DriverName,
    string? CardNumber,
    string? EmployeeNumber,
    DateTimeOffset DutyStartUtc,
    DateTimeOffset? DutyEndUtc,
    int WorkMinutes,
    int RestMinutes,
    int AvailableMinutes,
    int DriveMinutes,
    int BreakCount,
    int? BreakMinutes,
    DateTimeOffset? MetricsValidAtUtc,
    int? DailyDriverPeriodsAvailable,
    int? DriveAvailableTodayMinutes,
    int? DriveAvailableTomorrowMinutes,
    int? DriveAvailableWeekMinutes,
    int? DriveAvailableFortnightMinutes,
    int? LongDaysWorkedThisWeek,
    int? ShortDailyRestTakenThisWeek,
    int? WorkAvailableWeekMinutes);

public sealed record TachoDriverProfile(
    int MemberCode,
    string DriverName,
    string? CardNumber,
    string? EmployeeNumber,
    DateTimeOffset? MetricsValidAtUtc,
    int? DailyDriverPeriodsAvailable,
    int? DriveAvailableTodayMinutes,
    int? DriveAvailableTomorrowMinutes,
    int? DriveAvailableWeekMinutes,
    int? DriveAvailableFortnightMinutes,
    int? LongDaysWorkedThisWeek,
    int? ShortDailyRestTakenThisWeek,
    int? WorkAvailableWeekMinutes);