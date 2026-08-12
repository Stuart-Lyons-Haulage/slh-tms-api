using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;
[ApiController, Route("api/v1/staging")]
[Authorize]
public sealed class StagingController(TmsDbContext db, StagingService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] StagingStatus? status,
        [FromQuery] string? entityType,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        var query = db.StagedImports.AsNoTracking().AsQueryable();
        if (status is not null) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(x => x.EntityType == entityType.Trim().ToLowerInvariant());

        return Ok(await query.OrderBy(x => x.ReceivedAtUtc).Take(take).ToListAsync(ct));
    }

    [HttpPost, Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Stage(StageImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) return BadRequest(new ErrorResponse("invalid_idempotency_key", "IdempotencyKey is required", HttpContext.TraceIdentifier));
        var existing = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null) return Ok(service.ToResponse(existing, Request));
        var item = service.Create(request); db.StagedImports.Add(item); await db.SaveChangesAsync(ct);
        return Accepted(service.ToResponse(item, Request));
    }

    [HttpPost("batch"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> StageBatch(List<StageImportRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0 || requests.Count > 500) return BadRequest(new ErrorResponse("invalid_batch", "Submit between 1 and 500 records.", HttpContext.TraceIdentifier));
        if (requests.Any(request => string.IsNullOrWhiteSpace(request.IdempotencyKey))) return BadRequest(new ErrorResponse("invalid_idempotency_key", "Every record needs an IdempotencyKey.", HttpContext.TraceIdentifier));
        var keys = requests.Select(request => request.IdempotencyKey).ToList();
        if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count) return BadRequest(new ErrorResponse("duplicate_batch_key", "Idempotency keys must be unique within the batch.", HttpContext.TraceIdentifier));
        var existing = await db.StagedImports.AsNoTracking().Where(item => keys.Contains(item.IdempotencyKey)).ToDictionaryAsync(item => item.IdempotencyKey, ct);
        var existingCount = existing.Count;
        var responses = new List<StageImportResponse>();
        foreach (var request in requests)
        {
            if (existing.TryGetValue(request.IdempotencyKey, out var item)) responses.Add(service.ToResponse(item, Request));
            else { var created = service.Create(request); db.StagedImports.Add(created); responses.Add(service.ToResponse(created, Request)); }
        }
        await db.SaveChangesAsync(ct);
        return Accepted(new { received = responses.Count, existing = existingCount, created = responses.Count - existingCount, records = responses });
    }

    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct) => (await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)) is { } x ? Ok(x) : NotFound();
    [HttpPost("{id:guid}/approve"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Approve(Guid id, ReviewRequest request, CancellationToken ct)
    {
        try { return Ok(await service.ReviewAndPromote(id, true, request.Note, User, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
    [HttpPost("{id:guid}/reject"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reject(Guid id, ReviewRequest request, CancellationToken ct)
    {
        try { return Ok(await service.ReviewAndPromote(id, false, request.Note, User, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
