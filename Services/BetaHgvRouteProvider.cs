using System.Text.Json;

namespace Slh.Tms.Api.Services;

public sealed record BetaRoutePoint(string Name, decimal Latitude, decimal Longitude);
public sealed record BetaHgvRouteCost(decimal Miles, int DriveMinutes, string Source);

public interface IBetaHgvRouteProvider
{
    Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct);
}

/// <summary>
/// Route evidence for Beta planning. Only a live Azure Maps truck route is accepted;
/// the resilient approximate route is deliberately rejected so crow-fly/Haversine
/// evidence can never drive an optimisation recommendation.
/// </summary>
public sealed class AzureMapsHgvRouteProvider(
    AzureMapsRouteClient maps,
    ILogger<AzureMapsHgvRouteProvider> logger) : IBetaHgvRouteProvider
{
    public async Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct)
    {
        if (points.Count < 2) return new BetaHgvRouteCost(0m, 0, "AzureMapsHgv");
        try
        {
            var response = await maps.Directions(points.Select(point => (point.Longitude, point.Latitude)).ToList(), ct);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(response));
            var root = document.RootElement;
            if (root.TryGetProperty("approximate", out var approximate) && approximate.ValueKind == JsonValueKind.True)
            {
                logger.LogWarning("Beta Optimiser rejected approximate routing evidence for {StopCount} stops.", points.Count);
                return null;
            }
            if (!root.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0 ||
                !routes[0].TryGetProperty("summary", out var summary) ||
                !summary.TryGetProperty("lengthInMeters", out var metres) ||
                !summary.TryGetProperty("travelTimeInSeconds", out var seconds))
            {
                logger.LogWarning("Azure Maps HGV routing did not return a usable route summary.");
                return null;
            }
            return new BetaHgvRouteCost(
                Math.Round((decimal)metres.GetDouble() / 1609.344m, 2),
                (int)Math.Ceiling(seconds.GetDouble() / 60d),
                "AzureMapsHgv");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Azure Maps HGV evidence was unavailable to the Beta Optimiser.");
            return null;
        }
    }
}
