using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Parsers for stable customer body formats observed in the SLH Info mailbox.
/// Keep these small and format-specific; unknown formats fall through to the
/// generic parser and ultimately the Pending Review exception path.
/// </summary>
public sealed class KnownCustomerMailboxOrderParser
{
    private static readonly Regex HhpCollection = new(
        @"Please\s+collect\s+(?<pallets>\d{1,3})\s+pallets?\s+from\s+(?<site>.+?)\s+today\s+[^\d\r\n]*(?<date>\d{1,2}[./-]\d{1,2}[./-]\d{2,4})",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HhpDelivery = new(
        @"For\s+Delivery\s+date\s+[^\d\r\n]*(?<date>\d{1,2}[./-]\d{1,2}[./-]\d{2,4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HhpPo = new(@"\bPO\s+number\s*:\s*(?<po>[A-Z0-9/-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HhpCases = new(@"\b(?<cases>\d{1,6})\s+cases?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HhpDestination = new(@"(?m)^\s*(?:[-*]\s*)?(?<site>[A-Za-z][A-Za-z0-9 '&/-]{2,80}?)\s+(?<pallets>\d{1,3})\s+pallets?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WaitroseDate = new(@"DELIVERY\s+DATE[^\d]*(?<date>\d{1,2}\s*[./-]\s*\d{1,2}\s*[./-]\s*\d{2,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WaitrosePipeRow = new(@"(?m)^\s*(?<depot>[A-Z][A-Z0-9 '&/-]{2,80})\s*\|\s*(?<po>[A-Z0-9/-]{3,40})\s*\|\s*(?<pallets>\d{1,3})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
    {
        var subject = (request.Subject ?? string.Empty).Trim();
        var body = NormaliseBody(request.BodyText, request.BodyHtml);

        if (subject.Contains("HHP WAITROSE DIRECT DEPOT DELIVERY", StringComparison.OrdinalIgnoreCase))
            return ParseHhpWaitrose(request, body);

        if (subject.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("PO NUMBER", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("PALLET COUNT", StringComparison.OrdinalIgnoreCase))
            return ParseWaitrosePalletTable(request, body);

        return null;
    }

    private static EmailIntakeParseResult ParseHhpWaitrose(MailboxEmailIntakeRequest request, string body)
    {
        var collectionMatch = HhpCollection.Match(body);
        var deliveryMatch = HhpDelivery.Match(body);
        var poMatch = HhpPo.Match(body);
        var casesMatch = HhpCases.Match(body);
        var destinationMatch = HhpDestination.Matches(body).Cast<Match>()
            .FirstOrDefault(match => !match.Groups["site"].Value.Contains("collect", StringComparison.OrdinalIgnoreCase));

        var warnings = new List<string>();
        var collectionDate = collectionMatch.Success ? ParseDate(collectionMatch.Groups["date"].Value) : null;
        var deliveryDate = deliveryMatch.Success ? ParseDate(deliveryMatch.Groups["date"].Value) : null;
        var pallets = collectionMatch.Success && int.TryParse(collectionMatch.Groups["pallets"].Value, out var palletCount) ? palletCount : (int?)null;
        var collection = collectionMatch.Success ? Clean(collectionMatch.Groups["site"].Value) : null;
        var destination = destinationMatch?.Success == true ? Clean(destinationMatch.Groups["site"].Value) : null;
        var po = poMatch.Success ? poMatch.Groups["po"].Value.Trim().ToUpperInvariant() : null;
        var cases = casesMatch.Success && int.TryParse(casesMatch.Groups["cases"].Value, out var caseCount) ? caseCount : (int?)null;

        if (collectionDate is null) warnings.Add("HHP collection date could not be extracted.");
        if (deliveryDate is null) warnings.Add("HHP delivery date could not be extracted.");
        if (pallets is null) warnings.Add("HHP pallet quantity could not be extracted.");
        if (string.IsNullOrWhiteSpace(collection)) warnings.Add("HHP collection site could not be extracted.");
        if (string.IsNullOrWhiteSpace(destination)) warnings.Add("HHP Waitrose depot could not be extracted.");
        if (string.IsNullOrWhiteSpace(po)) warnings.Add("HHP PO number could not be extracted.");

        var workingDate = collectionDate ?? deliveryDate ?? DateOnly.FromDateTime((request.ReceivedAtUtc ?? DateTimeOffset.UtcNow).Date);
        var technicalReference = po ?? StableEmailReference(request.MessageId);
        var naturalKey = $"WAITROSE|HHP|{Normalise(po ?? technicalReference)}";
        var payload = new Dictionary<string, object?>
        {
            ["customer_supplier"] = "HHP / Waitrose",
            ["customer"] = "Waitrose",
            ["supplier"] = "Hall Hunter",
            ["customerCode"] = "WAITROSE",
            ["jobType"] = "Delivery",
            ["po"] = po,
            ["purchase_order"] = po,
            ["poNumber"] = technicalReference,
            ["orderReference"] = po,
            ["customerPo"] = po,
            ["collectionDate"] = collectionDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["collectionLocation"] = collection,
            ["collectionSite"] = collection,
            ["sellerName"] = collection,
            ["deliveryDate"] = deliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryLocation"] = destination,
            ["deliverySite"] = destination,
            ["stallNumber"] = destination,
            ["pallets"] = pallets,
            ["cases"] = cases,
            ["product"] = cases is null ? null : "Berries",
            ["planningDate"] = deliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? workingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["intakeNaturalKey"] = naturalKey,
            ["intakeMatchKeys"] = po is null ? new[] { $"ROUTE|WAITROSE|{workingDate:yyyy-MM-dd}|{Normalise(destination ?? string.Empty)}" } : new[] { $"PO|WAITROSE|{Normalise(po)}" },
            ["intakeConfidence"] = warnings.Count == 0 ? "High" : "Medium",
            ["intakeWarnings"] = warnings,
            ["intakeParser"] = "HHP Waitrose direct depot v1",
            ["mappingTemplate"] = "HHP / Waitrose"
        };

        return new EmailIntakeParseResult(
            [new ParsedEmailOrder("hhp-waitrose-1", naturalKey, JsonSerializer.SerializeToElement(payload), warnings)],
            [],
            null);
    }

    private static EmailIntakeParseResult ParseWaitrosePalletTable(MailboxEmailIntakeRequest request, string body)
    {
        var dateMatch = WaitroseDate.Match(body);
        var deliveryDate = dateMatch.Success ? ParseDate(dateMatch.Groups["date"].Value.Replace(" ", string.Empty, StringComparison.Ordinal)) : null;
        var rows = WaitrosePipeRow.Matches(body).Cast<Match>().ToList();
        if (rows.Count == 0)
            return new EmailIntakeParseResult([], ["Waitrose pallet-count email was recognised but no DEPOT / PO NUMBER / PALLET COUNT rows could be extracted."], "Recognised Waitrose order table requires review.");

        var orders = new List<ParsedEmailOrder>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var depot = Clean(row.Groups["depot"].Value);
            var po = row.Groups["po"].Value.Trim().ToUpperInvariant();
            var pallets = int.Parse(row.Groups["pallets"].Value, CultureInfo.InvariantCulture);
            var warnings = new List<string>();
            if (deliveryDate is null) warnings.Add("Waitrose delivery date could not be extracted from the table header.");
            warnings.Add("Collection date/site are not stated in this pallet-count email and must be confirmed or supplied by an existing customer rule before approval.");

            var naturalKey = $"WAITROSE|PO|{Normalise(po)}";
            var payload = new Dictionary<string, object?>
            {
                ["customer_supplier"] = "Fowler Welch / Waitrose",
                ["customer"] = "Waitrose",
                ["supplier"] = "Fowler Welch",
                ["customerCode"] = "WAITROSE",
                ["jobType"] = "Delivery",
                ["po"] = po,
                ["purchase_order"] = po,
                ["poNumber"] = po,
                ["orderReference"] = po,
                ["customerPo"] = po,
                ["collectionDate"] = null,
                ["deliveryDate"] = deliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["deliveryLocation"] = depot,
                ["deliverySite"] = depot,
                ["stallNumber"] = depot,
                ["pallets"] = pallets,
                ["planningDate"] = deliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["sourceMessageId"] = request.MessageId,
                ["sourceInternetMessageId"] = request.InternetMessageId,
                ["sourceSender"] = request.SenderAddress,
                ["sourceSenderName"] = request.SenderName,
                ["sourceSubject"] = request.Subject,
                ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
                ["sourceWebLink"] = request.WebLink,
                ["sourceRow"] = index + 1,
                ["intakeNaturalKey"] = naturalKey,
                ["intakeMatchKeys"] = new[] { $"PO|WAITROSE|{Normalise(po)}" },
                ["intakeConfidence"] = "Medium",
                ["intakeWarnings"] = warnings,
                ["intakeParser"] = "Waitrose pallet count body v1",
                ["mappingTemplate"] = "Fowler Welch / Waitrose pallet counts"
            };
            orders.Add(new ParsedEmailOrder($"waitrose-body-{index + 1}", naturalKey, JsonSerializer.SerializeToElement(payload), warnings));
        }

        return new EmailIntakeParseResult(orders, [], null);
    }

    private static string NormaliseBody(string? bodyText, string? bodyHtml)
    {
        var input = !string.IsNullOrWhiteSpace(bodyText) ? bodyText! : bodyHtml ?? string.Empty;
        input = Regex.Replace(input, @"(?i)<br\s*/?>|</p>|</div>|</tr>|</li>", "\n");
        input = Regex.Replace(input, @"<[^>]+>", " ");
        input = WebUtility.HtmlDecode(input).Replace("**", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        input = Regex.Replace(input, @"[ \t]+", " ");
        input = Regex.Replace(input, @"\r?\n[ \t]*", "\n");
        return input.Trim();
    }

    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().Replace('.', '/').Replace('-', '/');
        foreach (var format in new[] { "d/M/yyyy", "dd/MM/yyyy", "d/M/yy", "dd/MM/yy", "M/d/yyyy", "MM/dd/yyyy" })
            if (DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        return null;
    }

    private static string Clean(string value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim(' ', '-', '*', '_');
    private static string Normalise(string value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string StableEmailReference(string messageId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(messageId ?? string.Empty));
        return $"EMAIL-{Convert.ToHexString(bytes)[..12]}";
    }
}
