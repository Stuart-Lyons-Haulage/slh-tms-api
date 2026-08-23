using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/geofences")]
public sealed class GeofencePayloadHealthController(IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Get()
    {
        var revision = configuration["Deployment:Revision"] ?? Environment.GetEnvironmentVariable("Deployment__Revision");
        try
        {
            var expected = GeofenceSeedPayload.ApprovedGeofenceCount;
            var count = EmbeddedGeofenceEngine.ApprovedFences.Count;
            var progressionCount = EmbeddedGeofenceEngine.ApprovedProgressionFenceCount;
            var healthy = count == expected && progressionCount == 335;
            var payload = new
            {
                status = healthy ? "healthy" : "invalid",
                revision,
                sourceRecordCount = GeofenceSeedPayload.SourceRecordCount,
                geofenceCount = count,
                expectedGeofenceCount = expected,
                progressionGeofenceCount = progressionCount,
                expectedProgressionGeofenceCount = 335,
                payloadSha256 = GeofenceSeedPayload.JsonSha256,
                payloadReady = healthy,
                checkedAtUtc = DateTimeOffset.UtcNow
            };
            return healthy
                ? Ok(payload)
                : StatusCode(StatusCodes.Status503ServiceUnavailable, payload);
        }
        catch (Exception exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "payload-failure",
                revision,
                sourceRecordCount = GeofenceSeedPayload.SourceRecordCount,
                geofenceCount = 0,
                expectedGeofenceCount = GeofenceSeedPayload.ApprovedGeofenceCount,
                expectedProgressionGeofenceCount = 335,
                payloadSha256 = GeofenceSeedPayload.JsonSha256,
                payloadReady = false,
                error = exception.GetType().Name,
                message = exception.GetBaseException().Message,
                checkedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }
}
