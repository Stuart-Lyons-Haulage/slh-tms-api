using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/sites")]
[Authorize]
public sealed class SiteAliasController(TmsDbContext db) : ControllerBase
{
    private const string SiteMasterDetailType = "masterdetail:site";
    private const string AliasEditorSource = "SLH Site CRM alias editor";
    private const string ReviewNote = "Full workbook detail retained in the audited register for legacy production columns.";

    [HttpPut("{id:guid}/aliases")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> UpdateAliases(Guid id, SiteAliasUpdateRequest request, CancellationToken ct)
    {
        var site = await db.Sites.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (site is null) return NotFound();

        await MasterDetailStore.EnrichSitesAsync(db, new[] { site }, ct);
        var before = site.Aliases;
        site.Aliases = CleanAliases(request.Aliases);

        var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value;
        await PersistSiteDetailAsync(site, actor, ct);

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = site.Id,
            Action = "AliasesUpdated",
            ChangedBy = actor ?? "unknown",
            ChangesJson = JsonSerializer.Serialize(new { before, after = site.Aliases })
        });
        await db.SaveChangesAsync(ct);

        // Alias changes are operational Master Data. Apply any unique exact alias match to
        // an unlinked Falcon geofence immediately rather than waiting for a re-import/restart.
        var geofenceLinksRepaired = await GeofenceSiteAliasRepair.EnsureAsync(db, ct);

        return Ok(new
        {
            site.Id,
            site.ExternalCode,
            site.Name,
            site.Aliases,
            geofenceLinksRepaired
        });
    }

    private async Task PersistSiteDetailAsync(Site site, string? actor, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(site);
        var idempotencyKey = $"{SiteMasterDetailType}:{NormaliseKey(site.ExternalCode)}";
        var reviewedAt = DateTimeOffset.UtcNow;

        // Site aliases live in the audited master-detail register rather than the base Site
        // table. Update that row directly so repeated Site CRM saves cannot fail because an
        // immediately preceding Site edit has advanced the staged row-version.
        var updated = await db.StagedImports
            .Where(item => item.IdempotencyKey == idempotencyKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PayloadJson, payload)
                .SetProperty(item => item.Status, StagingStatus.Promoted)
                .SetProperty(item => item.Source, AliasEditorSource)
                .SetProperty(item => item.ReviewedAtUtc, reviewedAt)
                .SetProperty(item => item.ReviewedBy, actor)
                .SetProperty(item => item.ReviewNote, ReviewNote), ct);

        if (updated > 0) return;

        db.StagedImports.Add(new StagedImport
        {
            EntityType = SiteMasterDetailType,
            IdempotencyKey = idempotencyKey,
            PayloadJson = payload,
            Status = StagingStatus.Promoted,
            Source = AliasEditorSource,
            ReviewedAtUtc = reviewedAt,
            ReviewedBy = actor,
            ReviewNote = ReviewNote
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent first save may have inserted the same unique idempotency key.
            // Clear the failed insert and apply the authoritative alias payload to that row.
            db.ChangeTracker.Clear();
            await db.StagedImports
                .Where(item => item.IdempotencyKey == idempotencyKey)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.PayloadJson, payload)
                    .SetProperty(item => item.Status, StagingStatus.Promoted)
                    .SetProperty(item => item.Source, AliasEditorSource)
                    .SetProperty(item => item.ReviewedAtUtc, reviewedAt)
                    .SetProperty(item => item.ReviewedBy, actor)
                    .SetProperty(item => item.ReviewNote, ReviewNote), ct);
        }
    }

    private static string NormaliseKey(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? CleanAliases(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var aliases = value
            .Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(alias => alias.Trim())
            .Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (aliases.Count == 0) return null;
        var joined = string.Join("; ", aliases);
        return joined.Length <= 500 ? joined : joined[..500];
    }
}

public sealed record SiteAliasUpdateRequest(string? Aliases);
