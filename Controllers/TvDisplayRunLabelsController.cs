using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/tv-display/run-labels")]
public sealed class TvDisplayRunLabelsController(TmsDbContext db) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
    {
        if (!await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct))
            return Unauthorized(new { message = "This TV display is not paired." });

        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var loads = (await PlanningResilience.ReadLoadsAsync(db, day, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .ToList();

        // PlannerNotes is a resilient/not-mapped operational field, so enrich both
        // SQL-backed and planning-register loads before building the visible run name.
        await LoadCommercialStore.EnrichAsync(db, loads, ct);

        return Ok(new
        {
            planningDate = day,
            labels = loads.Select(load => new
            {
                loadId = load.Id,
                displayReference = RunDisplayLabel.For(load)
            }).ToList()
        });
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}

internal static partial class RunDisplayLabel
{
    private static readonly TimeZoneInfo UkZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [GeneratedRegex(@"^PLAN-\d{8}-(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InternalReferenceRegex();

    [GeneratedRegex(@"^(?:RUN[\s_-]*)?(\d+)(?:[\s_-]*(AM|PM))?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericRunRegex();

    [GeneratedRegex(@"\b(AM|PM)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodRegex();

    public static string For(Load load)
    {
        var plannerRun = NoteValue(load.PlannerNotes, "Planner run");
        var runType = NoteValue(load.PlannerNotes, "Run type");
        var source = string.IsNullOrWhiteSpace(plannerRun) ? StripInternalReference(load.Reference) : plannerRun.Trim();
        var period = ExplicitPeriod(source) ?? ExplicitPeriod(runType) ?? PeriodFromFirstStop(load);
        return Format(source, period);
    }

    private static string Format(string source, string? period)
    {
        var clean = source.Trim();
        var numeric = NumericRunRegex().Match(clean);
        if (numeric.Success)
        {
            var number = int.TryParse(numeric.Groups[1].Value, out var parsed) ? parsed.ToString() : numeric.Groups[1].Value;
            var resolvedPeriod = ExplicitPeriod(numeric.Groups[2].Value) ?? period;
            return $"Run {number}{(resolvedPeriod is null ? string.Empty : $" {resolvedPeriod}")}";
        }

        clean = Regex.Replace(clean, @"^RUN[\s:_-]*", string.Empty, RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"[-_]+", " ").Trim();
        if (string.IsNullOrWhiteSpace(clean)) clean = "TBC";

        var existing = ExplicitPeriod(clean);
        if (existing is not null)
        {
            clean = PeriodRegex().Replace(clean, string.Empty).Trim();
            return $"Run {clean} {existing}";
        }

        return $"Run {clean}{(period is null ? string.Empty : $" {period}")}";
    }

    private static string StripInternalReference(string reference)
    {
        var match = InternalReferenceRegex().Match(reference.Trim());
        return match.Success ? match.Groups[1].Value : reference.Trim();
    }

    private static string? NoteValue(string? notes, string key)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        foreach (var part in notes.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var prefix = $"{key}:";
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return part[prefix.Length..].Trim();
        }
        return null;
    }

    private static string? ExplicitPeriod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = PeriodRegex().Match(value);
        if (match.Success) return match.Groups[1].Value.ToUpperInvariant();
        if (value.Contains("morning", StringComparison.OrdinalIgnoreCase)) return "AM";
        if (value.Contains("afternoon", StringComparison.OrdinalIgnoreCase) || value.Contains("evening", StringComparison.OrdinalIgnoreCase)) return "PM";
        return null;
    }

    private static string? PeriodFromFirstStop(Load load)
    {
        var first = load.Stops.OrderBy(stop => stop.Sequence).FirstOrDefault(stop => stop.PlannedArrivalUtc is not null)?.PlannedArrivalUtc;
        if (first is null) return null;
        var local = TimeZoneInfo.ConvertTime(first.Value, UkZone);
        return local.Hour < 12 ? "AM" : "PM";
    }
}
