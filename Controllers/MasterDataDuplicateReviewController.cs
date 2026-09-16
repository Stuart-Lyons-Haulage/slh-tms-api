using System.Text.Json;
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
    {
        var candidates = await MasterDataDuplicateReviewService.FindCandidatesAsync(db, entityType, ct);
        var mergeable = candidates
            .Where(IsOperationallySafeAutoMergeCandidate)
            .Take(50)
            .ToList();

        var messages = new List<string>();
        var merged = 0;
        foreach (var candidate in mergeable)
        {
            var result = await MasterDataDuplicateReviewService.MergeAsync(
                db,
                candidate.EntityType,
                new MasterDataDuplicateMergeRequest(candidate.Canonical.Id, candidate.Duplicates.Select(row => row.Id).ToList(), "Automatic high-confidence master-data duplicate merge"),
                Actor(),
                ct);
            merged += result.Merged;
            messages.AddRange(result.Messages);
        }

        return Ok(new MasterDataDuplicateMergeResult(merged, candidates.Count, messages));
    }

    [HttpPost("{entityType}/merge"), Authorize(Policy = "TmsApprove")]
    public async Task<ActionResult<MasterDataDuplicateMergeResult>> Merge(string entityType, MasterDataDuplicateMergeRequest request, CancellationToken ct)
        => Ok(await MasterDataDuplicateReviewService.MergeAsync(db, entityType, request, Actor(), ct));

    [HttpPost("reject"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reject(JsonElement request, CancellationToken ct)
    {
        var candidateId = Text(request, "candidateId", "CandidateId");
        var entityType = Text(request, "entityType", "EntityType");
        var note = Text(request, "note", "Note");

        if (string.IsNullOrWhiteSpace(candidateId) || string.IsNullOrWhiteSpace(entityType))
            return BadRequest(new { error = "candidateId and entityType are required to keep a duplicate candidate separate." });

        await MasterDataDuplicateReviewService.RejectAsync(db, new MasterDataDuplicateRejectRequest(candidateId, entityType, note), Actor(), ct);
        return Ok(new { rejected = true });
    }

    private static bool IsOperationallySafeAutoMergeCandidate(MasterDataDuplicateCandidate candidate)
    {
        if (candidate.Duplicates.Count == 0) return false;
        if (candidate.CanAutoMerge) return true;

        var type = candidate.EntityType.Trim().ToLowerInvariant();
        return type switch
        {
            "site" or "sites" => candidate.Confidence >= 94,
            "market" or "markets" => candidate.Confidence >= 94,
            _ => false
        };
    }

    private static string? Text(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private string Actor() => User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "SLH Assistant";
}
