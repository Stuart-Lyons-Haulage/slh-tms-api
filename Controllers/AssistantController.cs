using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/assistant"), Authorize]
public sealed class AssistantController(
    TmsAssistantService assistant,
    TmsDbContext db,
    AzureMapsRouteClient maps,
    ILogger<AssistantSafeFixService> safeFixLogger) : ControllerBase
{
    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot([FromQuery] DateOnly? date, CancellationToken ct) =>
        Ok(await assistant.GetSnapshot(date ?? DateOnly.FromDateTime(DateTime.UtcNow), ct));

    [HttpPost("advice")]
    public async Task<IActionResult> Advice(AssistantAdviceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 1000)
            return BadRequest(new { message = "Ask a planning question between 1 and 1000 characters." });

        var plannerQuestion = request.Message.Trim();
        try
        {
            var sites = await db.Sites.AsNoTracking().Where(site => site.Active).Take(2500).ToListAsync(ct);
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
            var normalQuestion = Normalise(plannerQuestion);
            var matchedSites = sites
                .Select(site => new
                {
                    Site = site,
                    Keys = new[] { site.Name, site.ExternalCode, site.DriverTextName }
                        .Concat((site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(Normalise)
                        .Where(value => value.Length >= 3)
                        .ToArray()
                })
                .Where(item => item.Keys.Any(key => normalQuestion.Contains(key, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Site)
                .Take(8)
                .ToList();

            if (matchedSites.Count > 0)
            {
                var context = string.Join(" | ", matchedSites.Select(site =>
                    $"{site.Name} ({site.ExternalCode}): address={site.CollectionAddress ?? "not stored"}; map={site.MapLink ?? "not stored"}; coordinates={(site.Latitude is not null && site.Longitude is not null ? $"{site.Latitude},{site.Longitude}" : "not stored")}"));
                plannerQuestion = $"{plannerQuestion}\nKnown SLH site context: {context}";
            }
        }
        catch
        {
            // Site lookup is enrichment only; Assistant advice must remain available if master-data lookup is temporarily unavailable.
        }

        var userKey = User.FindFirst("oid")?.Value ?? User.Identity?.Name ?? "slh-planner";
        return Ok(await assistant.Advise(request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow), plannerQuestion, userKey, ct));
    }

    [HttpPost("fix-safe-validations"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> FixSafeValidations(CancellationToken ct)
    {
        var safeFixes = new AssistantSafeFixService(db, maps, safeFixLogger);
        return Ok(await safeFixes.Apply(ct));
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public sealed record AssistantAdviceRequest(string Message, DateOnly? Date);