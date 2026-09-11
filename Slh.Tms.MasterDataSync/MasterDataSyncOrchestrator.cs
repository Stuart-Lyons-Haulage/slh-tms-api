using Microsoft.Extensions.Options;

namespace Slh.Tms.MasterDataSync;

public sealed class MasterDataSyncOrchestrator(
    GraphSharePointClient graph,
    SqlMasterDataRepository sql,
    DeadLetterQueue deadLetters,
    TmsCacheInvalidationClient cache,
    SyncTelemetry telemetry,
    IOptions<SyncOptions> options)
{
    public async Task<SyncSummaryEnvelope> RunAsync(string? requestedList, CancellationToken ct)
    {
        var selected = string.IsNullOrWhiteSpace(requestedList)
            ? MasterListDefinitions.All.Values
            : MasterListDefinitions.All.TryGetValue(requestedList, out var definition)
                ? [definition]
                : throw new ArgumentException($"Unknown master list '{requestedList}'.");

        var summaries = new Dictionary<string, SyncSummary>();
        foreach (var current in selected.OrderBy(x => DependencyOrder(x.Key)))
        {
            var items = await graph.ReadListAsync(current, ct);
            var summary = await sql.UpsertListAsync(current, items,
                (item, error) => deadLetters.EnqueueAsync(current, item, error, ct), ct);
            summaries[current.Key] = summary;
            telemetry.Summary(current.Key, summary);

            if (summary.Failed == 0)
                await cache.InvalidateAsync(current.Key, ct);
        }

        var total = summaries.Values.Aggregate(new MutableTotal(), (acc, next) =>
        {
            acc.Read += next.Read;
            acc.Added += next.Added;
            acc.Updated += next.Updated;
            acc.Deactivated += next.Deactivated;
            acc.Failed += next.Failed;
            return acc;
        }).ToSummary();

        return new SyncSummaryEnvelope(summaries, total);
    }

    private static int DependencyOrder(string key) => key switch
    {
        "depot" => 0,
        "customer" => 1,
        "vehicle" => 2,
        "driver" => 3,
        "trailer" => 4,
        "site" => 5,
        "fuelcard" => 6,
        _ => 7
    };

    private sealed class MutableTotal
    {
        public int Read;
        public int Added;
        public int Updated;
        public int Deactivated;
        public int Failed;
        public SyncSummary ToSummary() => new(Read, Added, Updated, Deactivated, Failed);
    }
}

public sealed record SyncSummaryEnvelope(IReadOnlyDictionary<string, SyncSummary> Lists, SyncSummary Total);
