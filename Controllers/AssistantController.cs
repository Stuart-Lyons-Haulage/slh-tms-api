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
    public async Task<IActionResult> Snapshot([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var snapshot = await assistant.GetSnapshot(date ?? DateOnly.FromDateTime(DateTime.UtcNow), ct);
        var suggestions = await AddMarketSuggestions(snapshot.Suggestions, ct);
        return Ok(snapshot with { Suggestions = EnableSafeFixes(suggestions) });
    }

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
            db.ChangeTracker.Clear();
        }

        var userKey = User.FindFirst("oid")?.Value ?? User.Identity?.Name ?? "slh-planner";
        var advice = await assistant.Advise(request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow), plannerQuestion, userKey, ct);
        var suggestions = await AddMarketSuggestions(advice.Suggestions, ct);
        return Ok(advice with { Suggestions = EnableSafeFixes(suggestions) });
    }

    [HttpPost("fix-safe-validations"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> FixSafeValidations(CancellationToken ct)
    {
        try
        {
            var safeFixes = new AssistantSafeFixService(db, maps, safeFixLogger);
            return Ok(await safeFixes.Apply(ct));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            safeFixLogger.LogError(exception, "SLH Assistant master-data fixes failed; returning a controlled result instead of HTTP 500.");
            db.ChangeTracker.Clear();
            return Ok(new SafeFixResult(
                0,
                1,
                Array.Empty<string>(),
                new[] { $"Master data repair could not complete: {exception.GetBaseException().Message}" }));
        }
    }

    private async Task<IReadOnlyList<AssistantSuggestion>> AddMarketSuggestions(IReadOnlyList<AssistantSuggestion> suggestions, CancellationToken ct)
    {
        try
        {
            var rows = await db.MarketContacts.AsNoTracking().Where(x => x.Active).Take(5000).ToListAsync(ct);
            var result = suggestions.Where(x => x.Id != "markets-validation").ToList();
            var missingRequired = rows.Count(x => string.IsNullOrWhiteSpace(x.Market) || string.IsNullOrWhiteSpace(x.Name));
            var nonCanonical = rows.Count(x => CanonicalMarket(x.Market) != (x.Market ?? string.Empty).Trim());
            var duplicates = rows
                .Where(x => !string.IsNullOrWhiteSpace(x.Market) && !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => $"{Normalise(CanonicalMarket(x.Market))}|{Normalise(x.Name)}|{Normalise(x.StandOrLocation)}", StringComparer.OrdinalIgnoreCase)
                .Count(x => x.Count() > 1);
            var missingStand = rows.Count(x => !string.Equals(CanonicalMarket(x.Market), "Sender", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(x.StandOrLocation));

            if (missingRequired + nonCanonical + duplicates + missingStand > 0)
            {
                var parts = new[]
                {
                    missingRequired > 0 ? $"{missingRequired} missing market/name" : null,
                    duplicates > 0 ? $"{duplicates} duplicate group{(duplicates == 1 ? "" : "s")}" : null,
                    nonCanonical > 0 ? $"{nonCanonical} market name{(nonCanonical == 1 ? "" : "s")} to standardise" : null,
                    missingStand > 0 ? $"{missingStand} stand/location gap{(missingStand == 1 ? "" : "s")}" : null
                }.Where(x => x is not null);
                result.Add(new AssistantSuggestion(
                    "markets-validation",
                    missingRequired + duplicates > 0 ? "high" : "medium",
                    "Clean market master data",
                    $"Market validation found {string.Join(", ", parts)}. Safe fixes will standardise market names, infer obvious stand numbers and consolidate exact duplicate market records; incomplete identities remain review-only.",
                    "Markets",
                    nonCanonical + duplicates > 0 || rows.Any(x => string.IsNullOrWhiteSpace(x.StandOrLocation) && !string.IsNullOrWhiteSpace(InferStand(x.Name)))));
            }
            return result;
        }
        catch
        {
            db.ChangeTracker.Clear();
            return suggestions;
        }
    }

    private static IReadOnlyList<AssistantSuggestion> EnableSafeFixes(IReadOnlyList<AssistantSuggestion> suggestions) =>
        suggestions.Select(item => item.Id == "sites-duplicates" ? item with
        {
            AutoFixAvailable = true,
            Detail = item.Detail + " The Assistant can consolidate records where the normalised name and address/postcode prove they are the same site; ambiguous groups remain review-only."
        } : item).ToList();

    private static string CanonicalMarket(string? value)
    {
        var clean = (value ?? string.Empty).Trim();
        var normal = Normalise(clean);
        if (normal.Contains("covent")) return "Covent";
        if (normal.Contains("spit")) return "Spit";
        if (normal.Contains("western")) return "Western";
        if (normal.Contains("sender")) return "Sender";
        return clean;
    }

    private static string? InferStand(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var bracketStart = name.LastIndexOf('(');
        if (bracketStart >= 0 && name.EndsWith(')') && bracketStart < name.Length - 2) return name[(bracketStart + 1)..^1].Trim();
        return null;
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public sealed record AssistantAdviceRequest(string Message, DateOnly? Date);