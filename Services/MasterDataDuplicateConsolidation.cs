using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record MasterDataDuplicateConsolidationResult(
    SiteMasterConsolidationResult Sites,
    int MarketDuplicatesArchived,
    int VehicleDuplicatesArchived,
    int VehicleFuelDetailsRecovered);

public static class MasterDataDuplicateConsolidation
{
    public static async Task<MasterDataDuplicateConsolidationResult> RunAsync(
        TmsDbContext db,
        string actor,
        ILogger logger,
        CancellationToken ct)
    {
        var sites = await SiteMasterConsolidation.ReconcileAsync(db, actor, ct);
        var markets = await ConsolidateMarketsAsync(db, actor, ct);
        var (vehicles, fuelRecovered) = await ConsolidateVehiclesAsync(db, actor, logger, ct);
        return new MasterDataDuplicateConsolidationResult(sites, markets, vehicles, fuelRecovered);
    }

    private static async Task<int> ConsolidateMarketsAsync(TmsDbContext db, string actor, CancellationToken ct)
    {
        var rows = await db.MarketContacts.Where(row => row.Active).ToListAsync(ct);
        var archived = 0;

        foreach (var group in rows
            .GroupBy(row => MarketIdentity(row.Market, row.Name, row.StandOrLocation), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Count() > 1))
        {
            var canonical = group
                .OrderByDescending(row => !string.IsNullOrWhiteSpace(row.MarketKey))
                .ThenByDescending(MarketCompleteness)
                .ThenBy(row => row.Id)
                .First();

            foreach (var duplicate in group.Where(row => row.Id != canonical.Id))
            {
                canonical.Salesman ??= duplicate.Salesman;
                canonical.Sender ??= duplicate.Sender;
                canonical.ReadOnlyMapPdfUrl ??= duplicate.ReadOnlyMapPdfUrl;
                canonical.MarketKey ??= duplicate.MarketKey;
                duplicate.Active = false;
                archived++;

                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "MarketContact",
                    EntityId = canonical.Id,
                    Action = "MergedDuplicateMarketIdentity",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        canonicalMarketContactId = canonical.Id,
                        canonicalMarketKey = canonical.MarketKey,
                        market = canonical.Market,
                        name = canonical.Name,
                        standOrLocation = canonical.StandOrLocation,
                        duplicateMarketContactId = duplicate.Id,
                        duplicateMarketKey = duplicate.MarketKey
                    })
                });
            }
        }

        if (archived > 0) await db.SaveChangesAsync(ct);
        return archived;
    }

    private static async Task<(int Archived, int FuelRecovered)> ConsolidateVehiclesAsync(
        TmsDbContext db,
        string actor,
        ILogger logger,
        CancellationToken ct)
    {
        var vehicles = await db.Vehicles.ToListAsync(ct);
        var loadUse = await db.Loads.AsNoTracking()
            .Where(load => load.VehicleId != null)
            .GroupBy(load => load.VehicleId!.Value)
            .Select(group => new { VehicleId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.VehicleId, item => item.Count, ct);

        var archived = 0;
        var fuelRecovered = 0;
        foreach (var group in vehicles
            .GroupBy(vehicle => Normalise(vehicle.Registration), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Count() > 1))
        {
            var canonical = group
                .OrderByDescending(vehicle => vehicle.Active)
                .ThenByDescending(vehicle => !string.IsNullOrWhiteSpace(vehicle.FleetioId))
                .ThenByDescending(vehicle => loadUse.GetValueOrDefault(vehicle.Id))
                .ThenByDescending(VehicleCompleteness)
                .ThenBy(vehicle => vehicle.Id)
                .First();

            foreach (var duplicate in group.Where(vehicle => vehicle.Id != canonical.Id))
            {
                var hadFuelGap = HasFuelGap(canonical) && HasFuelData(duplicate);
                PreserveVehicleDetail(canonical, duplicate);
                if (hadFuelGap && HasFuelData(canonical)) fuelRecovered++;

                if (!duplicate.Active) continue;

                foreach (var load in await db.Loads.Where(load => load.VehicleId == duplicate.Id).ToListAsync(ct))
                    load.VehicleId = canonical.Id;

                try
                {
                    foreach (var run in await db.PlanProposalRuns.Where(run => run.VehicleId == duplicate.Id).ToListAsync(ct))
                        run.VehicleId = canonical.Id;
                    foreach (var candidate in await db.PlanProposalCandidates.Where(candidate => candidate.VehicleId == duplicate.Id).ToListAsync(ct))
                    {
                        var conflict = await db.PlanProposalCandidates.AnyAsync(existing =>
                            existing.Id != candidate.Id &&
                            existing.ProposalRunId == candidate.ProposalRunId &&
                            existing.VehicleId == canonical.Id &&
                            existing.DriverId == candidate.DriverId, ct);
                        if (conflict) db.PlanProposalCandidates.Remove(candidate);
                        else candidate.VehicleId = canonical.Id;
                    }
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Optional planning vehicle references could not be reassigned while consolidating {VehicleId}.", duplicate.Id);
                }

                try
                {
                    foreach (var allocation in await db.RunResourceAllocations.Where(allocation => allocation.VehicleId == duplicate.Id).ToListAsync(ct))
                        allocation.VehicleId = canonical.Id;
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Run allocation vehicle references could not be reassigned while consolidating {VehicleId}.", duplicate.Id);
                }

                try
                {
                    foreach (var mapping in await db.IntegrationMappings.Where(mapping => mapping.TmsEntityType == "Vehicle" && mapping.TmsEntityId == duplicate.Id).ToListAsync(ct))
                        mapping.TmsEntityId = canonical.Id;
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Vehicle integration mappings could not be reassigned while consolidating {VehicleId}.", duplicate.Id);
                }

                duplicate.Active = false;
                archived++;
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Vehicle",
                    EntityId = canonical.Id,
                    Action = "MergedDuplicateRegistration",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        canonicalVehicleId = canonical.Id,
                        canonicalRegistration = canonical.Registration,
                        duplicateVehicleId = duplicate.Id,
                        duplicateRegistration = duplicate.Registration,
                        fuelDetailsRecovered = hadFuelGap
                    })
                });
            }
        }

        if (archived > 0 || fuelRecovered > 0) await db.SaveChangesAsync(ct);
        return (archived, fuelRecovered);
    }

    private static void PreserveVehicleDetail(Vehicle target, Vehicle source)
    {
        target.FleetNumber ??= source.FleetNumber;
        target.VIN ??= source.VIN;
        target.VehicleSite ??= source.VehicleSite;
        target.OwnerType ??= source.OwnerType;
        target.Abbreviation ??= source.Abbreviation;
        target.Transmission ??= source.Transmission;
        target.DvsCompliant ??= source.DvsCompliant;
        target.CabMobile ??= source.CabMobile;
        target.FuelProvider ??= source.FuelProvider;
        target.FuelPin ??= source.FuelPin;
        target.ShellCard ??= source.ShellCard;
        target.BpRedCard ??= source.BpRedCard;
        target.BpPlainCard ??= source.BpPlainCard;
        target.FuelPinSecretName ??= source.FuelPinSecretName;
        target.FuelCardLastFour ??= source.FuelCardLastFour;
        target.Notes ??= source.Notes;
        target.FleetioId ??= source.FleetioId;
        target.FleetioName ??= source.FleetioName;
        target.FleetioStatus ??= source.FleetioStatus;
    }

    private static bool HasFuelData(Vehicle vehicle) =>
        !string.IsNullOrWhiteSpace(vehicle.FuelPin) ||
        !string.IsNullOrWhiteSpace(vehicle.ShellCard) ||
        !string.IsNullOrWhiteSpace(vehicle.BpRedCard) ||
        !string.IsNullOrWhiteSpace(vehicle.BpPlainCard) ||
        !string.IsNullOrWhiteSpace(vehicle.FuelProvider) ||
        !string.IsNullOrWhiteSpace(vehicle.FuelCardLastFour);

    private static bool HasFuelGap(Vehicle vehicle) =>
        string.IsNullOrWhiteSpace(vehicle.FuelPin) ||
        (string.IsNullOrWhiteSpace(vehicle.ShellCard) && string.IsNullOrWhiteSpace(vehicle.BpRedCard) && string.IsNullOrWhiteSpace(vehicle.BpPlainCard));

    private static int VehicleCompleteness(Vehicle vehicle) => new string?[]
    {
        vehicle.FleetNumber, vehicle.VIN, vehicle.VehicleSite, vehicle.CabMobile,
        vehicle.FuelProvider, vehicle.FuelPin, vehicle.ShellCard, vehicle.BpRedCard,
        vehicle.BpPlainCard, vehicle.FleetioId, vehicle.Notes
    }.Count(value => !string.IsNullOrWhiteSpace(value));

    private static int MarketCompleteness(MarketContact row) => new string?[]
    {
        row.MarketKey, row.Salesman, row.Sender, row.ReadOnlyMapPdfUrl
    }.Count(value => !string.IsNullOrWhiteSpace(value));

    private static string MarketIdentity(string? market, string? name, string? stand) =>
        $"{Normalise(market)}|{Normalise(name)}|{Normalise(stand)}";

    private static string Normalise(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
