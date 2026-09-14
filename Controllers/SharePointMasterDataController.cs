using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/sharepoint/master-data"), Authorize(Policy = "TmsApprove")]
public sealed class SharePointMasterDataController(
    SharePointMasterDataSyncService sync,
    StagingService staging,
    ILogger<SharePointMasterDataController> logger) : ControllerBase
{
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        // SQL is the operational master. Do not allow a manual SharePoint pull to
        // overwrite data written by TMS users or upstream integrations.
        if (!sync.IsEnabled)
            return Conflict(new { code = "SqlMasterDataAuthoritative", message = "SharePoint master-data import is disabled because SQL is the TMS master-data authority." });

        try
        {
            var result = await sync.ReadAsync(ct);
            var applied = 0;
            foreach (var request in result.Requests)
            {
                await staging.PromoteDirect(request.EntityType, request.Payload, ct);
                applied++;
            }
            return Ok(new { result.ListsRead, result.RowsRead, applied, message = "Microsoft Lists master data was read and applied to the TMS operational copy." });
        }
        catch (SharePointMasterDataException ex)
        {
            logger.LogWarning(ex, "SharePoint master-data sync failed with code {Code}.", ex.Code);
            return StatusCode(StatusCodes.Status502BadGateway, new { code = ex.Code, message = ex.Message });
        }
    }
}
