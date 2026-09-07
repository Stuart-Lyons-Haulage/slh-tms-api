using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/site-master-details")]
[Authorize]
public sealed class SiteMasterDetailsController(TmsDbContext db) : ControllerBase
{
    [HttpPut("{id:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Update(Guid id, SiteMasterDetailsUpdateRequest request, CancellationToken ct)
    {
        var site = await db.Sites.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (site is null) return NotFound();

        await MasterDetailStore.EnrichSitesAsync(db, new[] { site }, ct);
        var before = new { site.CustomField1, site.CustomField2, site.CustomField3 };
        site.CustomField1 = Clean(request.Notes, 200);
        site.CustomField2 = Clean(request.CustomField2, 200);
        site.CustomField3 = Clean(request.CustomField3, 200);

        var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value;
        await MasterDetailStore.SaveAsync(
            db,
            "site",
            site.ExternalCode,
            JsonSerializer.Serialize(site),
            "SLH unified Site Master editor",
            actor,
            ct);

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = site.Id,
            Action = "SiteDetailsUpdated",
            ChangedBy = actor ?? "unknown",
            ChangesJson = JsonSerializer.Serialize(new
            {
                before,
                after = new { site.CustomField1, site.CustomField2, site.CustomField3 }
            })
        });
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            site.Id,
            notes = site.CustomField1,
            site.CustomField2,
            site.CustomField3
        });
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim();
        return clean.Length <= max ? clean : clean[..max];
    }
}

public sealed record SiteMasterDetailsUpdateRequest(string? Notes, string? CustomField2, string? CustomField3);
