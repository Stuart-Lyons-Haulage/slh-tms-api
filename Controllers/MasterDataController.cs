using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/master-data")]
[Authorize]
public sealed class MasterDataController(StagingService staging) : ControllerBase
{
    private static readonly HashSet<string> DirectTypes = new(StringComparer.OrdinalIgnoreCase) { "customer", "customercontact", "vehicle", "driver", "trailer", "site", "marketcontact", "fuelprice" };

    [HttpPost("apply"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Apply(List<StageImportRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0 || requests.Count > 1000) return BadRequest(new ErrorResponse("invalid_batch", "Submit between 1 and 1000 master-data records.", HttpContext.TraceIdentifier));
        var results = new List<object>();
        var applied = 0;
        var failed = 0;
        foreach (var request in requests)
        {
            if (!DirectTypes.Contains(request.EntityType))
            {
                failed++;
                results.Add(new { request.EntityType, request.IdempotencyKey, applied = false, error = "This endpoint only applies master-data records." });
                continue;
            }

            try
            {
                await staging.PromoteDirect(request.EntityType, request.Payload, ct);
                applied++;
                results.Add(new { request.EntityType, request.IdempotencyKey, applied = true });
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                staging.ClearTrackedChanges();
                failed++;
                results.Add(new { request.EntityType, request.IdempotencyKey, applied = false, error = ex.GetBaseException().Message });
            }
        }

        return Ok(new { received = requests.Count, applied, failed, results });
    }
}
