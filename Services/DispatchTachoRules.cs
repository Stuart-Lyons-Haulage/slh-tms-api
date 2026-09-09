using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record DispatchRestRequirement(int Hours, int ReducedDailyRestsUsed, string Evidence);

public sealed record DispatchProjectedBreach(string Code, string Detail, decimal HoursIntoRun);

public static class DispatchTachoRules
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private const int StandardDailyDrivingMinutes = 9 * 60;
    private const int ExtendedDailyDrivingMinutes = 10 * 60;
    private const decimal AbsoluteWeeklyWorkingHours = 60m;

    public static DispatchRestRequirement DeriveRequiredRestPeriod(
        Driver driver,
        IEnumerable<TachoDriverDutyStatus> source)
    {
        var duties = Matching(driver, source)
            .Where(duty => duty.DutyEndUtc is not null)
            .OrderBy(duty => duty.DutyStartUtc)
            .ToList();

        if (duties.Count == 0)
            return new DispatchRestRequirement(11, 0, "No completed Tacho duty was available; regular daily rest is required conservatively.");

        var reducedSinceWeeklyRest = 0;
        for (var index = 1; index < duties.Count; index++)
        {
            var previousEnd = duties[index - 1].DutyEndUtc!.Value;
            var gap = duties[index].DutyStartUtc - previousEnd;
            if (gap >= TimeSpan.FromHours(24))
            {
                reducedSinceWeeklyRest = 0;
                continue;
            }

            if (gap >= TimeSpan.FromHours(9) && gap < TimeSpan.FromHours(11))
                reducedSinceWeeklyRest++;
        }

        var latestMetricCount = duties
            .OrderByDescending(duty => duty.MetricsValidAtUtc ?? duty.DutyStartUtc)
            .Select(duty => duty.ShortDailyRestTakenThisWeek)
            .FirstOrDefault(value => value is not null) ?? 0;

        var reducedUsed = Math.Clamp(Math.Max(reducedSinceWeeklyRest, latestMetricCount), 0, 3);
        if (reducedUsed >= 3)
            return new DispatchRestRequirement(11, reducedUsed, "Tacho evidence shows all three reduced daily rests have been used since the weekly-rest reset.");

        return new DispatchRestRequirement(9, reducedUsed, $"Tacho evidence shows {reducedUsed} of 3 reduced daily rests used since the weekly-rest reset.");
    }

    public static DateTimeOffset? AvailableFrom(DateTimeOffset? shiftEndTimeUtc, DispatchRestRequirement requirement) =>
        shiftEndTimeUtc?.AddHours(requirement.Hours);

    public static DateTimeOffset? LatestShiftEndUtc(Driver driver, IEnumerable<TachoDriverDutyStatus> source) =>
        Matching(driver, source)
            .Where(duty => duty.DutyEndUtc is not null)
            .OrderByDescending(duty => duty.DutyEndUtc)
            .Select(duty => duty.DutyEndUtc)
            .FirstOrDefault();

    public static string? LastVehicleRegistration(Driver driver, IEnumerable<TachoDriverDutyStatus> source) =>
        Matching(driver, source)
            .OrderByDescending(duty => duty.DutyEndUtc ?? duty.DutyStartUtc)
            .Select(duty => string.IsNullOrWhiteSpace(duty.VehicleCode) ? null : duty.VehicleCode.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public static decimal WeeklyWorkingTimeHours(Driver driver, IEnumerable<TachoDriverDutyStatus> source, DateOnly referenceDate)
    {
        var weekStart = referenceDate.AddDays(-(((int)referenceDate.DayOfWeek + 6) % 7));
        var weekEnd = weekStart.AddDays(7);
        var minutes = Matching(driver, source)
            .Where(duty =>
            {
                var localDate = LocalDate(duty.DutyStartUtc);
                return localDate >= weekStart && localDate < weekEnd;
            })
            .Sum(duty => Math.Max(0, duty.WorkMinutes) + Math.Max(0, duty.DriveMinutes));
        return Math.Round(minutes / 60m, 2);
    }

    public static decimal DailyDrivingTimeHours(Driver driver, IEnumerable<TachoDriverDutyStatus> source, DateOnly referenceDate)
    {
        var minutes = Matching(driver, source)
            .Where(duty => LocalDate(duty.DutyStartUtc) == referenceDate)
            .Sum(duty => Math.Max(0, duty.DriveMinutes));
        return Math.Round(minutes / 60m, 2);
    }

    public static bool BreakCompliant(Driver driver, IEnumerable<TachoDriverDutyStatus> source, DateOnly referenceDate)
    {
        var duties = Matching(driver, source).Where(duty => LocalDate(duty.DutyStartUtc) == referenceDate).ToList();
        var driveMinutes = duties.Sum(duty => Math.Max(0, duty.DriveMinutes));
        if (driveMinutes <= 270) return true;
        return duties.Sum(duty => Math.Max(0, duty.BreakMinutes ?? 0)) >= 45;
    }

    public static int DailyDrivingLimitMinutes(Driver driver, IEnumerable<TachoDriverDutyStatus> source)
    {
        var longDays = Matching(driver, source)
            .OrderByDescending(duty => duty.MetricsValidAtUtc ?? duty.DutyStartUtc)
            .Select(duty => duty.LongDaysWorkedThisWeek)
            .FirstOrDefault(value => value is not null) ?? 0;
        return longDays >= 2 ? StandardDailyDrivingMinutes : ExtendedDailyDrivingMinutes;
    }

    public static int? DriveAvailablePlanningDayMinutes(Driver driver, IEnumerable<TachoDriverDutyStatus> source, DateOnly planningDate, DateOnly today)
    {
        var latest = Matching(driver, source).OrderByDescending(duty => duty.MetricsValidAtUtc ?? duty.DutyStartUtc).FirstOrDefault();
        if (latest is null) return null;
        if (planningDate > today) return latest.DriveAvailableTomorrowMinutes;
        return latest.DriveAvailableTodayMinutes;
    }

    public static int? WorkAvailableWeekMinutes(Driver driver, IEnumerable<TachoDriverDutyStatus> source) =>
        Matching(driver, source)
            .OrderByDescending(duty => duty.MetricsValidAtUtc ?? duty.DutyStartUtc)
            .Select(duty => duty.WorkAvailableWeekMinutes)
            .FirstOrDefault(value => value is not null);

    public static string WtdStatus(decimal weeklyWorkingTimeHours) =>
        weeklyWorkingTimeHours >= 48m ? "red" : weeklyWorkingTimeHours >= 40m ? "amber" : "ok";

    public static DispatchProjectedBreach? DetectProjectedBreach(
        decimal weeklyWorkingTimeHours,
        decimal dailyDrivingTimeHours,
        decimal projectedDutyHours,
        decimal projectedDrivingHours,
        decimal dailyDrivingLimitHours,
        decimal? providerWorkAvailableHours = null,
        decimal? providerDriveAvailableHours = null)
    {
        var candidates = new List<DispatchProjectedBreach>();

        var wtdRemaining = AbsoluteWeeklyWorkingHours - weeklyWorkingTimeHours;
        if (wtdRemaining <= 0)
            candidates.Add(new DispatchProjectedBreach("WTD", "WTD breach — before this run", 0));
        else if (projectedDutyHours > wtdRemaining)
            candidates.Add(new DispatchProjectedBreach("WTD", $"WTD breach — {FormatOffset(wtdRemaining)} into this run", wtdRemaining));

        if (providerWorkAvailableHours is decimal workAvailable)
        {
            if (workAvailable <= 0)
                candidates.Add(new DispatchProjectedBreach("WTD", "TachoMaster working-time allowance exhausted before this run", 0));
            else if (projectedDutyHours > workAvailable)
                candidates.Add(new DispatchProjectedBreach("WTD", $"TachoMaster working-time allowance exhausted — {FormatOffset(workAvailable)} into this run", workAvailable));
        }

        var dailyRemaining = dailyDrivingLimitHours - dailyDrivingTimeHours;
        if (dailyRemaining <= 0)
            candidates.Add(new DispatchProjectedBreach("DAILY_DRIVING", "Daily driving limit breach — before this run", 0));
        else if (projectedDrivingHours > dailyRemaining)
            candidates.Add(new DispatchProjectedBreach("DAILY_DRIVING", $"Daily driving limit breach — {FormatOffset(dailyRemaining)} into this run", dailyRemaining));

        if (providerDriveAvailableHours is decimal driveAvailable)
        {
            if (driveAvailable <= 0)
                candidates.Add(new DispatchProjectedBreach("DAILY_DRIVING", "TachoMaster driving allowance exhausted before this run", 0));
            else if (projectedDrivingHours > driveAvailable)
                candidates.Add(new DispatchProjectedBreach("DAILY_DRIVING", $"TachoMaster driving allowance exhausted — {FormatOffset(driveAvailable)} into this run", driveAvailable));
        }

        return candidates.OrderBy(candidate => candidate.HoursIntoRun).ThenBy(candidate => candidate.Code).FirstOrDefault();
    }

    private static IEnumerable<TachoDriverDutyStatus> Matching(Driver driver, IEnumerable<TachoDriverDutyStatus> source) =>
        source
            .Where(duty => DriverDayCycleCalculator.MatchesDriver(driver, duty))
            .GroupBy(duty => new { duty.MemberCode, duty.DutyStartUtc, duty.DutyEndUtc, duty.VehicleCode })
            .Select(group => group.OrderByDescending(duty => duty.MetricsValidAtUtc ?? duty.DutyStartUtc).First());

    private static DateOnly LocalDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, London).DateTime);

    private static string FormatOffset(decimal hours)
    {
        var minutes = Math.Max(0, (int)Math.Ceiling(hours * 60m));
        if (minutes % 60 == 0) return $"{minutes / 60}h";
        if (minutes < 60) return $"{minutes}m";
        return $"{minutes / 60}h {minutes % 60}m";
    }
}
