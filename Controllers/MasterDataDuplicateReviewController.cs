using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/operational-master-data/duplicates")]
[Authorize]
public sealed class MasterDataDuplicateReviewController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MasterDataDuplicateCandidate>>> Candidates([FromQuery] string? entityType, CancellationToken ct)
        => Ok(await MasterDataDuplicateReviewService.FindCandidatesAsync(db, entityType, ct));

    [HttpPost("auto-merge"), Authorize(Policy = "TmsApprove")]
    public async Task<ActionResult<MasterDataDuplicateMergeResult>> AutoMerge([FromQuery] string? entityType, CancellationToken ct)
        => Ok(await MasterDataDuplicateReviewService.AutoMergeHighConfidenceAsync(db, entityType, Actor(), ct));

    [HttpPost("{entityType}/merge"), Authorize(Policy = "TmsApprove")]
    public async Task<ActionResult<MasterDataDuplicateMergeResult>> Merge(string entityType, MasterDataDuplicateMergeRequest request, CancellationToken ct)
        => Ok(await MasterDataDuplicateReviewService.MergeAsync(db, entityType, request, Actor(), ct));

    [HttpPost("reject"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reject(MasterDataDuplicateRejectRequest request, CancellationToken ct)
    {
        await MasterDataDuplicateReviewService.RejectAsync(db, request, Actor(), ct);
        return Ok(new { rejected = true });
    }

    private string Actor() => User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "SLH Assistant";
}
