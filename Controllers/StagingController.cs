using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;
[ApiController, Route("api/v1/staging")]
[Authorize]
public sealed class StagingController(TmsDbContext db, StagingService service) : ControllerBase
{
    [HttpPost, Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Stage(StageImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) return BadRequest(new ErrorResponse("invalid_idempotency_key", "IdempotencyKey is required", HttpContext.TraceIdentifier));
        var existing = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null) return Ok(service.ToResponse(existing, Request));
        var item = service.Create(request); db.StagedImports.Add(item); await db.SaveChangesAsync(ct);
        return Accepted(service.ToResponse(item, Request));
    }

    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct) => (await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)) is { } x ? Ok(x) : NotFound();
    [HttpPost("{id:guid}/approve"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Approve(Guid id, ReviewRequest request, CancellationToken ct) => Ok(await service.ReviewAndPromote(id, true, request.Note, User, ct));
    [HttpPost("{id:guid}/reject"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reject(Guid id, ReviewRequest request, CancellationToken ct) => Ok(await service.ReviewAndPromote(id, false, request.Note, User, ct));
}
