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
    public int RecoveryIntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; }
    public int StaleAfterMinutes { get; set; } = 10;

    /// <summary>
    /// RoadTech telemetry data mask. GPS is bit 0x01 and is mandatory for the
    /// operational tracking/geofence feed. Do not default to zero: RoadTech's
    /// zero-mask examples legitimately return dataGps = null.
    /// </summary>
    public int DataMask { get; set; } = 0x01;

    /// <summary>RoadTech flag to include active vehicles only.</summary>
    public bool OnlyLive { get; set; } = true;

    /// <summary>Safety limit for RoadTech Offset pagination.</summary>
    public int MaxPages { get; set; } = 100;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password) && !string.IsNullOrWhiteSpace(CompanyCode);
}
