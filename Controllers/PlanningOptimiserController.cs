using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning/optimiser"), Authorize]
public sealed class PlanningOptimiserController(
    PlanningOptimiserService service,
    TmsDbContext db,
    ILogger<PlanningOptimiserController> logger) : ControllerBase
{
    [HttpPost("proposals"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Generate([FromBody] GeneratePlanProposalRequest request, CancellationToken ct)
    {
        try
        {
            var proposal = await service.GenerateAsync(request, User.Identity?.Name, ct);
            return CreatedAtAction(nameof(Get), new { id = proposal.Id }, proposal);
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            logger.LogWarning(exception, "Planning optimiser schema was unavailable; applying the embedded schema repair before one retry.");
            db.ChangeTracker.Clear();
            await PlanningSchemaInitializer.Apply(db, logger, ct);
            db.ChangeTracker.Clear();
            try
            {
                var proposal = await service.GenerateAsync(request, User.Identity?.Name, ct);
                return CreatedAtAction(nameof(Get), new { id = proposal.Id }, proposal);
            }
            catch (Exception retryException) when (SchemaUnavailable(retryException))
            {
                logger.LogError(retryException, "Planning optimiser remained unavailable after schema repair.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    code = "PlanningOptimiserSchemaUnavailable",
                    message = "Planning proposal generation is temporarily unavailable while the planning schema catches up. No live runs were created or changed."
                });
            }
        }
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

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }
}
