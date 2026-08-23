using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning/optimiser"), Authorize]
public sealed class PlanningOptimiserController(PlanningOptimiserService service) : ControllerBase
{
    [HttpPost("proposals"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Generate([FromBody] GeneratePlanProposalRequest request, CancellationToken ct)
    {
        var proposal = await service.GenerateAsync(request, User.Identity?.Name, ct);
        return CreatedAtAction(nameof(Get), new { id = proposal.Id }, proposal);
    }

    [HttpGet("proposals/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var proposal = await service.GetAsync(id, ct);
        return proposal is null ? NotFound() : Ok(proposal);
    }
}
