namespace Slh.Tms.Api.Models.Tracking;

/// <summary>
/// Runtime configuration for the RoadTech Falcon tracking integration.
/// Credentials are supplied only from Azure Key Vault or environment variables.
/// </summary>
public sealed class DotTrackingOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string CompanyCode { get; set; } = string.Empty;
    public int PollIntervalMinutes { get; set; } = 5;
    public bool Enabled { get; set; }
    public int StaleAfterMinutes { get; set; } = 10;

    /// <summary>RoadTech telemetry data mask. 0 matches RoadTech's documented current telemetry sample.</summary>
    public int DataMask { get; set; } = 0;

    /// <summary>RoadTech flag to include active vehicles only.</summary>
    public int OnlyLive { get; set; } = 1;

    /// <summary>Safety limit for RoadTech Offset pagination.</summary>
    public int MaxPages { get; set; } = 100;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password) && !string.IsNullOrWhiteSpace(CompanyCode);
}
