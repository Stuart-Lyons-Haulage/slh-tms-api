using Microsoft.ApplicationInsights;

namespace Slh.Tms.Api.Services;

public interface ITmsTelemetry
{
    void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null);
    void TrackMetric(string name, double value, IReadOnlyDictionary<string, string>? properties = null);
}

/// <summary>
/// Single adapter for SLH custom business telemetry. TelemetryClient remains the production
/// transport, while unconfigured environments (local dev/tests) become a deliberate no-op.
/// This avoids the Application Insights 3.1.x missing-connection-string edge case and keeps
/// domain services independently testable.
/// </summary>
public sealed class ApplicationInsightsTmsTelemetry(
    TelemetryClient client,
    IConfiguration configuration,
    ILogger<ApplicationInsightsTmsTelemetry> logger) : ITmsTelemetry
{
    private readonly bool enabled = !string.IsNullOrWhiteSpace(
        configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] ??
        configuration["ApplicationInsights:ConnectionString"]);
    private int warned;

    public void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        if (!enabled)
        {
            WarnOnce();
            return;
        }

        client.TrackEvent(name, properties is null
            ? null
            : new Dictionary<string, string>(properties, StringComparer.Ordinal));
    }

    public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string>? properties = null)
    {
        if (!enabled)
        {
            WarnOnce();
            return;
        }

        client.TrackMetric(name, value, properties is null
            ? null
            : new Dictionary<string, string>(properties, StringComparer.Ordinal));
    }

    private void WarnOnce()
    {
        if (Interlocked.Exchange(ref warned, 1) != 0) return;
        logger.LogDebug("Application Insights custom business telemetry is disabled because no connection string is configured.");
    }
}

internal sealed class NoOpTmsTelemetry : ITmsTelemetry
{
    public static NoOpTmsTelemetry Instance { get; } = new();
    private NoOpTmsTelemetry() { }
    public void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null) { }
    public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string>? properties = null) { }
}
