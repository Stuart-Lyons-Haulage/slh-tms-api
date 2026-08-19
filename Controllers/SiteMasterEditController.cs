using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/site-master")]
[Authorize]
public sealed class SiteMasterEditController(TmsDbContext db) : ControllerBase
{
    [HttpPut("{id:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Update(Guid id, ResilientSiteUpdateRequest request, CancellationToken ct)
    {
        var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (site is null) return NotFound(new { message = "Site not found." });

        var before = JsonSerializer.Serialize(site);
        site.ExternalCode = CleanRequired(request.ExternalCode, site.ExternalCode);
        site.Name = CleanRequired(request.Name, site.Name);
        site.DriverTextName = Clean(request.DriverTextName);
        site.CollectionAddress = Clean(request.CollectionAddress);
        site.CollectionInstructions = Clean(request.CollectionInstructions);
        site.MapLink = Clean(request.MapLink);

        // Commit the operational site first. Audit logging is deliberately separated so
        // a missing/lagging MasterDataAudits schema can never roll back a valid amendment.
        await db.SaveChangesAsync(ct);

        var auditRecorded = false;
        try
        {
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Site",
                EntityId = id,
                Action = "Updated",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    before = JsonDocument.Parse(before).RootElement,
                    after = JsonDocument.Parse(JsonSerializer.Serialize(site)).RootElement
                }),
                ChangedBy = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown"
            });
            await db.SaveChangesAsync(ct);
            auditRecorded = true;
        }
        catch (Exception ex) when (IsAuditStorageUnavailable(ex))
        {
            db.ChangeTracker.Clear();
        }

        return Ok(new
        {
            site.Id,
            site.ExternalCode,
            site.Name,
            site.DriverTextName,
            site.CollectionAddress,
            site.CollectionInstructions,
            site.MapLink,
            site.Active,
            auditRecorded
        });
    }

    private static bool IsAuditStorageUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("MasterDataAudits", StringComparison.OrdinalIgnoreCase)
            || message.Contains("MasterDataAudit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not exist or you do not have permissions", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission was denied", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string CleanRequired(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public sealed record ResilientSiteUpdateRequest(
    string? ExternalCode,
    string? Name,
    string? DriverTextName,
    string? CollectionAddress,
    string? CollectionInstructions,
    string? MapLink);
