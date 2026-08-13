namespace Slh.Tms.Api.Models.Integrations;

public sealed class FleetioOptions
{
    public string BaseUrl { get; set; } = "https://secure.fleetio.com/api/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string AccountToken { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
    public string[] MissingSettings => new[]
    {
        Enabled ? string.Empty : "Fleetio enabled flag",
        string.IsNullOrWhiteSpace(BaseUrl) ? "Fleetio base URL" : string.Empty,
        string.IsNullOrWhiteSpace(ApiKey) ? "Fleetio access token" : string.Empty
    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
}
