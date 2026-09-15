using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record MasterDataDuplicateCandidate(
    string CandidateId,
    string EntityType,
    int Confidence,
    string Reason,
    bool CanAutoMerge,
    MasterDataDuplicateRecord Canonical,
    IReadOnlyList<MasterDataDuplicateRecord> Duplicates,
    IReadOnlyList<string> PreservedFields);

public sealed record MasterDataDuplicateRecord(
    Guid Id,
    string Code,
    string Name,
    string? Address,
    string? Postcode,
    bool Active,
    IReadOnlyDictionary<string, object?> Fields);

public sealed record MasterDataDuplicateMergeRequest(Guid CanonicalId, IReadOnlyList<Guid> DuplicateIds, string? Note);
public sealed record MasterDataDuplicateRejectRequest(string CandidateId, string EntityType, string? Note);
public sealed record MasterDataDuplicateMergeResult(int Merged, int Reviewed, IReadOnlyList<string> Messages);

public static class MasterDataDuplicateReviewService
{
    public static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindCandidatesAsync(TmsDbContext db, string? entityType, CancellationToken ct)
    {
        var type = Clean(entityType ?? "sites").ToLowerInvariant();
        if (type is "site" or "sites") return await FindSiteCandidatesAsync(db, ct);
        if (type is "driver" or "drivers") return await FindDriverCandidatesAsync(db, ct);
        if (type is "vehicle" or "vehicles") return await FindVehicleCandidatesAsync(db, ct);
        if (type is "market" or "markets") return await FindMarketCandidatesAsync(db, ct);
        return [];
    }

    public static async Task<MasterDataDuplicateMergeResult> AutoMergeHighConfidenceAsync(TmsDbContext db, string? entityType, string actor, CancellationToken ct)
    {
        var messages = new List<string>();
        var candidates = await FindCandidatesAsync(db, entityType, ct);
        var merged = 0;
        foreach (var candidate in candidates.Where(x => x.CanAutoMerge).Take(50))
        {
            var result = await MergeAsync(db, candidate.EntityType, new MasterDataDuplicateMergeRequest(candidate.Canonical.Id, candidate.Duplicates.Select(x => x.Id).ToList(), "Automatic high-confidence master-data duplicate merge"), actor, ct);
            merged += result.Merged;
            messages.AddRange(result.Messages);
        }
        return new MasterDataDuplicateMergeResult(merged, candidates.Count, messages);
    }

    public static async Task<MasterDataDuplicateMergeResult> MergeAsync(TmsDbContext db, string entityType, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var type = Clean(entityType).ToLowerInvariant();
        return type switch
        {
            "site" or "sites" => await MergeSitesAsync(db, request, actor, ct),
            "driver" or "drivers" => await MergeDriversAsync(db, request, actor, ct),
            "vehicle" or "vehicles" => await MergeVehiclesAsync(db, request, actor, ct),
            "market" or "markets" => await MergeMarketsAsync(db, request, actor, ct),
            _ => new MasterDataDuplicateMergeResult(0, 0, [$"Unsupported duplicate entity type '{entityType}'."])
        };
    }

