namespace Slh.Tms.Api.Services;

public sealed record BetaDayOrderInput(
    Guid OrderId,
    Guid SourceLineId,
    string Reference,
    string CustomerCode,
    string Period,
    string? PalletType,
    int Pallets,
    TimeOnly? CollectionTimeFrom,
    BetaRoutePoint Collection,
    BetaRoutePoint Delivery,
    bool RoutingMapped = true,
    string? MappingWarning = null);

public sealed record BetaDayBuiltRun(
    string Reference,
    string Period,
    string PalletFamily,
    int CapacityPallets,
    int PlannedPallets,
    decimal UtilisationPercent,
    bool RoutingAvailable,
    decimal? Miles,
    int? DriveMinutes,
    IReadOnlyList<BetaDayOrderInput> Orders,
    IReadOnlyList<BetaRoutePoint> Stops,
    IReadOnlyList<string> Warnings);

public sealed class BetaDayPlanBuilder(IBetaHgvRouteProvider routeProvider)
{
    public Task<IReadOnlyList<BetaDayBuiltRun>> BuildAsync(DateOnly planningDate, IReadOnlyList<BetaDayOrderInput> orders, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<BetaDayBuiltRun>>([]);
}
