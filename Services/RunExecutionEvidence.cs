using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Canonical evidence model carried from driver sign-on and vehicle movement
/// through live ETA calculation to customer-facing exports.
/// </summary>
public sealed record RunExecutionEvidence(
    Guid LoadId,
    string LoadReference,
    DateOnly PlanningDate,
    Guid? DriverId,
    string? DriverName,
    string? VehicleRegistration,
    DateTimeOffset? TachoSignOnUtc,
    string? TachoVehicleCode,
    int? DriveAvailableTodayMinutes,
    DateTimeOffset? FirstMovementUtc,
    DateTimeOffset? LatestTrackingUtc,
    string TrackingState,
    string EvidenceStatus,
    string EvidenceExplanation);

public static class RunExecutionEvidenceRules
{
    // RoadTech/DOT is polled every minute. Customer-facing ETAs must therefore
    // stop being treated as live promptly if tracking stops updating.
    public static readonly TimeSpan MaximumLiveTrackingAge = TimeSpan.FromMinutes(5);

    public static string EvidenceStatus(
        TachoVehicleDriverStatus? tacho,
        DateTimeOffset? latestTrackingUtc,
        DateTimeOffset now)
    {
        if (tacho is null && latestTrackingUtc is null) return "Unverified";
        if (tacho is null) return "TrackingOnly";
        if (latestTrackingUtc is null) return "TachoOnly";
        return now - latestTrackingUtc <= MaximumLiveTrackingAge ? "VerifiedLive" : "TrackingStale";
    }

    public static string Explanation(
        TachoVehicleDriverStatus? tacho,
        DateTimeOffset? firstMovementUtc,
        DateTimeOffset? latestTrackingUtc,
        DateTimeOffset now)
    {
        if (tacho is null && latestTrackingUtc is null)
            return "No matched TachoMaster duty or DOT/Falcon tracking evidence is available for this run.";
        if (tacho is null)
            return "DOT/Falcon tracking is available, but no current TachoMaster duty was matched to the allocated vehicle.";
        if (latestTrackingUtc is null)
            return "TachoMaster sign-on is available, but no DOT/Falcon movement has been matched to the allocated vehicle.";
        var freshness = now - latestTrackingUtc <= MaximumLiveTrackingAge ? "fresh" : "stale";
        var movement = firstMovementUtc is null ? "No movement event has been recorded yet." : $"First vehicle movement was recorded at {firstMovementUtc:O}.";
        return $"TachoMaster sign-on at {tacho.DutyStartUtc:O}; DOT/Falcon tracking is {freshness}. {movement}";
    }
}
