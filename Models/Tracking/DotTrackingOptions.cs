namespace Slh.Tms.Api.Models.Tracking;

/// <summary>
/// Configuration options for DOT tracking provider integration.
/// All sensitive values (BaseUrl, Username, Password) must be provided via environment variables or secure configuration at runtime.
/// These are NOT stored in source control or appsettings.json.
/// </summary>
public class DotTrackingOptions
{
    /// <summary>
    /// Base URL for the DOT tracking API endpoint.
    /// Example: https://api.dottracking.com
    /// Set via environment variable or Azure Key Vault.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Username for DOT tracking API authentication.
    /// Set via environment variable or Azure Key Vault.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Password for DOT tracking API authentication.
    /// Set via environment variable or Azure Key Vault.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Polling interval in minutes for fetching vehicle tracking events.
    /// Default: 5 minutes.
    /// </summary>
    public int PollIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Whether DOT tracking integration is enabled.
    /// Default: false (disabled until explicitly configured).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Number of minutes after which vehicle location data is considered stale.
    /// Default: 10 minutes.
    /// Used to detect vehicles with outdated GPS information.
    /// </summary>
    public int StaleAfterMinutes { get; set; } = 10;
}
