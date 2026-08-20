using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Adds source evidence, deterministic matching keys, master-data checks and
/// structured validation to every Info-mailbox order before it is staged.
/// The original extracted values are preserved; canonical matches are added as
/// separate fields so review never loses what the customer actually supplied.
/// </summary>
public sealed class MailboxOrderIntakeEnrichmentService(TmsDbContext db)
{
    public async Task<ParsedEmailOrder> EnrichAsync(
        MailboxEmailIntakeRequest request,
        ParsedEmailOrder order,
        CancellationToken ct)
    {
        var payload = JsonNode.Parse(order.Payload.GetRawText()) as JsonObject ?? new JsonObject();
        var issues = new List<ValidationIssue>();
        foreach (var warning in order.Warnings.Where(value => !string.IsNullOrWhiteSpace(value)))
            issues.Add(new("Warning", "PARSER_WARNING", null, warning));

        var customers = await SafeCustomers(ct);
        var sites = await SafeSites(ct);

        var customerCode = Text(payload, "customerCode") ?? Text(payload, "customer");
        if (string.IsNullOrWhiteSpace(customerCode))
        {
            issues.Add(new("Critical", "MISSING_CUSTOMER", "customerCode", "Customer could not be identified from the source order."));
        }
        else
        {
            var customer = MatchCustomer(customers, customerCode);
            if (customer is null)
            {
                issues.Add(new("Warning", "UNKNOWN_CUSTOMER", "customerCode", $"Customer '{customerCode}' is not an exact match in TMS Master Data."));
            }
            else
            {
                payload["masterCustomerId"] = customer.Id.ToString();
                payload["masterCustomerCode"] = customer.Code;
                payload["masterCustomerName"] = customer.Name;
                payload["customerCode"] = customer.Code;
            }
        }

        var collection = FirstText(payload, "collectionSite", "collectionLocation", "sellerName");
        var delivery = FirstText(payload, "deliverySite", "deliveryLocation", "stallNumber");
        AddSiteMatch(payload, issues, sites, collection, true);
        AddSiteMatch(payload, issues, sites, delivery, false);

        ValidateDates(payload, issues);
        ValidatePallets(request, payload, issues);
        ValidateRequiredLocations(payload, issues);

        var customerPo = FirstText(payload, "customerPo", "purchaseOrder", "purchase_order", "po");
        var orderReference = FirstText(payload, "poNumber", "orderReference", "order_reference", "customerReference");
        if (string.IsNullOrWhiteSpace(customerPo) && string.IsNullOrWhiteSpace(orderReference))
            issues.Add(new("Warning", "MISSING_REFERENCE", "poNumber", "No customer PO or order reference was found. Planner confirmation is required."));

        AddSourceEvidence(request, payload);
        payload["importSource"] = "PowerAutomate/InfoMailbox";
        payload["importBatchId"] = BatchId(request.MessageId);
        payload["importedAt"] = DateTimeOffset.UtcNow.ToString("O");
        payload["reviewStatus"] = "Pending Review";
        payload["mappingTemplate"] = FirstText(payload, "intakeParser", "mappingTemplate") ?? "Generic mailbox intake";

        var existingReady = Bool(payload, "plannerReady");
        var hasCritical = issues.Any(issue => issue.Severity == "Critical");
        payload["plannerReady"] = existingReady == false ? false : !hasCritical;
        if (Text(payload, "intakeStatus") is null)
            payload["intakeStatus"] = hasCritical ? "Exception" : "PendingReview";
        payload["validationStatus"] = hasCritical ? "Critical" : issues.Count > 0 ? "Warning" : "Valid";
        payload["validationIssues"] = JsonSerializer.SerializeToNode(issues);
        payload["extractionConfidence"] = FirstText(payload, "intakeConfidence", "extractionConfidence") ?? (hasCritical ? "Low" : issues.Count > 0 ? "Medium" : "High");

        var matchKeys = BuildMatchKeys(payload, order.NaturalKey);
        payload["intakeMatchKeys"] = JsonSerializer.SerializeToNode(matchKeys);
        payload["businessFingerprint"] = BusinessFingerprint(payload);

        var warnings = issues.Where(issue => issue.Severity != "Information").Select(issue => issue.Message).Distinct().ToList();
        payload["intakeWarnings"] = JsonSerializer.SerializeToNode(warnings);
        return new ParsedEmailOrder(order.SourceKey, order.NaturalKey, ToElement(payload), warnings);
    }

