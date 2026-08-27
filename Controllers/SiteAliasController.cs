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
    [HttpPut("{id:guid}/aliases")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> UpdateAliases(Guid id, SiteAliasUpdateRequest request, CancellationToken ct)
    {
        var site = await db.Sites.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (site is null) return NotFound();

        await MasterDetailStore.EnrichSitesAsync(db, new[] { site }, ct);
        var before = site.Aliases;
        site.Aliases = CleanAliases(request.Aliases);

        await MasterDetailStore.SaveAsync(
            db,
            "site",
            site.ExternalCode,
            JsonSerializer.Serialize(site),
            "SLH Site CRM alias editor",
            User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value,
            ct);

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = site.Id,
            Action = "AliasesUpdated",
            ChangedBy = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown",
            ChangesJson = JsonSerializer.Serialize(new { before, after = site.Aliases })
        });
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            site.Id,
            site.ExternalCode,
            site.Name,
            site.Aliases
        });
    }

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
