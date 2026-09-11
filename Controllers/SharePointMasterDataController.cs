using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/sharepoint/master-data"), Authorize(Policy = "TmsApprove")]
public sealed class SharePointMasterDataController(SharePointMasterDataSyncService sync, StagingService staging) : ControllerBase
{
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var result = await sync.ReadAsync(ct);
        var applied = 0;
        foreach (var request in result.Requests)
        {
            await staging.PromoteDirect(request.EntityType, request.Payload, ct);
            applied++;
        }
        return Ok(new { result.ListsRead, result.RowsRead, applied, message = "Microsoft Lists master data was read and applied idempotently." });
    }
}
