using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning/optimiser"), Authorize]
public sealed class PlanningOptimiserController(PlanningOptimiserService service, TmsDbContext db) : ControllerBase
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

    [HttpPost("proposals/{id:guid}/apply"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Apply(Guid id, [FromBody] ApplyPlanProposalRequest request, CancellationToken ct)
    {
        try
        {
            var result = await new PlanningProposalApplicationService(db).ApplyAsync(id, request, User.Identity?.Name, ct);
            return Ok(result);
        }
        catch (PlanProposalApplyException exception) when (exception.Code == "ProposalNotFound")
        {
            return NotFound(new { exception.Code, exception.Message });
        }
        catch (PlanProposalApplyException exception)
        {
            return Conflict(new { exception.Code, exception.Message });
        }
    }
}
