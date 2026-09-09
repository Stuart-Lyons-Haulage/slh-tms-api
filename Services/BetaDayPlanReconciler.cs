namespace Slh.Tms.Api.Services;

public sealed record BetaComparisonOrderLine(string Reference, string Collection, string Delivery, int Pallets);

public sealed record BetaDayPlanReconciliation(
    int BetaOrderLines,
    int LyonsOrderLines,
    int MatchedOrderLines,
    IReadOnlyList<string> MissingFromLyons,
    IReadOnlyList<string> OnlyInLyons,
    bool OrderCoverageComplete,
    bool ComparableRouting,
    int RunCountDelta,
    decimal? MilesDelta,
    int? DriveMinutesDelta,
    IReadOnlyList<string> Warnings);

public static class BetaDayPlanReconciler
{
    public static BetaDayPlanReconciliation Reconcile(
        IReadOnlyList<BetaComparisonOrderLine> beta,
        IReadOnlyList<BetaComparisonOrderLine> lyons,
        int betaRunCount,
        int lyonsRunCount,
        bool betaRoutingComplete,
        bool lyonsRoutingComplete,
        decimal? betaMiles,
        decimal? lyonsMiles,
        int? betaDriveMinutes,
        int? lyonsDriveMinutes)
        => new(beta.Count, lyons.Count, 0, [], [], false, false,
            lyonsRunCount - betaRunCount, null, null, []);
}
