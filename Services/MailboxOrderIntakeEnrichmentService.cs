using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Enriches parsed mailbox orders with complete source evidence, master-data
/// matches, structured validation, deterministic duplicate keys and review state.
/// It never replaces a customer's source value with a guessed value.
/// </summary>
public sealed class MailboxOrderIntakeEnrichmentService(TmsDbContext db)
{
    public async Task<ParsedEmailOrder> EnrichAsync(MailboxEmailIntakeEnvelope request, ParsedEmailOrder order, CancellationToken ct)
    {
        var payload = JsonNode.Parse(order.Payload.GetRawText()) as JsonObject ?? new JsonObject();
        var issues = order.Warnings.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => new ValidationIssue("Warning", "PARSER_WARNING", null, x)).ToList();

        var customers = await SafeCustomers(ct);
        var sites = await SafeSites(ct);
        MatchCustomer(payload, customers, issues);
        MatchSite(payload, sites, issues, true);
        MatchSite(payload, sites, issues, false);
        Validate(payload, request, issues);
        AddEvidence(payload, request);

        var hasCritical = issues.Any(x => x.Severity == "Critical");
        var explicitlyNotReady = Bool(payload, "plannerReady") == false;
        payload["importSource"] = "PowerAutomate/InfoMailbox";
        payload["importBatchId"] = BatchId(request.MessageId);
        payload["importedAt"] = DateTimeOffset.UtcNow.ToString("O");
        payload["reviewStatus"] = "Pending Review";
        payload["mappingTemplate"] = First(payload, "intakeParser", "mappingTemplate") ?? "Generic mailbox intake";
        payload["validationStatus"] = hasCritical ? "Critical" : issues.Count > 0 ? "Warning" : "Valid";
        payload["plannerReady"] = !hasCritical && !explicitlyNotReady;
        if (First(payload, "intakeStatus") is null) payload["intakeStatus"] = hasCritical ? "Exception" : "PendingReview";
        payload["extractionConfidence"] = First(payload, "intakeConfidence", "extractionConfidence") ?? (hasCritical ? "Low" : issues.Count > 0 ? "Medium" : "High");
        payload["validationIssues"] = JsonSerializer.SerializeToNode(issues);
        payload["intakeWarnings"] = JsonSerializer.SerializeToNode(issues.Where(x => x.Severity != "Information").Select(x => x.Message).Distinct().ToArray());
        payload["intakeMatchKeys"] = JsonSerializer.SerializeToNode(MatchKeys(payload, order.NaturalKey));
        payload["businessFingerprint"] = Fingerprint(payload);

