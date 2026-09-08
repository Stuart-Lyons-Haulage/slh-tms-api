using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/beta-optimiser")]
[Authorize]
public sealed class BetaOptimiserController(
    BetaOptimiserService service,
    ILogger<BetaOptimiserController> logger) : ControllerBase
{
    [HttpGet("day")]
    public async Task<IActionResult> AnalyseDay([FromQuery] DateOnly planningDate, CancellationToken ct)
    {
        try
        {
            return Ok(await service.AnalyseDayAsync(planningDate, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Beta Optimiser day analysis failed for {PlanningDate}.", planningDate);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "BetaOptimiserUnavailable",
                message = "Beta Optimiser could not complete the read-only analysis. No planning data was changed."
            });
        }
    }

    [HttpPost("planner-csv/compare")]
    public async Task<IActionResult> ComparePlannerCsv(
        [FromBody] BetaPlannerComparisonRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await service.AnalysePlannerRoutesAsync(request, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Beta Optimiser planner CSV comparison failed for {PlanningDate}.", request.PlanningDate);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "BetaOptimiserPlannerComparisonUnavailable",
                message = "The uploaded planner benchmark could not be analysed. No planning data was changed."
            });
        }
    }
}
