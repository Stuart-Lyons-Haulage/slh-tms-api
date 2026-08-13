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
        if (requests.Count == 0 || requests.Count > 10000) return BadRequest(new ErrorResponse("invalid_batch", "Submit between 1 and 10000 master-data records.", HttpContext.TraceIdentifier));
        requests = requests
            .OrderBy(request => request.EntityType.ToLowerInvariant() switch
            {
                "customer" => 0,
                "site" => 1,
                "customercontact" => 2,
                "driver" => 3,
                "vehicle" => 4,
                "trailer" => 5,
                "marketcontact" => 6,
                "fuelprice" => 7,
                _ => 99
            })
            .ToList();
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
            catch (Exception ex)
            {
                staging.ClearTrackedChanges();
                if (IsDatabaseUnavailable(ex))
                {
                    try
                    {
                        await staging.RegisterFallback(request.EntityType, request.Payload, request.Source, ct);
                        applied++;
                        results.Add(new { request.EntityType, request.IdempotencyKey, applied = true, registered = true, error = "Stored in the section register while the live SQL table is repaired." });
                        continue;
                    }
                    catch (Exception registerException)
                    {
                        ex = registerException;
                    }
                }
                failed++;
                results.Add(new { request.EntityType, request.IdempotencyKey, applied = false, error = ex.GetBaseException().Message });
            }
        }

        return Ok(new { received = requests.Count, applied, failed, results });
    }

    [HttpPost("register/link"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> LinkRegister(CancellationToken ct)
    {
        var linked = await staging.LinkRegistered(ct);
        return Ok(new { linked, message = linked == 0 ? "No registered rows could be linked yet." : $"Linked {linked} registered rows into the live master tables." });
    }

    private static bool IsDatabaseUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not exist or you do not have permissions", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission was denied", StringComparison.OrdinalIgnoreCase);
    }
}
