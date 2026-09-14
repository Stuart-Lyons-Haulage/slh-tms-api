using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Manages the persisted email sender → collection site profiles.
/// These replace the hardcoded SenderDomainCollectionSites dictionary
/// that previously lived in EmailOrderIntakeService.
///
/// When a sender profile exists and AutoApprove = true, orders from that
/// sender are automatically promoted without going through Review, provided
/// the intake parser returns High confidence.
/// </summary>
[ApiController]
[Route("api/v1/email-sender-profiles")]
[Authorize]
public sealed class EmailSenderProfilesController(
    TmsDbContext db,
    ILogger<EmailSenderProfilesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] bool? active,
        CancellationToken ct)
    {
        var query = db.EmailSenderProfiles.AsNoTracking().AsQueryable();
        if (active.HasValue) query = query.Where(p => p.Active == active.Value);
        var profiles = await query.OrderBy(p => p.SenderPattern).ToListAsync(ct);
        return Ok(profiles);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var profile = await db.EmailSenderProfiles.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == id, ct);
        return profile is null ? NotFound() : Ok(profile);
    }

    [HttpPost, Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Create(
        [FromBody] EmailSenderProfileRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SenderPattern))
            return BadRequest(new { message = "SenderPattern is required." });
        if (string.IsNullOrWhiteSpace(request.CollectionSiteName))
            return BadRequest(new { message = "CollectionSiteName is required." });
        if (request.PatternType is not ("Email" or "Domain"))
            return BadRequest(new { message = "PatternType must be 'Email' or 'Domain'." });

        var normalised = request.SenderPattern.Trim().ToLowerInvariant();
        if (await db.EmailSenderProfiles.AnyAsync(p => p.SenderPattern == normalised && p.Active, ct))
            return Conflict(new { message = $"An active profile for '{normalised}' already exists." });

        // Optionally resolve the site ID for display linking
        Guid? siteId = null;
        if (!string.IsNullOrWhiteSpace(request.CollectionSiteName))
        {
            var normSite = Normalise(request.CollectionSiteName);
            var site = await db.Sites.AsNoTracking()
                .Where(s => s.Active)
                .FirstOrDefaultAsync(s =>
                    Normalise(s.Name) == normSite ||
                    Normalise(s.DriverTextName ?? "") == normSite, ct);
            siteId = site?.Id;
        }

        var profile = new EmailSenderProfile
        {
            SenderPattern       = normalised,
            PatternType         = request.PatternType,
            CollectionSiteName  = request.CollectionSiteName.Trim(),
            CollectionSiteId    = siteId,
            DefaultCustomerCode = request.DefaultCustomerCode?.Trim().ToUpperInvariant(),
            AutoApprove         = request.AutoApprove,
            Notes               = request.Notes?.Trim(),
            Active              = true,
            CreatedAtUtc        = DateTimeOffset.UtcNow,
            CreatedBy           = User.Identity?.Name
        };

        db.EmailSenderProfiles.Add(profile);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "EmailSenderProfile created: {Pattern} → {Site} by {User}.",
            normalised, profile.CollectionSiteName, User.Identity?.Name);

        return Created($"/api/v1/email-sender-profiles/{profile.Id}", profile);
    }

    [HttpPut("{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] EmailSenderProfileRequest request,
        CancellationToken ct)
    {
        var profile = await db.EmailSenderProfiles.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null) return NotFound();

        profile.SenderPattern       = request.SenderPattern.Trim().ToLowerInvariant();
        profile.PatternType         = request.PatternType;
        profile.CollectionSiteName  = request.CollectionSiteName.Trim();
        profile.DefaultCustomerCode = request.DefaultCustomerCode?.Trim().ToUpperInvariant();
        profile.AutoApprove         = request.AutoApprove;
        profile.Notes               = request.Notes?.Trim();
        profile.Active              = request.Active;
        profile.UpdatedAtUtc        = DateTimeOffset.UtcNow;
        profile.UpdatedBy           = User.Identity?.Name;

        await db.SaveChangesAsync(ct);
        return Ok(profile);
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var profile = await db.EmailSenderProfiles.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null) return NotFound();
        profile.Active      = false;
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        profile.UpdatedBy   = User.Identity?.Name;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Test endpoint — shows what site/customer would be resolved for a given sender
    /// address without consuming the email.
    /// </summary>
    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve(
        [FromQuery] string sender,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sender))
            return BadRequest(new { message = "sender query parameter is required." });

        var profiles = await db.EmailSenderProfiles.AsNoTracking()
            .Where(p => p.Active).ToListAsync(ct);

        var normalised = sender.Trim().ToLowerInvariant();
        var domain     = ExtractDomain(normalised);

        var exact = profiles.FirstOrDefault(p =>
            p.PatternType == "Email" &&
            p.SenderPattern.Equals(normalised, StringComparison.OrdinalIgnoreCase));

        var domainMatch = domain is null ? null : profiles.FirstOrDefault(p =>
            p.PatternType == "Domain" &&
            p.SenderPattern.Equals(domain, StringComparison.OrdinalIgnoreCase));

        var matched = exact ?? domainMatch;
        return Ok(new
        {
            sender          = normalised,
            matched         = matched is not null,
            matchType       = matched?.PatternType,
            pattern         = matched?.SenderPattern,
            collectionSite  = matched?.CollectionSiteName,
            customerCode    = matched?.DefaultCustomerCode,
            autoApprove     = matched?.AutoApprove ?? false,
            profileId       = matched?.Id
        });
    }

    private static string Normalise(string? v) =>
        new((v ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? ExtractDomain(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 || at == address.Length - 1
            ? null
            : address[(at + 1)..].Trim('>', ')', ']');
    }
}

public sealed record EmailSenderProfileRequest(
    string SenderPattern,
    string PatternType,
    string CollectionSiteName,
    string? DefaultCustomerCode = null,
    bool AutoApprove = false,
    string? Notes = null,
    bool Active = true);
