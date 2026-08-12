namespace Slh.Tms.Api.Models.Integrations;

public sealed class TextBeeOptions
{
    public string BaseUrl { get; set; } = "https://api.textbee.dev";
    public string ApiKey { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DutyPhoneLabel { get; set; } = "Duty phone";
    public bool Enabled { get; set; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(DeviceId);
    public string[] MissingSettings => new[]
    {
        Enabled ? string.Empty : "Integrations:TextBee:Enabled",
        string.IsNullOrWhiteSpace(BaseUrl) ? "Integrations:TextBee:BaseUrl" : string.Empty,
        string.IsNullOrWhiteSpace(ApiKey) ? "Integrations:TextBee:ApiKey" : string.Empty,
        string.IsNullOrWhiteSpace(DeviceId) ? "Integrations:TextBee:DeviceId" : string.Empty
    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
}
