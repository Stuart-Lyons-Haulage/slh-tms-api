using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/sharepoint/master-data"), Authorize(Policy = "TmsApprove")]
public sealed class SharePointMasterDataController(
    SharePointMasterDataSyncService sync,
    StagingService staging,
    TmsDbContext db,
    ILogger<SharePointMasterDataController> logger) : ControllerBase
{
    [HttpPost("publish"), RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Publish(CancellationToken ct)
    {
        try
        {
            var result = await sync.PublishFromSqlAsync(db, ct);
            return Ok(new { result.ListsWritten, result.RowsWritten, result.RowsByList, message = "TMS master data was mirrored to the governed SharePoint Lists. Nothing was removed from the TMS operational copy." });
        }
        catch (SharePointMasterDataException ex)
        {
            logger.LogWarning(ex, "SharePoint master-data publish failed with code {Code}.", ex.Code);
            return StatusCode(StatusCodes.Status502BadGateway, new { code = ex.Code, message = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SharePoint master-data publish failed unexpectedly.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { code = "MasterDataPublishUnhandled", message = ex.GetBaseException().Message });
        }
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
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
