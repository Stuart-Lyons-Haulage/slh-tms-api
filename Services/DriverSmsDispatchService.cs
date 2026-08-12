using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

public sealed class DriverSmsDispatchService(TextBeeOptions textBee, AzureSmsDispatchService azureSms, HttpClient httpClient, ILogger<DriverSmsDispatchService> logger)
{
    private static readonly Regex E164 = new(@"^\+[1-9]\d{7,14}$", RegexOptions.Compiled);

    public bool IsConfigured => IsTextBeeConfigured || azureSms.IsConfigured;
    public bool IsTextBeeConfigured => textBee.IsConfigured;

    public async Task<SmsDispatchResult> SendAsync(string destination, string message, CancellationToken ct)
    {
        if (!E164.IsMatch(destination)) throw new InvalidOperationException("The assigned driver mobile number must use E.164 format, for example +447700900123.");
        if (IsTextBeeConfigured) return await SendTextBee(destination, message, ct);
        return await azureSms.SendAsync(destination, message, ct);
    }

    private async Task<SmsDispatchResult> SendTextBee(string destination, string message, CancellationToken ct)
    {
        var baseUrl = textBee.BaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/gateway/devices/{Uri.EscapeDataString(textBee.DeviceId)}/send-sms");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", textBee.ApiKey);
        request.Content = JsonContent.Create(new { recipients = new[] { destination }, message });

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"TextBee rejected the driver dispatch: {(int)response.StatusCode} {response.ReasonPhrase}. {body}", null, response.StatusCode);

        logger.LogInformation("Sent dispatch SMS with TextBee to driver mobile ending {MobileSuffix}", destination[^4..]);
        return new SmsDispatchResult($"textbee:{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}", destination[^4..], "TextBee");
    }
}

public sealed record SmsDispatchResult(string MessageId, string MobileSuffix, string Provider);
