using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Applies the assimilated weekly-rest 6 x 24-hour rule to TachoMaster duty history.
/// A weekly rest is recognised from a continuous gap of at least 24 hours between duty blocks;
/// the rest starts when the preceding duty ends and finishes when the following duty starts.
/// The following weekly rest must start no later than 144 hours after the end of the previous weekly rest.
/// </summary>
public sealed class DriverWeeklyRestComplianceService(TachoMasterClient tachoMaster, ILogger<DriverWeeklyRestComplianceService> logger)
{
    private static readonly TimeSpan ReducedWeeklyRest = TimeSpan.FromHours(24);
    private static readonly TimeSpan WeeklyRestDeadline = TimeSpan.FromHours(144);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public bool IsConfigured => tachoMaster.IsConfigured;

    public async Task<WeeklyRestComplianceResult> EvaluateAsync(
        Driver driver,
        DateOnly planningDate,
        DateTimeOffset referenceUtc,
        CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured)
            return WeeklyRestComplianceResult.Unknown("TachoMaster is not configured; weekly-rest availability could not be independently verified.");

        if (!DriverDayCycleCalculator.HasBoundTachoIdentity(driver))
            return WeeklyRestComplianceResult.Unknown("The driver is not bound to a TachoMaster identity.");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var throughDate = planningDate;
            var fromDate = throughDate.AddDays(-8);
            var duties = new List<TachoDriverDutyStatus>();
            for (var day = fromDate; day <= throughDate; day = day.AddDays(1))
                duties.AddRange(await tachoMaster.GetDriverDutyStatusesAsync(day, timeout.Token));

