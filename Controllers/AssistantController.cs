using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/assistant"), Authorize]
public sealed class AssistantController(TmsAssistantService assistant) : ControllerBase
{
    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot([FromQuery] DateOnly? date, CancellationToken ct) =>
        Ok(await assistant.GetSnapshot(date ?? DateOnly.FromDateTime(DateTime.UtcNow), ct));

    [HttpPost("advice")]
    public async Task<IActionResult> Advice(AssistantAdviceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 1000)
            return BadRequest(new { message = "Ask a planning question between 1 and 1000 characters." });
        var userKey = User.FindFirst("oid")?.Value ?? User.Identity?.Name ?? "slh-planner";
        return Ok(await assistant.Advise(request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow), request.Message, userKey, ct));
    }

    [HttpPost("fix-safe-validations"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> FixSafeValidations(CancellationToken ct) => Ok(await assistant.ApplySafeFixes(ct));
}

public sealed record AssistantAdviceRequest(string Message, DateOnly? Date);
