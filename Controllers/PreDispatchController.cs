using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/runs"), Authorize]
public sealed class PreDispatchController(PreDispatchSafetyService service) : ControllerBase
{
    [HttpGet("{id:guid}/dispatch-readiness")]
    public async Task<IActionResult> Readiness(Guid id, CancellationToken ct)
    {
        try
        {
            return Ok(await service.EvaluateAsync(id, ct));
        }
        catch (PreDispatchException exception) when (exception.Code == "RunNotFound")
        {
            return NotFound(new { exception.Code, exception.Message });
        }
    }

    [HttpPost("{id:guid}/dispatch"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Dispatch(Guid id, [FromBody] ControlledDispatchRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await service.DispatchAsync(id, request, User.Identity?.Name, ct));
        }
        catch (PreDispatchException exception) when (exception.Code == "RunNotFound")
        {
            return NotFound(new { exception.Code, exception.Message });
        }
        catch (PreDispatchException exception)
        {
            return Conflict(new { exception.Code, exception.Message });
        }
    }
}