            return Evaluate(driver, referenceUtc, duties);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("TachoMaster exceeded the weekly-rest compliance budget for driver {DriverId}.", driver.Id);
            return WeeklyRestComplianceResult.Unverified("TachoMaster weekly-rest history timed out. Dispatch is stopped until weekly rest can be verified.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster weekly-rest history was unavailable for driver {DriverId}.", driver.Id);
            return WeeklyRestComplianceResult.Unverified("TachoMaster weekly-rest history is unavailable. Dispatch is stopped until weekly rest can be verified.");
        }
    }

    public static WeeklyRestComplianceResult Evaluate(
        Driver driver,
        DateTimeOffset referenceUtc,
        IEnumerable<TachoDriverDutyStatus> source)
    {
        var duties = source
            .Where(item => DriverDayCycleCalculator.MatchesDriver(driver, item))
            .Where(item => item.DutyStartUtc != default)
            .GroupBy(item => new DutyKey(item.MemberCode, item.DutyStartUtc, item.DutyEndUtc, item.VehicleCode))
            .Select(group => group.First())
            .OrderBy(item => item.DutyStartUtc)
            .ToList();

        if (duties.Count == 0)
            return WeeklyRestComplianceResult.Unknown("No recent TachoMaster duty history was returned for this driver.");

        var blocks = MergeDuties(duties, referenceUtc);
        if (blocks.Count == 0)
            return WeeklyRestComplianceResult.Unknown("No usable TachoMaster duty blocks were returned for this driver.");

        var restGaps = new List<WeeklyRestGap>();
        for (var index = 1; index < blocks.Count; index++)
        {
            var previous = blocks[index - 1];
            var current = blocks[index];
            if (previous.EndUtc is not DateTimeOffset previousEnd) continue;
            var gap = current.StartUtc - previousEnd;
            if (gap >= ReducedWeeklyRest)
                restGaps.Add(new WeeklyRestGap(previousEnd, current.StartUtc));
        }

        DateTimeOffset? lastWeeklyRestEndUtc = null;
        DateTimeOffset? missedDeadlineUtc = null;
        foreach (var gap in restGaps)
        {
            if (gap.StartUtc > referenceUtc)
                break;

            if (lastWeeklyRestEndUtc is null)
            {
                if (gap.EndUtc <= referenceUtc)
                    lastWeeklyRestEndUtc = gap.EndUtc;
                continue;
            }

            var deadline = lastWeeklyRestEndUtc.Value + WeeklyRestDeadline;
            if (gap.StartUtc > deadline)
            {
                missedDeadlineUtc = deadline;
                break;
            }

            if (gap.EndUtc <= referenceUtc)
                lastWeeklyRestEndUtc = gap.EndUtc;
            else
                break;
        }

        if (missedDeadlineUtc is not null && referenceUtc >= missedDeadlineUtc.Value)
        {
            var elapsedHours = Math.Max(0, (referenceUtc - lastWeeklyRestEndUtc!.Value).TotalHours);
            return WeeklyRestComplianceResult.Overdue(
                missedDeadlineUtc.Value,
                lastWeeklyRestEndUtc,
                $"Weekly rest was not started by the legal 144-hour deadline. {elapsedHours:0.#} hours have elapsed since the end of the last weekly rest; the legal 6 x 24-hour window has expired.");
        }

        if (lastWeeklyRestEndUtc is null)
        {
            var earliest = blocks[0].StartUtc;
            var conservativeDeadline = earliest + WeeklyRestDeadline;
            if (referenceUtc >= conservativeDeadline)
                return WeeklyRestComplianceResult.Overdue(
                    conservativeDeadline,
                    null,
                    "No weekly rest is visible in the recent TachoMaster history and the 6 x 24-hour window has been exceeded. Do not dispatch this driver until weekly rest is verified.");

            return WeeklyRestComplianceResult.Ready(
                conservativeDeadline,
                null,
                "TachoMaster shows recent duty history within the current 6 x 24-hour window; no completed weekly-rest reset is visible in the returned period.");
        }

        var deadlineAfterLastRest = lastWeeklyRestEndUtc.Value + WeeklyRestDeadline;
        if (referenceUtc >= deadlineAfterLastRest)
        {
            var elapsedHours = Math.Max(0, (referenceUtc - lastWeeklyRestEndUtc.Value).TotalHours);
            return WeeklyRestComplianceResult.Overdue(
                deadlineAfterLastRest,
                lastWeeklyRestEndUtc,
                $"Weekly rest is due. {elapsedHours:0.#} hours have elapsed since the end of the last weekly rest; the legal 144-hour window has expired.");
        }

        var remaining = deadlineAfterLastRest - referenceUtc;
        if (remaining <= TimeSpan.FromHours(12))
        {
            return WeeklyRestComplianceResult.DueSoon(
                deadlineAfterLastRest,
                lastWeeklyRestEndUtc,
                $"Weekly rest is due by {LocalTime(deadlineAfterLastRest)}. Only {remaining.TotalHours:0.#} hours remain in the 6 x 24-hour window.");
        }

        return WeeklyRestComplianceResult.Ready(
            deadlineAfterLastRest,
            lastWeeklyRestEndUtc,
            $"Weekly-rest window is open until {LocalTime(deadlineAfterLastRest)}."
        );
    }

    private static List<DutyBlock> MergeDuties(IReadOnlyList<TachoDriverDutyStatus> duties, DateTimeOffset referenceUtc)
    {
        var blocks = new List<DutyBlock>();
        foreach (var duty in duties.OrderBy(item => item.DutyStartUtc))
        {
            var end = duty.DutyEndUtc;
            if (end is DateTimeOffset dutyEnd && dutyEnd < duty.DutyStartUtc)
                end = duty.DutyStartUtc;

            if (blocks.Count == 0)
            {
                blocks.Add(new DutyBlock(duty.DutyStartUtc, end));
                continue;
            }

            var previous = blocks[^1];
            var previousEffectiveEnd = previous.EndUtc ?? referenceUtc;
            if (duty.DutyStartUtc <= previousEffectiveEnd)
            {
                DateTimeOffset? mergedEnd;
                if (previous.EndUtc is null || end is null) mergedEnd = null;
                else mergedEnd = previous.EndUtc >= end ? previous.EndUtc : end;
                blocks[^1] = new DutyBlock(previous.StartUtc, mergedEnd);
                continue;
            }

            blocks.Add(new DutyBlock(duty.DutyStartUtc, end));
        }
        return blocks;
    }

    private static string LocalTime(DateTimeOffset value)
        => TimeZoneInfo.ConvertTime(value, London).ToString("dd/MM HH:mm");

    private sealed record DutyKey(int MemberCode, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, string VehicleCode);
    private sealed record DutyBlock(DateTimeOffset StartUtc, DateTimeOffset? EndUtc);
    private sealed record WeeklyRestGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}

public sealed record WeeklyRestComplianceResult(
    string Status,
    string Message,
    DateTimeOffset? WeeklyRestDueUtc,
    DateTimeOffset? LastWeeklyRestEndUtc)
{
    public bool IsBlocked => Status is "Overdue" or "Unverified";

    public static WeeklyRestComplianceResult Ready(DateTimeOffset? due, DateTimeOffset? lastEnd, string message)
        => new("Ready", message, due, lastEnd);

    public static WeeklyRestComplianceResult DueSoon(DateTimeOffset due, DateTimeOffset? lastEnd, string message)
        => new("DueSoon", message, due, lastEnd);

    public static WeeklyRestComplianceResult Overdue(DateTimeOffset due, DateTimeOffset? lastEnd, string message)
        => new("Overdue", message, due, lastEnd);

    public static WeeklyRestComplianceResult Unverified(string message)
        => new("Unverified", message, null, null);

    public static WeeklyRestComplianceResult Unknown(string message)
        => new("Unknown", message, null, null);
}