using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/planning-events")]
[Authorize]
public sealed class PlanningEventsController(PlanningChangeNotifier notifier) : ControllerBase
{
    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        var subscription = notifier.Subscribe();
        try
        {
            await Response.WriteAsync(": connected\n\n", ct);
            await Response.Body.FlushAsync(ct);

            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(25));
            while (!ct.IsCancellationRequested)
            {
                var readTask = subscription.Reader.ReadAsync(ct).AsTask();
                var heartbeatTask = heartbeat.WaitForNextTickAsync(ct).AsTask();
                var completed = await Task.WhenAny(readTask, heartbeatTask);
                if (completed == readTask)
                {
                    var change = await readTask;
                    await Response.WriteAsync($"event: planning-data-changed\nid: {change.Sequence}\ndata: {change.ChangedAtUtc:O}\n\n", ct);
                }
                else if (await heartbeatTask)
                {
                    await Response.WriteAsync($": heartbeat {DateTimeOffset.UtcNow:O}\n\n", ct);
                }
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            notifier.Unsubscribe(subscription.Id);
        }
    }
}