    public ParsedEmailOrder BuildReviewException(
        MailboxEmailIntakeRequest request,
        string reason,
        IReadOnlyList<string> parserWarnings)
    {
        var warnings = parserWarnings.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (!warnings.Contains(reason, StringComparer.OrdinalIgnoreCase)) warnings.Add(reason);
        var payload = new JsonObject
        {
            ["sourceMailbox"] = request.Mailbox,
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceConversationId"] = request.ConversationId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc?.ToString("O"),
            ["sourceWebLink"] = request.WebLink,
            ["sourceBodyFormat"] = request.BodyFormat,
            ["sourceImportance"] = request.Importance,
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
        AddSourceEvidence(request, payload);
        payload["validationIssues"] = JsonSerializer.SerializeToNode(new[]
        {
            new ValidationIssue("Critical", "EXTRACTION_REVIEW_REQUIRED", null, reason)
        });
        payload["intakeWarnings"] = JsonSerializer.SerializeToNode(warnings);
        payload["intakeMatchKeys"] = JsonSerializer.SerializeToNode(new[] { $"MESSAGE|{Normalise(request.MessageId)}" });
        payload["businessFingerprint"] = BusinessFingerprint(payload);
        return new ParsedEmailOrder("review-exception", $"MESSAGE|{Normalise(request.MessageId)}", ToElement(payload), warnings);
    }

    public bool ShouldRetainForReview(MailboxEmailIntakeRequest request, string? ignoredReason)
    {
        if (string.IsNullOrWhiteSpace(ignoredReason)) return false;
        if (ignoredReason.Contains("Internal Lyons email", StringComparison.OrdinalIgnoreCase) ||
            ignoredReason.Contains("Operational request", StringComparison.OrdinalIgnoreCase) ||
            ignoredReason.Contains("Cancellation", StringComparison.OrdinalIgnoreCase))
            return false;

        var source = $"{request.Subject}\n{request.BodyText}\n{request.BodyHtml}";
        if (Regex.IsMatch(source, @"\b(order|purchase order|\bpo\b|pallet|collection|delivery|booking|manifest|load plan|transport)\b", RegexOptions.IgnoreCase))
            return true;

        return (request.Attachments ?? []).Any(attachment =>
            attachment.IsInline != true &&
            Path.GetExtension(attachment.Name ?? string.Empty).ToLowerInvariant() is ".xls" or ".xlsx" or ".xlsm" or ".csv" or ".pdf");
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

    private static Customer? MatchCustomer(IEnumerable<Customer> customers, string value)
    {
        var key = Normalise(value);
        return customers.FirstOrDefault(customer => Normalise(customer.Code) == key || Normalise(customer.Name) == key);
    }

    private static Site? MatchSite(IEnumerable<Site> sites, string value)
    {
        var key = NormaliseSite(value);
        return sites.FirstOrDefault(site => new[] { site.ExternalCode, site.Name, site.DriverTextName }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Any(candidate => NormaliseSite(candidate!) == key));
    }

    private static void AddSiteMatch(JsonObject payload, List<ValidationIssue> issues, IReadOnlyList<Site> sites, string? value, bool collection)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var match = MatchSite(sites, value);
        var prefix = collection ? "collection" : "delivery";
        if (match is null)
        {
            issues.Add(new("Warning", collection ? "UNKNOWN_COLLECTION_SITE" : "UNKNOWN_DELIVERY_SITE", prefix + "Site", $"{(collection ? "Collection" : "Delivery")} site '{value}' is not an exact TMS Master Data match."));
            return;
        }
        payload[prefix + "SiteId"] = match.Id.ToString();
        payload[prefix + "SiteMatchedName"] = match.Name;
        payload[prefix + "SiteOriginal"] = value;
    }

    private static void ValidateDates(JsonObject payload, List<ValidationIssue> issues)
    {
        var collection = FirstText(payload, "collectionDate", "collection_date");
        var delivery = FirstText(payload, "deliveryDate", "delivery_date");
        if (string.IsNullOrWhiteSpace(collection))
            issues.Add(new("Critical", "MISSING_COLLECTION_DATE", "collectionDate", "Collection date is missing and must be confirmed before approval."));
        else if (!DateOnly.TryParse(collection, out _))
            issues.Add(new("Critical", "INVALID_COLLECTION_DATE", "collectionDate", $"Collection date '{collection}' is not valid."));

        if (string.IsNullOrWhiteSpace(delivery))
            issues.Add(new("Warning", "MISSING_DELIVERY_DATE", "deliveryDate", "Delivery date is missing and should be checked."));
        else if (!DateOnly.TryParse(delivery, out _))
            issues.Add(new("Critical", "INVALID_DELIVERY_DATE", "deliveryDate", $"Delivery date '{delivery}' is not valid."));
        else if (DateOnly.TryParse(collection, out var collectionDate) && DateOnly.TryParse(delivery, out var deliveryDate) && deliveryDate < collectionDate)
            issues.Add(new("Warning", "DATE_SEQUENCE", "deliveryDate", "Delivery date is earlier than collection date."));
    }

    private static void ValidatePallets(MailboxEmailIntakeRequest request, JsonObject payload, List<ValidationIssue> issues)
    {
        var pallets = FirstText(payload, "pallets", "palletQty", "palletQuantity");
        if (string.IsNullOrWhiteSpace(pallets))
        {
            var sourceMentionsPallets = Regex.IsMatch($"{request.Subject} {request.BodyText} {request.BodyHtml}", @"\bpallets?\b", RegexOptions.IgnoreCase)
                || (request.Attachments ?? []).Any(a => (a.Name ?? string.Empty).Contains("pallet", StringComparison.OrdinalIgnoreCase));
            issues.Add(new("Warning", sourceMentionsPallets ? "PALLET_EXTRACTION_FAILED" : "MISSING_PALLETS", "pallets",
                sourceMentionsPallets ? "The source refers to pallets but no pallet quantity reached the staging payload." : "Pallet quantity is absent from the source/extraction."));
            return;
        }
        if (!decimal.TryParse(pallets, out var value))
            issues.Add(new("Critical", "INVALID_PALLETS", "pallets", $"Pallet quantity '{pallets}' is invalid."));
        else if (value <= 0)
            issues.Add(new("Critical", "INVALID_PALLETS", "pallets", "Pallet quantity must be greater than zero for a transport order."));
    }

    private static void ValidateRequiredLocations(JsonObject payload, List<ValidationIssue> issues)
    {
        var jobType = FirstText(payload, "jobType", "job_type") ?? string.Empty;
        var collection = FirstText(payload, "collectionSite", "collectionLocation", "sellerName");
        var delivery = FirstText(payload, "deliverySite", "deliveryLocation", "stallNumber");
        if (string.IsNullOrWhiteSpace(collection))
            issues.Add(new("Warning", "MISSING_COLLECTION_SITE", "collectionSite", "Collection location was not identified."));
        if (string.IsNullOrWhiteSpace(delivery) && !jobType.Contains("tray collection", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("Warning", "MISSING_DELIVERY_SITE", "deliverySite", "Delivery location was not identified."));
    }

    private static void AddSourceEvidence(MailboxEmailIntakeRequest request, JsonObject payload)
    {
        payload["sourceMailbox"] = request.Mailbox;
        payload["sourceMessageId"] = request.MessageId;
        payload["sourceInternetMessageId"] = request.InternetMessageId;
        payload["sourceConversationId"] = request.ConversationId;
        payload["sourceSender"] = request.SenderAddress;
        payload["sourceSenderName"] = request.SenderName;
        payload["sourceSubject"] = request.Subject;
        payload["sourceReceivedAtUtc"] = request.ReceivedAtUtc?.ToString("O");
        payload["sourceWebLink"] = request.WebLink;
        payload["sourceBodyFormat"] = request.BodyFormat;
        payload["sourceImportance"] = request.Importance;
        payload["sourceToRecipients"] = JsonSerializer.SerializeToNode(request.ToRecipients ?? []);
        payload["sourceCcRecipients"] = JsonSerializer.SerializeToNode(request.CcRecipients ?? []);
        payload["sourceAttachmentCount"] = request.AttachmentCount ?? request.Attachments?.Count ?? 0;

        var evidence = (request.Attachments ?? []).Select(attachment => new
        {
            name = attachment.Name,
            contentType = attachment.ContentType,
            attachmentId = attachment.AttachmentId,
            sizeBytes = attachment.SizeBytes,
            isInline = attachment.IsInline,
            sha256 = AttachmentHash(attachment.ContentBase64)
        }).ToList();
        payload["sourceAttachments"] = JsonSerializer.SerializeToNode(evidence);

        var sourceAttachmentName = Text(payload, "sourceAttachmentName");
        if (!string.IsNullOrWhiteSpace(sourceAttachmentName))
        {
            var attachment = (request.Attachments ?? []).FirstOrDefault(item => string.Equals(item.Name, sourceAttachmentName, StringComparison.OrdinalIgnoreCase));
            if (attachment is not null)
            {
                payload["sourceAttachmentType"] = attachment.ContentType;
                payload["sourceAttachmentReference"] = attachment.AttachmentId;
                payload["sourceAttachmentSizeBytes"] = attachment.SizeBytes;
                payload["sourceAttachmentSha256"] = AttachmentHash(attachment.ContentBase64);
            }
        }
    }

    private static IReadOnlyList<string> BuildMatchKeys(JsonObject payload, string naturalKey)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(naturalKey)) keys.Add($"NATURAL|{Normalise(naturalKey)}");
        var customer = FirstText(payload, "customerCode", "customer") ?? "UNKNOWN";
        var customerPo = FirstText(payload, "customerPo", "purchaseOrder", "purchase_order", "po");
        var reference = FirstText(payload, "poNumber", "orderReference", "order_reference");
        if (!string.IsNullOrWhiteSpace(customerPo)) keys.Add($"PO|{Normalise(customer)}|{Normalise(customerPo)}");
        else if (!string.IsNullOrWhiteSpace(reference) && !reference.StartsWith("EMAIL-", StringComparison.OrdinalIgnoreCase))
            keys.Add($"REF|{Normalise(customer)}|{Normalise(reference)}");

        var collectionDate = FirstText(payload, "collectionDate", "collection_date") ?? string.Empty;
        var deliveryDate = FirstText(payload, "deliveryDate", "delivery_date") ?? string.Empty;
        var collection = FirstText(payload, "collectionSite", "collectionLocation", "sellerName") ?? string.Empty;
        var delivery = FirstText(payload, "deliverySite", "deliveryLocation", "stallNumber") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(collectionDate) || !string.IsNullOrWhiteSpace(deliveryDate))
            keys.Add($"ROUTE|{Normalise(customer)}|{collectionDate}|{deliveryDate}|{NormaliseSite(collection)}|{NormaliseSite(delivery)}");
        return keys.ToList();
    }

    private static string BusinessFingerprint(JsonObject payload)
    {
        var fields = new[]
        {
            "customerCode", "customerPo", "poNumber", "collectionDate", "deliveryDate", "pallets", "cases", "quantity",
            "sellerName", "stallNumber", "collectionLocation", "deliveryLocation", "requestedTime", "availableTime", "jobType",
            "temperature", "temperatureRequirement", "trailerType", "trailerNotes"
        };
        var canonical = string.Join('|', fields.Select(field => $"{field}={Normalise(FirstText(payload, field) ?? string.Empty)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? AttachmentHash(string? base64)
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

    private static string? FirstText(JsonObject payload, params string[] names)
    {
        foreach (var name in names)
            if (Text(payload, name) is { Length: > 0 } value) return value;
        return null;
    }

    private static string? Text(JsonObject payload, string name)
    {
        var pair = payload.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase));
        if (pair.Key is null || pair.Value is null) return null;
        return pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
            ? text?.Trim()
            : pair.Value.ToJsonString().Trim('"').Trim();
    }

    private static bool? Bool(JsonObject payload, string name)
    {
        var pair = payload.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase));
        if (pair.Key is null || pair.Value is null) return null;
        if (pair.Value is JsonValue value && value.TryGetValue<bool>(out var result)) return result;
        return bool.TryParse(Text(payload, name), out var parsed) ? parsed : null;
    }

    private static string Normalise(string value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NormaliseSite(string value) => Normalise(value)
        .Replace("COOP", "COOP", StringComparison.OrdinalIgnoreCase)
        .Replace("MORRISONSS", "MORRISONS", StringComparison.OrdinalIgnoreCase);

    private static JsonElement ToElement(JsonObject payload)
    {
        using var document = JsonDocument.Parse(payload.ToJsonString());
        return document.RootElement.Clone();
    }

    private sealed record ValidationIssue(string Severity, string Code, string? Field, string Message);
}
