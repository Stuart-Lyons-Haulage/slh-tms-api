using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Slh.Tms.MasterDataSync;

public sealed class SyncFunctions(MasterDataSyncOrchestrator orchestrator, ILogger<SyncFunctions> logger)
{
    [Function("MasterDataSyncTimer")]
    public async Task RunTimerAsync([TimerTrigger("%MasterDataSyncSchedule%")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Master data timer started at {StartedAtUtc}.", DateTime.UtcNow);
        await orchestrator.RunAsync(null, ct);
    }

    [Function("MasterDataSyncManual")]
    public async Task<HttpResponseData> RunManualAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "sync/trigger/{listName}")] HttpRequestData request,
        string listName,
        CancellationToken ct)
    {
        try
        {
            var result = await orchestrator.RunAsync(listName, ct);
            var response = request.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result, ct);
            return response;
        }
        catch (ArgumentException ex)
        {
            var response = request.CreateResponse(HttpStatusCode.BadRequest);
            await response.WriteStringAsync(ex.Message, ct);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual master-data sync failed for {ListName}.", listName);
            var response = request.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("Master-data sync failed.", ct);
            return response;
        }
    }
}
