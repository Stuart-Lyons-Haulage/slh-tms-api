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
            var expected = OperationalGeofencePayload.ExpectedFenceCount;
            var expectedProgression = OperationalGeofencePayload.ExpectedProgressionFenceCount;
            var count = EmbeddedGeofenceEngine.ApprovedFences.Count;
            var progressionCount = EmbeddedGeofenceEngine.ApprovedProgressionFenceCount;
            var healthy = count == expected && progressionCount == expectedProgression;
            var payload = new
            {
                status = healthy ? "healthy" : "invalid",
                revision,
                sourceRecordCount = OperationalGeofencePayload.ExpectedSourceRecordCount,
                geofenceCount = count,
                expectedGeofenceCount = expected,
                progressionGeofenceCount = progressionCount,
                expectedProgressionGeofenceCount = expectedProgression,
                payloadSha256 = OperationalGeofencePayload.ExpectedJsonSha256,
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
                sourceRecordCount = OperationalGeofencePayload.ExpectedSourceRecordCount,
                geofenceCount = 0,
                expectedGeofenceCount = OperationalGeofencePayload.ExpectedFenceCount,
                expectedProgressionGeofenceCount = OperationalGeofencePayload.ExpectedProgressionFenceCount,
                payloadSha256 = OperationalGeofencePayload.ExpectedJsonSha256,
                payloadReady = false,
                error = exception.GetType().Name,
                message = exception.GetBaseException().Message,
                checkedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }
}
