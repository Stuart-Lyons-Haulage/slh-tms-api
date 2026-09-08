using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Middleware;

public sealed class PlanningChangeNotificationMiddleware(RequestDelegate next)
{
    private static readonly string[] PlanningPrefixes =
    [
        "/api/v1/planning-control",
        "/api/v1/runs",
        "/api/v1/loads",
        "/api/v1/staging",
        "/api/v1/order-intake",
        "/api/v1/orders",
        "/api/v1/planner-import"
    ];

    public async Task InvokeAsync(HttpContext context, PlanningChangeNotifier notifier)
    {
        var mutation = context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE";
        var path = context.Request.Path.Value ?? string.Empty;
        var relevant = mutation && PlanningPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        await next(context);

        if (relevant && context.Response.StatusCode is >= 200 and < 300)
            notifier.Publish($"{context.Request.Method} {path}");
    }
}
