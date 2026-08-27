using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record SiteDuplicateCandidate(
    Guid Id,
    string ExternalCode,
    string Name,
    string? DriverTextName,
    string? Aliases,
    int LinkedGeofences,
    bool HasAddress,
    IReadOnlyList<string> SharedIdentityKeys);

public sealed record SiteDuplicateGroup(
    Guid SuggestedCanonicalSiteId,
    string SuggestedCanonicalCode,
    string SuggestedCanonicalName,
    IReadOnlyList<SiteDuplicateCandidate> Sites);

public sealed record SiteCanonicalMergeResult(
    Guid CanonicalSiteId,
    string CanonicalCode,
    string CanonicalName,
    int ArchivedDuplicates,
    int AliasesAdded,
    int GeofencesReassigned,
    int IntegrationMappingsReassigned,
    bool PlanningProfileMerged,
    IReadOnlyList<string> Aliases);

/// <summary>
/// Provides an explicit, auditable path from duplicate Site Master records to one active
/// canonical physical location. The duplicate rows are archived rather than deleted so
/// historical identity remains inspectable. Their names/codes/aliases and geofence links
/// are transferred to the selected canonical Site.
/// </summary>
public static class SiteCanonicalMergeService
{
    private const string SiteDetailType = "masterdetail:site";
    private const string SiteProfileType = "siteplanningprofile";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task<IReadOnlyList<SiteDuplicateGroup>> FindDuplicateGroupsAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).OrderBy(site => site.Name).ToListAsync(ct);
        if (sites.Count < 2) return [];
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        var fences = await db.SiteGeofences.AsNoTracking().Where(fence => fence.Active && fence.SiteId != null).ToListAsync(ct);
        var fenceCount = fences.GroupBy(fence => fence.SiteId!.Value).ToDictionary(group => group.Key, group => group.Count());

        // Build connected duplicate groups. A Site can be linked by more than one known
        // spelling (for example NWF-Selsey -> Selsey -> Selsey (Natures Way)).
        var parent = sites.ToDictionary(site => site.Id, site => site.Id);
        Guid Find(Guid id)
        {
            var root = id;
            while (parent[root] != root) root = parent[root];
            while (parent[id] != id)
            {
                var next = parent[id];
                parent[id] = root;
                id = next;
            }
            return root;
        }
        void Union(Guid left, Guid right)
        {
            var a = Find(left);
            var b = Find(right);
            if (a != b) parent[b] = a;
        }

        var byKey = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            foreach (var key in SiteCanonicalIdentity.Keys(site))
            {
                if (!byKey.TryGetValue(key, out var ids)) byKey[key] = ids = [];
                ids.Add(site.Id);
            }
        }
        foreach (var ids in byKey.Values.Where(ids => ids.Distinct().Count() > 1))
        {
            var distinct = ids.Distinct().ToList();
            for (var index = 1; index < distinct.Count; index++) Union(distinct[0], distinct[index]);
        }

        var groups = sites.GroupBy(site => Find(site.Id)).Where(group => group.Count() > 1).ToList();
        var result = new List<SiteDuplicateGroup>();
        foreach (var group in groups)
        {
            var rows = group.ToList();
            var scored = rows
                .Select(site => new
                {
                    Site = site,
                    Score = fenceCount.GetValueOrDefault(site.Id) * 1000
                        + (site.ExternalCode.StartsWith("SITE", StringComparison.OrdinalIgnoreCase) ? 100 : 0)
                        + (!string.IsNullOrWhiteSpace(site.CollectionAddress) ? 20 : 0)
                        + (!string.IsNullOrWhiteSpace(site.DriverTextName) ? 10 : 0)
                        + SiteCanonicalIdentity.Aliases(site.Aliases).Count()
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Site.ExternalCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var suggested = scored[0].Site;

            result.Add(new SiteDuplicateGroup(
                suggested.Id,
                suggested.ExternalCode,
                suggested.Name,
                rows
                    .OrderBy(site => site.Id == suggested.Id ? 0 : 1)
                    .ThenBy(site => site.ExternalCode, StringComparer.OrdinalIgnoreCase)
                    .Select(site => new SiteDuplicateCandidate(
                        site.Id,
                        site.ExternalCode,
                        site.Name,
                        site.DriverTextName,
                        site.Aliases,
                        fenceCount.GetValueOrDefault(site.Id),
                        !string.IsNullOrWhiteSpace(site.CollectionAddress),
                        rows.Where(other => other.Id != site.Id)
                            .SelectMany(other => SiteCanonicalIdentity.SharedKeys(site, other))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(key => key.Length)
                            .ToList()))
                    .ToList()));
        }

        return result
            .OrderBy(group => group.SuggestedCanonicalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.SuggestedCanonicalCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<SiteCanonicalMergeResult> MergeAsync(
        TmsDbContext db,
        Guid canonicalSiteId,
        IEnumerable<Guid> duplicateSiteIds,
        string? actor,
        CancellationToken ct)
    {
        var duplicateIds = duplicateSiteIds
            .Where(id => id != Guid.Empty && id != canonicalSiteId)
            .Distinct()
            .Take(25)
            .ToList();
        if (canonicalSiteId == Guid.Empty) throw new InvalidOperationException("Choose a canonical Site before merging duplicates.");
        if (duplicateIds.Count == 0) throw new InvalidOperationException("Choose at least one duplicate Site to merge.");

        var selectedIds = duplicateIds.Append(canonicalSiteId).ToHashSet();
        var selected = await db.Sites.Where(site => selectedIds.Contains(site.Id)).ToListAsync(ct);
        var canonical = selected.SingleOrDefault(site => site.Id == canonicalSiteId)
            ?? throw new InvalidOperationException("The selected canonical Site no longer exists.");
        if (!canonical.Active) throw new InvalidOperationException("The canonical Site must be active before duplicates can be merged into it.");

        var duplicates = selected.Where(site => duplicateIds.Contains(site.Id)).ToList();
        if (duplicates.Count != duplicateIds.Count) throw new InvalidOperationException("One or more duplicate Sites no longer exist.");

        await MasterDetailStore.EnrichSitesAsync(db, selected, ct);
        foreach (var duplicate in duplicates)
        {
            if (!SiteCanonicalIdentity.LooksEquivalent(canonical, duplicate))
                throw new InvalidOperationException($"{duplicate.ExternalCode} · {duplicate.Name} does not share a recognised Site identity with {canonical.ExternalCode} · {canonical.Name}. Nothing was merged.");
        }

        var aliases = new HashSet<string>(SiteCanonicalIdentity.Aliases(canonical.Aliases), StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in duplicates)
        {
            foreach (var alias in new[] { duplicate.ExternalCode, duplicate.Name, duplicate.DriverTextName }.Where(value => !string.IsNullOrWhiteSpace(value)))
                aliases.Add(alias!.Trim());
            foreach (var alias in SiteCanonicalIdentity.Aliases(duplicate.Aliases)) aliases.Add(alias);
        }
        aliases.RemoveWhere(alias => string.Equals(alias.Trim(), canonical.Name.Trim(), StringComparison.OrdinalIgnoreCase));

        // Keep the selected canonical record but fill genuinely missing descriptive fields
        // from the retired aliases. Conflicting non-empty values are never overwritten.
        canonical.DriverTextName ??= FirstValue(duplicates.Select(site => site.DriverTextName));
        canonical.CollectionAddress ??= FirstValue(duplicates.Select(site => site.CollectionAddress));
        canonical.CollectionInstructions ??= FirstValue(duplicates.Select(site => site.CollectionInstructions));
        canonical.MapLink ??= FirstValue(duplicates.Select(site => site.MapLink));
        canonical.OperationalRegion ??= FirstValue(duplicates.Select(site => site.OperationalRegion));

        var fences = await db.SiteGeofences
            .Where(fence => fence.SiteId != null && duplicateIds.Contains(fence.SiteId.Value))
            .ToListAsync(ct);
        foreach (var fence in fences)
        {
            fence.SiteId = canonical.Id;
            fence.SiteNumber = canonical.ExternalCode;
            fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        var mappings = await db.IntegrationMappings.Where(mapping => duplicateIds.Contains(mapping.TmsEntityId)).ToListAsync(ct);
        foreach (var mapping in mappings)
        {
            mapping.TmsEntityId = canonical.Id;
            mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mapping.UpdatedBy = actor;
        }

        foreach (var duplicate in duplicates) duplicate.Active = false;

        var aliasesAdded = await SaveCanonicalAliasesAsync(db, canonical, aliases, actor, ct);
        await ArchiveDuplicateDetailRowsAsync(db, canonical, duplicates, actor, ct);
        var profileMerged = await MergePlanningProfilesAsync(db, canonical, duplicates, actor, ct);

        var changedBy = string.IsNullOrWhiteSpace(actor) ? "TMS Site Master cleanup" : actor;
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = canonical.Id,
            Action = "MergedDuplicates",
            ChangedBy = changedBy,
            ChangesJson = JsonSerializer.Serialize(new
            {
                canonical = new { canonical.Id, canonical.ExternalCode, canonical.Name },
                duplicates = duplicates.Select(site => new { site.Id, site.ExternalCode, site.Name }).ToArray(),
                aliasesAdded,
                geofencesReassigned = fences.Count,
                integrationMappingsReassigned = mappings.Count,
                planningProfileMerged = profileMerged
            })
        });
        foreach (var duplicate in duplicates)
        {
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Site",
                EntityId = duplicate.Id,
                Action = "ArchivedAsDuplicate",
                ChangedBy = changedBy,
                ChangesJson = JsonSerializer.Serialize(new
                {
                    duplicate = new { duplicate.Id, duplicate.ExternalCode, duplicate.Name },
                    canonical = new { canonical.Id, canonical.ExternalCode, canonical.Name }
                })
            });
        }

        await db.SaveChangesAsync(ct);
        canonical.Aliases = string.Join("; ", aliases.OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase));

        return new SiteCanonicalMergeResult(
            canonical.Id,
            canonical.ExternalCode,
            canonical.Name,
            duplicates.Count,
            aliasesAdded,
            fences.Count,
            mappings.Count,
            profileMerged,
            aliases.OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string? FirstValue(IEnumerable<string?> values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static async Task<int> SaveCanonicalAliasesAsync(
        TmsDbContext db,
        Site canonical,
        IReadOnlyCollection<string> aliases,
        string? actor,
        CancellationToken ct)
    {
        var key = $"{SiteDetailType}:{NormaliseKey(canonical.ExternalCode)}";
        var row = await db.StagedImports.SingleOrDefaultAsync(item => item.IdempotencyKey == key, ct);
        JsonObject payload;
        try { payload = row is null ? new JsonObject() : JsonNode.Parse(row.PayloadJson)?.AsObject() ?? new JsonObject(); }
        catch (JsonException) { payload = new JsonObject(); }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (payload["aliases"] is JsonValue aliasNode && aliasNode.TryGetValue<string>(out var current))
            foreach (var alias in SiteCanonicalIdentity.Aliases(current)) existing.Add(alias);

        var before = existing.Count;
        foreach (var alias in aliases) existing.Add(alias);
        payload["externalCode"] = canonical.ExternalCode;
        payload["aliases"] = string.Join("; ", existing.OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase));

        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = SiteDetailType,
                IdempotencyKey = key,
                PayloadJson = payload.ToJsonString(),
                Source = "TMS canonical Site merge"
            };
            db.StagedImports.Add(row);
        }
        row.EntityType = SiteDetailType;
        row.PayloadJson = payload.ToJsonString();
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = actor;
        row.ReviewNote = "Duplicate Site identities merged into one canonical Site; former names/codes retained as aliases.";
        return existing.Count - before;
    }

    private static async Task ArchiveDuplicateDetailRowsAsync(
        TmsDbContext db,
        Site canonical,
        IReadOnlyCollection<Site> duplicates,
        string? actor,
        CancellationToken ct)
    {
        var keys = duplicates.Select(site => $"{SiteDetailType}:{NormaliseKey(site.ExternalCode)}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.Where(row => keys.Contains(row.IdempotencyKey)).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status = StagingStatus.Archived;
            row.ReviewedAtUtc = DateTimeOffset.UtcNow;
            row.ReviewedBy = actor;
            row.ReviewNote = $"Archived after Site identity was merged into canonical {canonical.ExternalCode} · {canonical.Name}.";
        }
    }

    private static async Task<bool> MergePlanningProfilesAsync(
        TmsDbContext db,
        Site canonical,
        IReadOnlyCollection<Site> duplicates,
        string? actor,
        CancellationToken ct)
    {
        var ids = duplicates.Select(site => site.Id).Append(canonical.Id).ToHashSet();
        var keys = ids.Select(id => $"{SiteProfileType}:{id:N}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.Where(row => row.EntityType == SiteProfileType && keys.Contains(row.IdempotencyKey)).ToListAsync(ct);
        if (rows.Count == 0) return false;

        SitePlanningProfile? canonicalProfile = null;
        var canonicalRow = rows.FirstOrDefault(row => string.Equals(row.IdempotencyKey, $"{SiteProfileType}:{canonical.Id:N}", StringComparison.OrdinalIgnoreCase));
        if (canonicalRow is not null)
        {
            try { canonicalProfile = JsonSerializer.Deserialize<SitePlanningProfile>(canonicalRow.PayloadJson, JsonOptions); }
            catch (JsonException) { }
        }

        var duplicateProfiles = rows
            .Where(row => row != canonicalRow)
            .Select(row =>
            {
                try { return (Row: row, Profile: JsonSerializer.Deserialize<SitePlanningProfile>(row.PayloadJson, JsonOptions)); }
                catch (JsonException) { return (Row: row, Profile: (SitePlanningProfile?)null); }
            })
            .ToList();

        var sourceProfile = duplicateProfiles.Select(item => item.Profile).FirstOrDefault(profile => profile is not null);
        var merged = canonicalProfile ?? new SitePlanningProfile(canonical.Id, canonical.ExternalCode, canonical.Name, null, canonical.OperationalRegion ?? "Other", canonical.CollectionAddress);
        if (sourceProfile is not null)
        {
            merged = merged with
            {
                SiteId = canonical.Id,
                ExternalCode = canonical.ExternalCode,
                Name = canonical.Name,
                DefaultTemperatureC = merged.DefaultTemperatureC ?? sourceProfile.DefaultTemperatureC,
                Region = string.IsNullOrWhiteSpace(merged.Region) || string.Equals(merged.Region, "Other", StringComparison.OrdinalIgnoreCase) ? sourceProfile.Region : merged.Region,
                Address = merged.Address ?? sourceProfile.Address ?? canonical.CollectionAddress
            };
        }
        else
        {
            merged = merged with { SiteId = canonical.Id, ExternalCode = canonical.ExternalCode, Name = canonical.Name, Address = merged.Address ?? canonical.CollectionAddress };
        }

        if (canonicalRow is null)
        {
            canonicalRow = new StagedImport
            {
                EntityType = SiteProfileType,
                IdempotencyKey = $"{SiteProfileType}:{canonical.Id:N}",
                PayloadJson = "{}",
                Source = "TMS canonical Site merge"
            };
            db.StagedImports.Add(canonicalRow);
        }
        canonicalRow.PayloadJson = JsonSerializer.Serialize(merged, JsonOptions);
        canonicalRow.Status = StagingStatus.Promoted;
        canonicalRow.ReviewedAtUtc = DateTimeOffset.UtcNow;
        canonicalRow.ReviewedBy = actor;
        canonicalRow.ReviewNote = "Planning profile retained on canonical Site after duplicate merge.";

        foreach (var item in duplicateProfiles)
        {
            item.Row.Status = StagingStatus.Archived;
            item.Row.ReviewedAtUtc = DateTimeOffset.UtcNow;
            item.Row.ReviewedBy = actor;
            item.Row.ReviewNote = $"Planning profile archived after Site merge into {canonical.ExternalCode}.";
        }
        return duplicateProfiles.Count > 0;
    }

    private static string NormaliseKey(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}