using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves a sender email address or domain against the persisted EmailSenderProfiles table.
/// Called once per intake request (not per order) with a short-lived cache to avoid
/// repeated DB hits within the same request pipeline.
///
/// Replaces the hardcoded SenderDomainCollectionSites dictionary in EmailOrderIntakeService.
/// </summary>
public sealed class EmailSenderProfileResolver(TmsDbContext db)
{
    private List<EmailSenderProfile>? _cache;

    /// <summary>
    /// Returns the best matching profile for the sender, or null if none is configured.
    /// Exact email match takes priority over domain match.
    /// </summary>
    public async Task<EmailSenderProfile?> ResolveAsync(string? senderAddress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(senderAddress)) return null;

        _cache ??= await db.EmailSenderProfiles.AsNoTracking()
            .Where(p => p.Active)
            .ToListAsync(ct);

        var normalised = senderAddress.Trim().ToLowerInvariant();
        var domain     = ExtractDomain(normalised);

        // 1. Exact email match
        var exact = _cache.FirstOrDefault(p =>
            p.PatternType.Equals("Email", StringComparison.OrdinalIgnoreCase) &&
            p.SenderPattern.Equals(normalised, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // 2. Domain suffix match
        if (domain is not null)
        {
            var domainMatch = _cache.FirstOrDefault(p =>
                p.PatternType.Equals("Domain", StringComparison.OrdinalIgnoreCase) &&
                domain.Equals(p.SenderPattern.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
            if (domainMatch is not null) return domainMatch;
        }

        return null;
    }

    private static string? ExtractDomain(string address)
    {
        var at = address.LastIndexOf('@');
        if (at < 0 || at == address.Length - 1) return null;
        return address[(at + 1)..].Trim('>', ')', ']');
    }
}
