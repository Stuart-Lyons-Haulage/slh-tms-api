using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Applies planner-approved sender mappings to parsed mailbox orders. Exact addresses
/// outrank domains; ambiguous/conflicting mappings always remain in review.
/// </summary>
public static class CustomerEmailRouteService
{
    public static async Task<EmailIntakeParseResult> ApplyAsync(
        TmsDbContext db,
        EmailIntakeParseResult parsed,
        MailboxEmailIntakeRequest request,
        CancellationToken ct)
    {
        if (parsed.Orders.Count == 0 || NormalizeEmail(request.SenderAddress) is not { } sender)
            return parsed;

        List<CustomerEmailRoute> routes;
        try
        {
            routes = await db.CustomerEmailRoutes.AsNoTracking().Where(route => route.Active).ToListAsync(ct);
        }
        catch (Exception ex) when (DatabaseObjectUnavailable(ex))
        {
            // Migration 043 is deliberately non-blocking in production. Until its
            // maintenance migration is applied, order intake must remain available.
            return parsed;
        }

        var subject = request.Subject ?? string.Empty;
        var domain = sender[(sender.IndexOf('@') + 1)..];
        var matches = routes
            .Select(route => new { Route = route, Score = Score(route, sender, domain, subject) })
            .Where(match => match.Score >= 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Route.Id)
            .ToList();
        if (matches.Count == 0) return parsed;

        var bestScore = matches[0].Score;
        var best = matches.Where(match => match.Score == bestScore).Select(match => match.Route).ToList();
        var distinctDestinations = best
            .Select(route => $"{route.CustomerCode.Trim().ToUpperInvariant()}|{route.DefaultSiteCode?.Trim().ToUpperInvariant()}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctDestinations.Count != 1)
        {
            return parsed with
            {
                Warnings = parsed.Warnings.Append(
                    $"Sender {sender} has conflicting CRM routes. Planner review is required before this sender can be automated.")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        var route = best[0];
        Site? site = null;
        if (!string.IsNullOrWhiteSpace(route.DefaultSiteCode))
        {
            site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(
                item => item.Active && item.ExternalCode == route.DefaultSiteCode, ct);
        }

        var routed = new List<ParsedEmailOrder>(parsed.Orders.Count);
        var globalWarnings = parsed.Warnings.ToList();
        foreach (var order in parsed.Orders)
        {
            var root = JsonNode.Parse(order.Payload.GetRawText())?.AsObject() ?? new JsonObject();
            var warnings = order.Warnings.ToList();
            var conflict = ApplyRoute(root, route, site, sender, warnings);
            if (route.RequiresReview || conflict)
                root["plannerReady"] = false;
            routed.Add(order with
            {
                Payload = JsonSerializer.SerializeToElement(root),
                Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            });
        }

        if (route.RequiresReview)
            globalWarnings.Add($"CRM route for {sender} is marked Requires Review.");
        return parsed with
        {
            Orders = routed,
            Warnings = globalWarnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>
    /// Learns only from an order a planner has approved. A sender used for more than
    /// one collection site retains the customer mapping but loses its unsafe site default.
    /// Cross-customer conflicts are flagged for review and never silently reassigned.
    /// </summary>
    public static async Task LearnFromApprovedOrderAsync(
        TmsDbContext db,
        JsonElement payload,
        string customerCode,
        CancellationToken ct)
    {
        var sender = NormalizeEmail(Text(payload, "sourceSender") ?? Text(payload, "senderAddress"));
        if (sender is null || sender.EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase)) return;

        var siteCode = Text(payload, "collectionSiteCode");
        if (string.IsNullOrWhiteSpace(siteCode))
        {
            var siteName = Text(payload, "collectionSite") ?? Text(payload, "sellerName");
            if (!string.IsNullOrWhiteSpace(siteName))
                siteCode = await db.Sites.AsNoTracking()
                    .Where(site => site.Active && (site.Name == siteName || site.DriverTextName == siteName))
                    .Select(site => site.ExternalCode)
                    .FirstOrDefaultAsync(ct);
        }

        try
        {
            var existing = await db.CustomerEmailRoutes
                .Where(route => route.Active && route.SenderEmail == sender && route.SubjectContains == null)
                .OrderBy(route => route.Id)
                .FirstOrDefaultAsync(ct);
            var normalCustomer = customerCode.Trim().ToUpperInvariant();
            if (existing is null)
            {
                existing = new CustomerEmailRoute
                {
                    CustomerCode = normalCustomer,
                    SenderEmail = sender,
                    SenderDomain = sender[(sender.IndexOf('@') + 1)..],
                    ParserType = Clip(Text(payload, "parserTemplate") ?? Text(payload, "mappingTemplate"), 120),
                    DefaultSiteCode = Clip(siteCode, 80),
                    RequiresReview = false,
                    Active = true
                };
                db.CustomerEmailRoutes.Add(existing);
            }
            else if (!string.Equals(existing.CustomerCode, normalCustomer, StringComparison.OrdinalIgnoreCase))
            {
                existing.RequiresReview = true;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(existing.DefaultSiteCode)
                    && !string.IsNullOrWhiteSpace(siteCode)
                    && !string.Equals(existing.DefaultSiteCode, siteCode, StringComparison.OrdinalIgnoreCase))
                    existing.DefaultSiteCode = null;
                existing.RequiresReview = false;
            }

            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "CustomerEmailRoute",
                EntityId = existing.Id,
                Action = existing.RequiresReview ? "EmailRouteConflict" : "EmailRouteLearnedFromApprovedOrder",
                ChangedBy = "Order approval",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    senderEmail = sender,
                    customerCode = normalCustomer,
                    defaultSiteCode = existing.DefaultSiteCode,
                    existing.RequiresReview
                })
            });
        }
        catch (Exception ex) when (DatabaseObjectUnavailable(ex))
        {
            // Preserve approval availability until the optional CRM-link table exists.
        }
    }

    private static bool ApplyRoute(JsonObject root, CustomerEmailRoute route, Site? site, string sender, List<string> warnings)
    {
        var conflict = false;
        var currentCustomer = Text(root, "customerCode");
        if (IsUnmapped(currentCustomer))
            root["customerCode"] = route.CustomerCode.Trim().ToUpperInvariant();
        else if (!string.Equals(currentCustomer, route.CustomerCode, StringComparison.OrdinalIgnoreCase))
        {
            conflict = true;
            warnings.Add($"Parsed customer {currentCustomer} conflicts with CRM sender route {route.CustomerCode}; planner review retained.");
        }

        if (site is not null)
        {
            var currentCode = Text(root, "collectionSiteCode");
            var currentName = Text(root, "collectionSite") ?? Text(root, "sellerName");
            if (string.IsNullOrWhiteSpace(currentCode) && string.IsNullOrWhiteSpace(currentName))
            {
                root["collectionSiteCode"] = site.ExternalCode;
                root["collectionSiteId"] = site.Id.ToString();
                root["collectionSite"] = site.Name;
                root["sellerName"] = site.Name;
            }
            else if (!string.IsNullOrWhiteSpace(currentCode)
                && !string.Equals(currentCode, site.ExternalCode, StringComparison.OrdinalIgnoreCase))
            {
                conflict = true;
                warnings.Add($"Parsed collection site {currentCode} conflicts with CRM sender route {site.ExternalCode}; planner review retained.");
            }
        }

        root["emailRouteMatched"] = true;
        root["emailRouteId"] = route.Id.ToString();
        root["emailRouteSender"] = sender;
        root["emailRouteCustomerCode"] = route.CustomerCode;
        root["emailRouteDefaultSiteCode"] = route.DefaultSiteCode;
        root["emailRouteRequiresReview"] = route.RequiresReview || conflict;
        return conflict;
    }

    private static int Score(CustomerEmailRoute route, string sender, string domain, string subject)
    {
        var exact = NormalizeEmail(route.SenderEmail);
        var routeDomain = NormalizeDomain(route.SenderDomain);
        var addressMatches = exact is not null && string.Equals(exact, sender, StringComparison.OrdinalIgnoreCase);
        var domainMatches = routeDomain is not null &&
            (string.Equals(domain, routeDomain, StringComparison.OrdinalIgnoreCase)
             || domain.EndsWith($".{routeDomain}", StringComparison.OrdinalIgnoreCase));
        if (!addressMatches && !domainMatches) return -1;
        if (!string.IsNullOrWhiteSpace(route.SubjectContains)
            && !subject.Contains(route.SubjectContains.Trim(), StringComparison.OrdinalIgnoreCase)) return -1;
        return (addressMatches ? 10000 : 1000) + (route.SubjectContains?.Trim().Length ?? 0);
    }

    internal static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var address = new MailAddress(value.Trim()).Address.Trim().ToLowerInvariant();
            return address.Contains('@') ? address : null;
        }
        catch (FormatException) { return null; }
    }

    private static string? NormalizeDomain(string? value)
    {
        var result = value?.Trim().TrimStart('@').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool IsUnmapped(string? value) => string.IsNullOrWhiteSpace(value)
        || value.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
        || value.Equals("UNMAPPED", StringComparison.OrdinalIgnoreCase)
        || value.Equals("GENERAL", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
        {
            value = payload.EnumerateObject()
                .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
        }
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
    }

    private static string? Text(JsonObject payload, string name)
    {
        var value = payload.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    private static bool DatabaseObjectUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Length <= length ? value : value[..length];
}
