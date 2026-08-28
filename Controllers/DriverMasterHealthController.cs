using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/driver-master")]
[AllowAnonymous]
public sealed class DriverMasterHealthController(TmsDbContext db, TachoDriverMasterSyncService sync) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var quality = await sync.QualityAsync(ct);
        var latestPayload = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "tachodrivermastersync")
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .Select(item => item.PayloadJson)
            .FirstOrDefaultAsync(ct);

        int? sourceWorkers = null;
        if (!string.IsNullOrWhiteSpace(latestPayload))
        {
            try
            {
                using var document = JsonDocument.Parse(latestPayload);
                if (document.RootElement.TryGetProperty("sourceWorkers", out var sourceElement) && sourceElement.TryGetInt32(out var sourceCount))
                    sourceWorkers = sourceCount;
            }
            catch (JsonException)
            {
                // The quality endpoint remains useful even if one historic audit payload is malformed.
            }
        }

        var populationAligned = sourceWorkers is > 0 && quality.ActiveDrivers == sourceWorkers.Value;
        var healthy = quality.DuplicateMemberGroups == 0 &&
                      quality.DuplicateCardGroups == 0 &&
                      quality.ActiveWithoutMember == 0 &&
                      populationAligned;

        return Ok(new
        {
            status = healthy ? "healthy" : "attention",
            quality.ActiveDrivers,
            sourceWorkers,
            populationAligned,
            quality.ActiveWithMember,
            quality.ActiveWithCard,
            quality.DuplicateMemberGroups,
            quality.DuplicateCardGroups,
            quality.ActiveWithoutMember,
            quality.ActiveWithoutCard,
            quality.LatestCanonicalSyncUtc
        });
    }
}