        var warnings = issues.Where(x => x.Severity != "Information").Select(x => x.Message).Distinct().ToList();
        return new ParsedEmailOrder(order.SourceKey, order.NaturalKey, Element(payload), warnings);
    }

    public ParsedEmailOrder BuildReviewException(MailboxEmailIntakeEnvelope request, string reason, IReadOnlyList<string> parserWarnings)
    {
        var payload = new JsonObject
        {
            ["importSource"] = "PowerAutomate/InfoMailbox",
            ["importBatchId"] = BatchId(request.MessageId),
            ["importedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["reviewStatus"] = "Pending Review",
            ["intakeStatus"] = "Exception",
            ["plannerReady"] = false,
            ["validationStatus"] = "Critical",
            ["extractionConfidence"] = "Low",
            ["mappingTemplate"] = "Unmatched mailbox order"
        };
        AddEvidence(payload, request);
        var warnings = parserWarnings.Where(x => !string.IsNullOrWhiteSpace(x)).Append(reason).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        payload["validationIssues"] = JsonSerializer.SerializeToNode(new[] { new ValidationIssue("Critical", "EXTRACTION_REVIEW_REQUIRED", null, reason) });
        payload["intakeWarnings"] = JsonSerializer.SerializeToNode(warnings);
        payload["intakeMatchKeys"] = JsonSerializer.SerializeToNode(new[] { $"MESSAGE|{Normalise(request.MessageId)}" });
        payload["businessFingerprint"] = Fingerprint(payload);
        return new ParsedEmailOrder("review-exception", $"MESSAGE|{Normalise(request.MessageId)}", Element(payload), warnings);
    }

    public bool ShouldRetainForReview(MailboxEmailIntakeEnvelope request, string? ignoredReason)
    {
        if (string.IsNullOrWhiteSpace(ignoredReason)) return false;
        if (ignoredReason.Contains("Internal Lyons email", StringComparison.OrdinalIgnoreCase) ||
            ignoredReason.Contains("Operational request", StringComparison.OrdinalIgnoreCase) ||
            ignoredReason.Contains("Cancellation", StringComparison.OrdinalIgnoreCase)) return false;
        var source = $"{request.Subject}\n{request.BodyText}\n{request.BodyHtml}";
        if (Regex.IsMatch(source, @"\b(order|purchase order|\bpo\b|pallet|collection|delivery|booking|manifest|load plan|transport)\b", RegexOptions.IgnoreCase)) return true;
        return (request.Attachments ?? []).Any(a => a.IsInline != true && Path.GetExtension(a.Name ?? string.Empty).ToLowerInvariant() is ".xls" or ".xlsx" or ".xlsm" or ".csv" or ".pdf");
    }

    private async Task<List<Customer>> SafeCustomers(CancellationToken ct)
    {
        try { return await db.Customers.AsNoTracking().Where(x => x.Active).ToListAsync(ct); }
        catch { db.ChangeTracker.Clear(); return []; }
    }

    private async Task<List<Site>> SafeSites(CancellationToken ct)
    {
        try { return await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct); }
        catch { db.ChangeTracker.Clear(); return []; }
    }

    private static void MatchCustomer(JsonObject payload, IReadOnlyList<Customer> customers, List<ValidationIssue> issues)
    {
        var source = First(payload, "customerCode", "customer");
        if (string.IsNullOrWhiteSpace(source))
        {
            issues.Add(new("Critical", "MISSING_CUSTOMER", "customerCode", "Customer could not be identified from the source order."));
            return;
        }
        var key = Normalise(source);
        var match = customers.FirstOrDefault(x => Normalise(x.Code) == key || Normalise(x.Name) == key);
        if (match is null)
        {
            issues.Add(new("Warning", "UNKNOWN_CUSTOMER", "customerCode", $"Customer '{source}' is not an exact TMS Master Data match."));
            return;
        }
        payload["masterCustomerId"] = match.Id.ToString();
        payload["masterCustomerCode"] = match.Code;
        payload["masterCustomerName"] = match.Name;
        payload["customerCode"] = match.Code;
    }

    private static void MatchSite(JsonObject payload, IReadOnlyList<Site> sites, List<ValidationIssue> issues, bool collection)
    {
        var source = collection ? First(payload, "collectionSite", "collectionLocation", "sellerName") : First(payload, "deliverySite", "deliveryLocation", "stallNumber");
        if (string.IsNullOrWhiteSpace(source)) return;
        var key = Normalise(source);
        var match = sites.FirstOrDefault(site => new[] { site.ExternalCode, site.Name, site.DriverTextName }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => Normalise(x!) == key));
        if (match is null)
        {
            issues.Add(new("Warning", collection ? "UNKNOWN_COLLECTION_SITE" : "UNKNOWN_DELIVERY_SITE", collection ? "collectionSite" : "deliverySite", $"{(collection ? "Collection" : "Delivery")} site '{source}' is not an exact TMS Master Data match."));
            return;
        }
        var prefix = collection ? "collection" : "delivery";
        payload[prefix + "SiteOriginal"] = source;
        payload[prefix + "SiteId"] = match.Id.ToString();
        payload[prefix + "SiteMatchedName"] = match.Name;
    }

    private static void Validate(JsonObject payload, MailboxEmailIntakeEnvelope request, List<ValidationIssue> issues)
    {
        var collectionDate = First(payload, "collectionDate", "collection_date");
        var deliveryDate = First(payload, "deliveryDate", "delivery_date");
        if (string.IsNullOrWhiteSpace(collectionDate)) issues.Add(new("Critical", "MISSING_COLLECTION_DATE", "collectionDate", "Collection date is missing and must be confirmed before approval."));
        else if (!DateOnly.TryParse(collectionDate, out _)) issues.Add(new("Critical", "INVALID_COLLECTION_DATE", "collectionDate", $"Collection date '{collectionDate}' is invalid."));
        if (string.IsNullOrWhiteSpace(deliveryDate)) issues.Add(new("Warning", "MISSING_DELIVERY_DATE", "deliveryDate", "Delivery date is missing and should be checked."));
        else if (!DateOnly.TryParse(deliveryDate, out _)) issues.Add(new("Critical", "INVALID_DELIVERY_DATE", "deliveryDate", $"Delivery date '{deliveryDate}' is invalid."));

        var customerPo = First(payload, "customerPo", "purchaseOrder", "purchase_order", "po");
        var reference = First(payload, "poNumber", "orderReference", "order_reference");
        if (string.IsNullOrWhiteSpace(customerPo) && string.IsNullOrWhiteSpace(reference)) issues.Add(new("Warning", "MISSING_REFERENCE", "poNumber", "No PO/order reference was found."));

        var jobType = First(payload, "jobType", "job_type") ?? string.Empty;
        var collection = First(payload, "collectionSite", "collectionLocation", "sellerName");
        var delivery = First(payload, "deliverySite", "deliveryLocation", "stallNumber");
        if (string.IsNullOrWhiteSpace(collection)) issues.Add(new("Warning", "MISSING_COLLECTION_SITE", "collectionSite", "Collection location was not identified."));
        if (string.IsNullOrWhiteSpace(delivery) && !jobType.Contains("tray collection", StringComparison.OrdinalIgnoreCase)) issues.Add(new("Warning", "MISSING_DELIVERY_SITE", "deliverySite", "Delivery location was not identified."));

        var pallets = First(payload, "pallets", "palletQty", "palletQuantity");
        if (string.IsNullOrWhiteSpace(pallets))
        {
            var sourceMentionsPallets = Regex.IsMatch($"{request.Subject} {request.BodyText} {request.BodyHtml}", @"\bpallets?\b", RegexOptions.IgnoreCase)
                || (request.Attachments ?? []).Any(a => (a.Name ?? string.Empty).Contains("pallet", StringComparison.OrdinalIgnoreCase));
            issues.Add(new("Warning", sourceMentionsPallets ? "PALLET_EXTRACTION_FAILED" : "MISSING_PALLETS", "pallets", sourceMentionsPallets
                ? "The source refers to pallets but no pallet quantity reached staging."
                : "Pallet quantity is absent from the source/extraction."));
        }
        else if (!decimal.TryParse(pallets, out var qty) || qty <= 0) issues.Add(new("Critical", "INVALID_PALLETS", "pallets", $"Pallet quantity '{pallets}' is invalid."));
    }

    private static void AddEvidence(JsonObject payload, MailboxEmailIntakeEnvelope request)
    {
        payload["sourceMailbox"] = request.Mailbox;
        payload["sourceMessageId"] = request.MessageId;
        payload["sourceInternetMessageId"] = request.InternetMessageId;
        payload["sourceConversationId"] = request.ConversationId;
        payload["sourceSender"] = request.SenderAddress;
        payload["sourceSenderName"] = request.SenderName;
        payload["sourceToRecipients"] = JsonSerializer.SerializeToNode(request.ToRecipients ?? []);
        payload["sourceCcRecipients"] = JsonSerializer.SerializeToNode(request.CcRecipients ?? []);
        payload["sourceSubject"] = request.Subject;
        payload["sourceReceivedAtUtc"] = request.ReceivedAtUtc?.ToString("O");
        payload["sourceBodyFormat"] = request.BodyFormat;
        payload["sourceImportance"] = request.Importance;
        payload["sourceWebLink"] = request.WebLink;
        payload["sourceAttachmentCount"] = request.AttachmentCount ?? request.Attachments?.Count ?? 0;
        var evidence = (request.Attachments ?? []).Select(a => new { a.AttachmentId, a.Name, a.ContentType, a.SizeBytes, a.IsInline, sha256 = HashAttachment(a.ContentBase64) }).ToList();
        payload["sourceAttachments"] = JsonSerializer.SerializeToNode(evidence);

        var sourceName = First(payload, "sourceAttachmentName");
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            var attachment = (request.Attachments ?? []).FirstOrDefault(a => string.Equals(a.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            if (attachment is not null)
            {
                payload["sourceAttachmentType"] = attachment.ContentType;
                payload["sourceAttachmentReference"] = attachment.AttachmentId;
                payload["sourceAttachmentSizeBytes"] = attachment.SizeBytes;
                payload["sourceAttachmentSha256"] = HashAttachment(attachment.ContentBase64);
            }
        }
    }

    private static IReadOnlyList<string> MatchKeys(JsonObject payload, string naturalKey)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(naturalKey)) keys.Add($"NATURAL|{Normalise(naturalKey)}");
        var customer = First(payload, "customerCode", "customer") ?? "UNKNOWN";
        var po = First(payload, "customerPo", "purchaseOrder", "purchase_order", "po");
        var reference = First(payload, "poNumber", "orderReference", "order_reference");
        if (!string.IsNullOrWhiteSpace(po)) keys.Add($"PO|{Normalise(customer)}|{Normalise(po)}");
        else if (!string.IsNullOrWhiteSpace(reference) && !reference.StartsWith("EMAIL-", StringComparison.OrdinalIgnoreCase)) keys.Add($"REF|{Normalise(customer)}|{Normalise(reference)}");
        var collectionDate = First(payload, "collectionDate", "collection_date") ?? string.Empty;
        var deliveryDate = First(payload, "deliveryDate", "delivery_date") ?? string.Empty;
        var collection = First(payload, "collectionSite", "collectionLocation", "sellerName") ?? string.Empty;
        var delivery = First(payload, "deliverySite", "deliveryLocation", "stallNumber") ?? string.Empty;
        if (collectionDate.Length > 0 || deliveryDate.Length > 0) keys.Add($"ROUTE|{Normalise(customer)}|{collectionDate}|{deliveryDate}|{Normalise(collection)}|{Normalise(delivery)}");
        return keys.ToList();
    }

    private static string Fingerprint(JsonObject payload)
    {
        var names = new[] { "customerCode", "customerPo", "poNumber", "collectionDate", "deliveryDate", "pallets", "cases", "quantity", "sellerName", "stallNumber", "collectionLocation", "deliveryLocation", "requestedTime", "availableTime", "jobType", "temperature", "trailerType", "trailerNotes" };
        var canonical = string.Join('|', names.Select(name => $"{name}={Normalise(First(payload, name) ?? string.Empty)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? First(JsonObject payload, params string[] names)
    {
        foreach (var name in names)
        {
            var pair = payload.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase));
            if (pair.Key is null || pair.Value is null) continue;
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)) { if (!string.IsNullOrWhiteSpace(text)) return text.Trim(); }
            else { var raw = pair.Value.ToJsonString().Trim('"').Trim(); if (raw.Length > 0 && raw != "null") return raw; }
        }
        return null;
    }

    private static bool? Bool(JsonObject payload, string name)
    {
        var pair = payload.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase));
        if (pair.Key is null || pair.Value is null) return null;
        if (pair.Value is JsonValue value && value.TryGetValue<bool>(out var result)) return result;
        return bool.TryParse(First(payload, name), out var parsed) ? parsed : null;
    }

    private static string? HashAttachment(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var value = base64.Trim();
            var comma = value.IndexOf(',');
            if (comma >= 0 && value[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) value = value[(comma + 1)..];
            return Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(value))).ToLowerInvariant();
        }
        catch { return null; }
    }

    private static string BatchId(string messageId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId ?? string.Empty))).ToLowerInvariant();
        return $"info-{hash[..20]}";
    }
    private static string Normalise(string value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static JsonElement Element(JsonObject payload) { using var doc = JsonDocument.Parse(payload.ToJsonString()); return doc.RootElement.Clone(); }
    private sealed record ValidationIssue(string Severity, string Code, string? Field, string Message);
}