    public static async Task RejectAsync(TmsDbContext db, MasterDataDuplicateRejectRequest request, string actor, CancellationToken ct)
    {
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = $"Duplicate:{Clean(request.EntityType)}",
            EntityId = Guid.Empty,
            Action = "RejectedDuplicateCandidate",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new { request.CandidateId, request.Note, rejectedAtUtc = DateTimeOffset.UtcNow })
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindSiteCandidatesAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        var byKey = new Dictionary<string, HashSet<Site>>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            var name = Normalise(site.Name);
            var address = NormaliseAddress(site.CollectionAddress);
            var postcode = ExtractPostcode(site.CollectionAddress);
            if (name.Length >= 5 && !string.IsNullOrWhiteSpace(postcode)) Add(byKey, $"name-postcode:{name}|{postcode}", site);
            if (name.Length >= 5 && address.Length >= 8) Add(byKey, $"name-address:{name}|{address}", site);
            if (address.Length >= 12 && !string.IsNullOrWhiteSpace(postcode)) Add(byKey, $"address-postcode:{address}|{postcode}", site);
        }
        return DistinctGroups(byKey.Values)
            .Select(group => BuildSiteCandidate(group))
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.Canonical.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindDriverCandidatesAsync(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.Drivers.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var groups = rows
            .Select(x => new { Driver = x, Key = !string.IsNullOrWhiteSpace(x.TachoMasterDriverId) ? $"tachomaster:{Clean(x.TachoMasterDriverId)}" : Normalise(x.EmployeeNumber).Length > 0 ? $"employee:{Normalise(x.EmployeeNumber)}" : string.Empty })
            .Where(x => x.Key.Length > 0)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.Select(v => v.Driver).ToList())
            .ToList();
        return groups.Select(BuildDriverCandidate).OrderByDescending(x => x.Confidence).ToList();
    }

    private static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindVehicleCandidatesAsync(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var groups = rows
            .Where(x => NormaliseRegistration(x.Registration).Length > 0)
            .GroupBy(x => NormaliseRegistration(x.Registration), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.ToList())
            .ToList();
        return groups.Select(BuildVehicleCandidate).OrderByDescending(x => x.Confidence).ToList();
    }

    private static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindMarketCandidatesAsync(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.MarketContacts.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var groups = rows
            .Where(x => Normalise(x.Market).Length > 0 && Normalise(x.Name).Length > 0)
            .GroupBy(x => $"{Normalise(x.Market)}|{Normalise(x.Name)}|{Normalise(x.StandOrLocation)}", StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.ToList())
            .ToList();
        return groups.Select(BuildMarketCandidate).OrderByDescending(x => x.Confidence).ToList();
    }

    private static async Task<MasterDataDuplicateMergeResult> MergeSitesAsync(TmsDbContext db, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var canonical = await db.Sites.FirstOrDefaultAsync(x => x.Id == request.CanonicalId, ct);
        if (canonical is null) return new MasterDataDuplicateMergeResult(0, 0, ["Canonical site was not found."]);
        var duplicates = await db.Sites.Where(x => request.DuplicateIds.Contains(x.Id) && x.Id != canonical.Id).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, new[] { canonical }.Concat(duplicates).ToList(), ct);
        var messages = new List<string>();
        foreach (var duplicate in duplicates)
        {
            PreserveSiteFields(canonical, duplicate, messages);
            var geofences = await db.SiteGeofences.Where(x => x.SiteId == duplicate.Id).ToListAsync(ct);
            foreach (var geofence in geofences) geofence.SiteId = canonical.Id;
            var mappings = await db.IntegrationMappings.Where(x => x.TmsEntityType == "Site" && x.TmsEntityId == duplicate.Id).ToListAsync(ct);
            foreach (var mapping in mappings) { mapping.TmsEntityId = canonical.Id; mapping.UpdatedAtUtc = DateTimeOffset.UtcNow; mapping.UpdatedBy = actor; }
            duplicate.Active = false;
            await MasterDetailStore.SaveAsync(db, "site", duplicate.ExternalCode, JsonSerializer.Serialize(duplicate), "Duplicate merge archived source site", actor, ct);
        }
        canonical.Aliases = MergeAliases(canonical, duplicates);
        await MasterDetailStore.SaveAsync(db, "site", canonical.ExternalCode, JsonSerializer.Serialize(canonical), "Duplicate merge preserved canonical site address/routing detail", actor, ct);
        db.MasterDataAudits.Add(new MasterDataAudit { EntityType = "Site", EntityId = canonical.Id, Action = "DuplicateMerge", ChangedBy = actor, ChangesJson = JsonSerializer.Serialize(new { canonical = canonical.ExternalCode, merged = duplicates.Select(x => x.ExternalCode), request.Note, messages }) });
        await db.SaveChangesAsync(ct);
        messages.Insert(0, $"Merged {duplicates.Count} site duplicate(s) into {canonical.Name} without blanking address/routing fields.");
        return new MasterDataDuplicateMergeResult(duplicates.Count, duplicates.Count + 1, messages);
    }

    private static async Task<MasterDataDuplicateMergeResult> MergeDriversAsync(TmsDbContext db, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var canonical = await db.Drivers.FirstOrDefaultAsync(x => x.Id == request.CanonicalId, ct);
        if (canonical is null) return new MasterDataDuplicateMergeResult(0, 0, ["Canonical driver was not found."]);
        var duplicates = await db.Drivers.Where(x => request.DuplicateIds.Contains(x.Id) && x.Id != canonical.Id).ToListAsync(ct);
        foreach (var duplicate in duplicates)
        {
            canonical.TachoMasterDriverId = Preserve(canonical.TachoMasterDriverId, duplicate.TachoMasterDriverId);
            canonical.MobileNumber = Preserve(canonical.MobileNumber, duplicate.MobileNumber);
            canonical.DriverType = Preserve(canonical.DriverType, duplicate.DriverType);
            canonical.DriverGroup = Preserve(canonical.DriverGroup, duplicate.DriverGroup);
            canonical.Skills = Preserve(canonical.Skills, duplicate.Skills);
            duplicate.Active = false;
        }
        db.MasterDataAudits.Add(new MasterDataAudit { EntityType = "Driver", EntityId = canonical.Id, Action = "DuplicateMerge", ChangedBy = actor, ChangesJson = JsonSerializer.Serialize(new { canonical = canonical.EmployeeNumber, merged = duplicates.Select(x => x.EmployeeNumber), request.Note }) });
        await db.SaveChangesAsync(ct);
        return new MasterDataDuplicateMergeResult(duplicates.Count, duplicates.Count + 1, [$"Merged {duplicates.Count} driver duplicate(s) into {canonical.DisplayName}."]);
    }

    private static async Task<MasterDataDuplicateMergeResult> MergeVehiclesAsync(TmsDbContext db, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var canonical = await db.Vehicles.FirstOrDefaultAsync(x => x.Id == request.CanonicalId, ct);
        if (canonical is null) return new MasterDataDuplicateMergeResult(0, 0, ["Canonical vehicle was not found."]);
        var duplicates = await db.Vehicles.Where(x => request.DuplicateIds.Contains(x.Id) && x.Id != canonical.Id).ToListAsync(ct);
        foreach (var duplicate in duplicates)
        {
            canonical.FleetNumber = Preserve(canonical.FleetNumber, duplicate.FleetNumber);
            canonical.Abbreviation = Preserve(canonical.Abbreviation, duplicate.Abbreviation);
            canonical.FuelProvider = Preserve(canonical.FuelProvider, duplicate.FuelProvider);
            canonical.CabMobile = Preserve(canonical.CabMobile, duplicate.CabMobile);
            canonical.FuelPin = Preserve(canonical.FuelPin, duplicate.FuelPin);
            canonical.ShellCard = Preserve(canonical.ShellCard, duplicate.ShellCard);
            canonical.BpRedCard = Preserve(canonical.BpRedCard, duplicate.BpRedCard);
            canonical.BpPlainCard = Preserve(canonical.BpPlainCard, duplicate.BpPlainCard);
            duplicate.Active = false;
        }
        db.MasterDataAudits.Add(new MasterDataAudit { EntityType = "Vehicle", EntityId = canonical.Id, Action = "DuplicateMerge", ChangedBy = actor, ChangesJson = JsonSerializer.Serialize(new { canonical = canonical.Registration, merged = duplicates.Select(x => x.Registration), request.Note }) });
        await db.SaveChangesAsync(ct);
        return new MasterDataDuplicateMergeResult(duplicates.Count, duplicates.Count + 1, [$"Merged {duplicates.Count} vehicle duplicate(s) into {canonical.Registration}; fuel/card fields were preserved where available."]);
    }

    private static async Task<MasterDataDuplicateMergeResult> MergeMarketsAsync(TmsDbContext db, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var canonical = await db.MarketContacts.FirstOrDefaultAsync(x => x.Id == request.CanonicalId, ct);
        if (canonical is null) return new MasterDataDuplicateMergeResult(0, 0, ["Canonical market record was not found."]);
        var duplicates = await db.MarketContacts.Where(x => request.DuplicateIds.Contains(x.Id) && x.Id != canonical.Id).ToListAsync(ct);
        foreach (var duplicate in duplicates)
        {
            canonical.StandOrLocation = Preserve(canonical.StandOrLocation, duplicate.StandOrLocation);
            canonical.Salesman = Preserve(canonical.Salesman, duplicate.Salesman);
            canonical.Sender = Preserve(canonical.Sender, duplicate.Sender);
            duplicate.Active = false;
        }
        db.MasterDataAudits.Add(new MasterDataAudit { EntityType = "Market", EntityId = canonical.Id, Action = "DuplicateMerge", ChangedBy = actor, ChangesJson = JsonSerializer.Serialize(new { canonical = canonical.Name, merged = duplicates.Select(x => x.Name), request.Note }) });
        await db.SaveChangesAsync(ct);
        return new MasterDataDuplicateMergeResult(duplicates.Count, duplicates.Count + 1, [$"Merged {duplicates.Count} market duplicate(s) into {canonical.Market} / {canonical.Name}."]);
    }

    private static MasterDataDuplicateCandidate BuildSiteCandidate(IReadOnlyList<Site> group)
    {
        var canonical = group.OrderByDescending(SiteCompleteness).ThenBy(x => x.ExternalCode, StringComparer.OrdinalIgnoreCase).First();
        var duplicates = group.Where(x => x.Id != canonical.Id).ToList();
        var postcode = ExtractPostcode(canonical.CollectionAddress) ?? duplicates.Select(x => ExtractPostcode(x.CollectionAddress)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var exactName = duplicates.All(x => Normalise(x.Name) == Normalise(canonical.Name));
        var samePostcode = !string.IsNullOrWhiteSpace(postcode) && duplicates.All(x => ExtractPostcode(x.CollectionAddress) == postcode);
        var confidence = exactName && samePostcode ? 98 : exactName ? 86 : samePostcode ? 82 : 70;
        return new MasterDataDuplicateCandidate(
            CandidateId($"site:{canonical.Id}:{string.Join(',', duplicates.Select(x => x.Id))}"),
            "sites",
            confidence,
            samePostcode ? "Same normalised site name/address and postcode." : "Likely duplicate site name/address. Review before merging.",
            confidence >= 95,
            SiteRecord(canonical),
            duplicates.Select(SiteRecord).ToList(),
            ["collectionAddress", "mapLink", "latitude", "longitude", "collectionInstructions", "driverTextName", "aliases", "geofences", "integrationMappings"]);
    }

    private static MasterDataDuplicateCandidate BuildDriverCandidate(IReadOnlyList<Driver> group)
    {
        var canonical = group.OrderByDescending(x => new[] { x.TachoMasterDriverId, x.MobileNumber, x.DriverType, x.DriverGroup, x.Skills }.Count(v => !string.IsNullOrWhiteSpace(v))).ThenBy(x => x.DisplayName).First();
        var duplicates = group.Where(x => x.Id != canonical.Id).ToList();
        var confidence = !string.IsNullOrWhiteSpace(canonical.TachoMasterDriverId) ? 99 : 92;
        return new MasterDataDuplicateCandidate(CandidateId($"driver:{canonical.Id}:{string.Join(',', duplicates.Select(x => x.Id))}"), "drivers", confidence, confidence >= 99 ? "Same Tachomaster member number." : "Same employee number.", confidence >= 99, DriverRecord(canonical), duplicates.Select(DriverRecord).ToList(), ["tachomasterDriverId", "mobileNumber", "driverType", "driverGroup", "skills"]);
    }

    private static MasterDataDuplicateCandidate BuildVehicleCandidate(IReadOnlyList<Vehicle> group)
    {
        var canonical = group.OrderByDescending(x => new[] { x.FleetNumber, x.Abbreviation, x.FuelProvider, x.CabMobile, x.FuelPin, x.ShellCard, x.BpRedCard, x.BpPlainCard }.Count(v => !string.IsNullOrWhiteSpace(v))).ThenBy(x => x.Registration).First();
        var duplicates = group.Where(x => x.Id != canonical.Id).ToList();
        return new MasterDataDuplicateCandidate(CandidateId($"vehicle:{canonical.Id}:{string.Join(',', duplicates.Select(x => x.Id))}"), "vehicles", 99, "Same normalised vehicle registration.", true, VehicleRecord(canonical), duplicates.Select(VehicleRecord).ToList(), ["fleetNumber", "abbreviation", "fuelProvider", "cabMobile", "fuelPin", "fuel cards"]);
    }

    private static MasterDataDuplicateCandidate BuildMarketCandidate(IReadOnlyList<MarketContact> group)
    {
        var canonical = group.OrderByDescending(x => new[] { x.StandOrLocation, x.Salesman, x.Sender }.Count(v => !string.IsNullOrWhiteSpace(v))).ThenBy(x => x.Name).First();
        var duplicates = group.Where(x => x.Id != canonical.Id).ToList();
        return new MasterDataDuplicateCandidate(CandidateId($"market:{canonical.Id}:{string.Join(',', duplicates.Select(x => x.Id))}"), "markets", 94, "Same market, customer/sender and stand/location.", false, MarketRecord(canonical), duplicates.Select(MarketRecord).ToList(), ["standOrLocation", "salesman", "sender"]);
    }

    private static void PreserveSiteFields(Site canonical, Site duplicate, List<string> messages)
    {
        var beforeAddress = canonical.CollectionAddress;
        canonical.CollectionAddress = Preserve(canonical.CollectionAddress, duplicate.CollectionAddress);
        canonical.CollectionInstructions = Preserve(canonical.CollectionInstructions, duplicate.CollectionInstructions);
        canonical.DriverTextName = Preserve(canonical.DriverTextName, duplicate.DriverTextName);
        canonical.MapLink = Preserve(canonical.MapLink, duplicate.MapLink);
        canonical.OperationalRegion = Preserve(canonical.OperationalRegion, duplicate.OperationalRegion);
        canonical.Latitude ??= duplicate.Latitude;
        canonical.Longitude ??= duplicate.Longitude;
        if (string.IsNullOrWhiteSpace(beforeAddress) && !string.IsNullOrWhiteSpace(canonical.CollectionAddress)) messages.Add($"Recovered address for {canonical.Name} from duplicate {duplicate.ExternalCode}.");
    }

    public static string? Preserve(string? existing, string? incoming)
        => !string.IsNullOrWhiteSpace(existing) ? existing.Trim() : Clean(incoming);

    private static void Add(Dictionary<string, HashSet<Site>> groups, string key, Site site)
    {
        if (!groups.TryGetValue(key, out var set)) groups[key] = set = [];
        set.Add(site);
    }

    private static IReadOnlyList<IReadOnlyList<Site>> DistinctGroups(IEnumerable<HashSet<Site>> source)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return source.Select(group => group.OrderBy(x => x.Id).ToList())
            .Where(group => group.Count > 1 && seen.Add(string.Join('|', group.Select(x => x.Id).OrderBy(x => x))))
            .Cast<IReadOnlyList<Site>>()
            .ToList();
    }

    private static MasterDataDuplicateRecord SiteRecord(Site site) => new(site.Id, site.ExternalCode, site.Name, site.CollectionAddress, ExtractPostcode(site.CollectionAddress), site.Active, new Dictionary<string, object?> { ["driverTextName"] = site.DriverTextName, ["collectionInstructions"] = site.CollectionInstructions, ["mapLink"] = site.MapLink, ["latitude"] = site.Latitude, ["longitude"] = site.Longitude, ["aliases"] = site.Aliases, ["region"] = site.OperationalRegion });
    private static MasterDataDuplicateRecord DriverRecord(Driver row) => new(row.Id, row.EmployeeNumber, row.DisplayName, null, null, row.Active, new Dictionary<string, object?> { ["tachoMasterDriverId"] = row.TachoMasterDriverId, ["mobileNumber"] = row.MobileNumber, ["driverType"] = row.DriverType, ["driverGroup"] = row.DriverGroup, ["skills"] = row.Skills });
    private static MasterDataDuplicateRecord VehicleRecord(Vehicle row) => new(row.Id, row.Registration, row.Registration, null, null, row.Active, new Dictionary<string, object?> { ["fleetNumber"] = row.FleetNumber, ["abbreviation"] = row.Abbreviation, ["fuelProvider"] = row.FuelProvider, ["cabMobile"] = row.CabMobile, ["fuelPin"] = row.FuelPin, ["shellCard"] = row.ShellCard, ["bpRedCard"] = row.BpRedCard, ["bpPlainCard"] = row.BpPlainCard });
    private static MasterDataDuplicateRecord MarketRecord(MarketContact row) => new(row.Id, row.MarketKey ?? row.Id.ToString("N"), $"{row.Market} / {row.Name}", null, null, row.Active, new Dictionary<string, object?> { ["market"] = row.Market, ["standOrLocation"] = row.StandOrLocation, ["salesman"] = row.Salesman, ["sender"] = row.Sender });
    private static int SiteCompleteness(Site site) => new object?[] { site.CollectionAddress, site.MapLink, site.Latitude, site.Longitude, site.CollectionInstructions, site.DriverTextName, site.Aliases, site.OperationalRegion }.Count(x => x is not null && !string.IsNullOrWhiteSpace(x.ToString()));
    private static string MergeAliases(Site canonical, IReadOnlyCollection<Site> duplicates) => string.Join(", ", new[] { canonical.Name, canonical.DriverTextName, canonical.Aliases }.Concat(duplicates.SelectMany(x => new[] { x.Name, x.DriverTextName, x.Aliases, x.ExternalCode })).Where(x => !string.IsNullOrWhiteSpace(x)).SelectMany(x => x!.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase));
    private static string CandidateId(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value.Trim(), @"\s+", " ");
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string NormaliseRegistration(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NormaliseAddress(string? value) => Normalise(Regex.Replace(value ?? string.Empty, @"\b(road|rd|street|st|avenue|ave|lane|ln|drive|dr|unit|industrial|estate)\b", string.Empty, RegexOptions.IgnoreCase));
    private static string? ExtractPostcode(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; var match = Regex.Match(value.ToUpperInvariant(), @"\b([A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})\b"); return match.Success ? Regex.Replace(match.Groups[1].Value, @"\s+", string.Empty) : null; }
}
