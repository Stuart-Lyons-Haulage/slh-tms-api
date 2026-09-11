using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;

namespace Slh.Tms.MasterDataSync;

public sealed class SyncTelemetry(TelemetryClient telemetry, ILogger<SyncTelemetry> logger)
{
    public void Summary(string list, SyncSummary summary)
    {
        logger.LogInformation("Master sync {List}: added={Added}, updated={Updated}, deactivated={Deactivated}, failed={Failed}, read={Read}.",
            list, summary.Added, summary.Updated, summary.Deactivated, summary.Failed, summary.Read);
        telemetry.TrackEvent("MasterDataSyncSummary", new Dictionary<string, string>
        {
            ["List"] = list,
            ["Added"] = summary.Added.ToString(),
            ["Updated"] = summary.Updated.ToString(),
            ["Deactivated"] = summary.Deactivated.ToString(),
            ["Failed"] = summary.Failed.ToString(),
            ["Read"] = summary.Read.ToString()
        });
    }
}

public sealed record SyncSummary(int Read, int Added, int Updated, int Deactivated, int Failed);
