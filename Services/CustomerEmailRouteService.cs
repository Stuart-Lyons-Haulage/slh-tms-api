using System.Net.Mail;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, CachedRouteCandidates> RouteCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RouteCacheLifetime = TimeSpan.FromMinutes(1);

    /// <summary>True only for an unambiguous, planner-approved route. It is used to
    /// bypass broad Site Master reads before parsing a recurring sender's email.</summary>
    public static async Task<bool> HasApprovedRouteAsync(TmsDbContext db, MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        var match = await FindRouteAsync(db, request, ct);
        return match is { RequiresReview: false };
    }

    public static void InvalidateCache() => RouteCache.Clear();

    public static async Task<EmailIntakeParseResult> ApplyAsync(
        TmsDbContext db,
        EmailIntakeParseResult parsed,
        MailboxEmailIntakeRequest request,
        CancellationToken ct)
    {
        if (parsed.Orders.Count == 0 || NormalizeEmail(request.SenderAddress) is not { } sender)
            return parsed;

        var route = await FindRouteAsync(db, request, ct);
        if (route is null) return parsed;
        if (route.Conflicting)
        {
            return parsed with
            {
                Warnings = parsed.Warnings.Append(
                    $"Sender {sender} has conflicting CRM routes. Planner review is required before this sender can be automated.")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        Site? collectionSite = null;
        Site? deliverySite = null;
        if (!string.IsNullOrWhiteSpace(route.DefaultSiteCode))
        {
            collectionSite = await db.Sites.AsNoTracking().FirstOrDefaultAsync(
                item => item.Active && item.ExternalCode == route.DefaultSiteCode, ct);
        }
        if (!string.IsNullOrWhiteSpace(route.DefaultDeliverySiteCode))
            deliverySite = await db.Sites.AsNoTracking().FirstOrDefaultAsync(
                item => item.Active && item.ExternalCode == route.DefaultDeliverySiteCode, ct);

        var routed = new List<ParsedEmailOrder>(parsed.Orders.Count);
        var globalWarnings = parsed.Warnings.ToList();
        foreach (var order in parsed.Orders)
        {
            var root = JsonNode.Parse(order.Payload.GetRawText())?.AsObject() ?? new JsonObject();
            var warnings = order.Warnings.ToList();
            var conflict = ApplyRoute(root, route, collectionSite, deliverySite, sender, warnings);
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

    private static async Task<RouteMatch?> FindRouteAsync(TmsDbContext db, MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        var sender = NormalizeEmail(request.SenderAddress);
        if (sender is null) return null;
        var domain = sender[(sender.IndexOf('@') + 1)..];
        var key = $"{sender}|{request.Subject?.Trim()}";
        if (!RouteCache.TryGetValue(key, out var cached) || cached.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            try
            {
                // This predicate is deliberately indexable: the former implementation
                // fetched every active CRM route for every incoming email.
                var routes = await db.CustomerEmailRoutes.AsNoTracking()
                    .Where(route => route.Active && (route.SenderEmail == sender || route.SenderDomain == domain))
                    .ToListAsync(ct);
                cached = new CachedRouteCandidates(routes, DateTimeOffset.UtcNow.Add(RouteCacheLifetime));
                RouteCache[key] = cached;
            }
            catch (Exception ex) when (DatabaseObjectUnavailable(ex)) { return null; }
        }

        var subject = request.Subject ?? string.Empty;
        var matches = cached.Routes.Select(route => new { Route = route, Score = Score(route, sender, domain, subject) })
            .Where(match => match.Score >= 0).OrderByDescending(match => match.Score).ThenBy(match => match.Route.Id).ToList();
        if (matches.Count == 0) return null;
        var bestScore = matches[0].Score;
        var best = matches.Where(match => match.Score == bestScore).Select(match => match.Route).ToList();
        var destinations = best.Select(route => $"{route.CustomerCode.Trim().ToUpperInvariant()}|{route.DefaultSiteCode?.Trim().ToUpperInvariant()}|{route.DefaultDeliverySiteCode?.Trim().ToUpperInvariant()}|{route.MarketKey?.Trim()}")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return destinations.Count == 1 ? new RouteMatch(best[0], false) : new RouteMatch(best[0], true);
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
        var deliverySiteCode = Text(payload, "deliverySiteCode");

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
                    DefaultDeliverySiteCode = Clip(deliverySiteCode, 80),
                    MarketKey = Clip(Text(payload, "marketKey"), 160),
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
                var marketConflict = false;
                if (!string.IsNullOrWhiteSpace(existing.DefaultSiteCode)
                    && !string.IsNullOrWhiteSpace(siteCode)
                    && !string.Equals(existing.DefaultSiteCode, siteCode, StringComparison.OrdinalIgnoreCase))
                    existing.DefaultSiteCode = null;
                if (!string.IsNullOrWhiteSpace(existing.DefaultDeliverySiteCode)
                    && !string.IsNullOrWhiteSpace(deliverySiteCode)
                    && !string.Equals(existing.DefaultDeliverySiteCode, deliverySiteCode, StringComparison.OrdinalIgnoreCase))
                    existing.DefaultDeliverySiteCode = null;
                var marketKey = Clip(Text(payload, "marketKey"), 160);
                if (!string.IsNullOrWhiteSpace(marketKey))
                {
                    if (!string.IsNullOrWhiteSpace(existing.MarketKey)
                        && !string.Equals(existing.MarketKey, marketKey, StringComparison.OrdinalIgnoreCase))
                        marketConflict = true;
                    else
                        existing.MarketKey = marketKey;
                }
                existing.RequiresReview = marketConflict;
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
            InvalidateCache();
        }
        catch (Exception ex) when (DatabaseObjectUnavailable(ex))
        {
            // Preserve approval availability until the optional CRM-link table exists.
        }
    }

    private static bool ApplyRoute(JsonObject root, CustomerEmailRoute route, Site? collectionSite, Site? deliverySite, string sender, List<string> warnings)
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

        if (collectionSite is not null)
        {
            var currentCode = Text(root, "collectionSiteCode");
            var currentName = Text(root, "collectionSite") ?? Text(root, "sellerName");
            if (string.IsNullOrWhiteSpace(currentCode) && string.IsNullOrWhiteSpace(currentName))
            {
                root["collectionSiteCode"] = collectionSite.ExternalCode;
                root["collectionSiteId"] = collectionSite.Id.ToString();
                root["collectionSite"] = collectionSite.Name;
                root["sellerName"] = collectionSite.Name;
            }
            else if (!string.IsNullOrWhiteSpace(currentCode)
                && !string.Equals(currentCode, collectionSite.ExternalCode, StringComparison.OrdinalIgnoreCase))
            {
                conflict = true;
                warnings.Add($"Parsed collection site {currentCode} conflicts with CRM sender route {collectionSite.ExternalCode}; planner review retained.");
            }
        }
        if (deliverySite is not null)
        {
            var currentCode = Text(root, "deliverySiteCode");
            var currentName = Text(root, "deliverySite") ?? Text(root, "destination");
            if (string.IsNullOrWhiteSpace(currentCode) && string.IsNullOrWhiteSpace(currentName))
            {
                root["deliverySiteCode"] = deliverySite.ExternalCode;
                root["deliverySiteId"] = deliverySite.Id.ToString();
                root["deliverySite"] = deliverySite.Name;
                root["destination"] = deliverySite.Name;
            }
            else if (!string.IsNullOrWhiteSpace(currentCode) && !string.Equals(currentCode, deliverySite.ExternalCode, StringComparison.OrdinalIgnoreCase))
            {
                conflict = true;
                warnings.Add($"Parsed delivery site {currentCode} conflicts with CRM sender route {deliverySite.ExternalCode}; planner review retained.");
            }
        }

        root["emailRouteMatched"] = true;
        root["emailRouteId"] = route.Id.ToString();
        root["emailRouteSender"] = sender;
        root["emailRouteCustomerCode"] = route.CustomerCode;
        if (!string.IsNullOrWhiteSpace(route.MarketKey))
            root["marketKey"] = route.MarketKey.Trim();
        root["emailRouteDefaultSiteCode"] = route.DefaultSiteCode;
        root["emailRouteDefaultDeliverySiteCode"] = route.DefaultDeliverySiteCode;
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

    private sealed record CachedRouteCandidates(IReadOnlyList<CustomerEmailRoute> Routes, DateTimeOffset ExpiresAtUtc);
    private sealed record RouteMatch(CustomerEmailRoute Route, bool Conflicting)
    {
        public static implicit operator CustomerEmailRoute(RouteMatch match) => match.Route;
        public bool RequiresReview => Route.RequiresReview;
        public string CustomerCode => Route.CustomerCode;
        public string? DefaultSiteCode => Route.DefaultSiteCode;
        public string? DefaultDeliverySiteCode => Route.DefaultDeliverySiteCode;
        public string? MarketKey => Route.MarketKey;
        public Guid Id => Route.Id;
    }
}
