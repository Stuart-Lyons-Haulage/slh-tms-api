using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Handles high-value Info mailbox formats whose structure cannot be represented
/// safely by the generic single-date body parser. Returning null delegates to
/// EmailOrderIntakeService.
/// </summary>
public sealed class SpecialistMailboxOrderParser
{
    private static readonly Regex CancellationRegex = new(
        @"\b(cancelled|canceled|cancellation)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BookingReferenceRegex = new(
        @"\bBooking\s+Ref(?:erence)?\s*:\s*(?<ref>[A-Z0-9/-]{5,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GenericPalletRegex = new(
        @"\b(?<qty>\d{1,3})\s+pallets?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumericDateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])[./-](?<month>0?[1-9]|1[0-2])[./-](?<year>20\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NamedDateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])(?:st|nd|rd|th)?\s+(?<month>January|February|March|April|May|June|July|August|September|October|November|December)(?:\s+(?<year>20\d{2}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CollectionTimeRegex = new(
        @"\bCollection\s*:[^\r\n]*?\bfrom\s+(?<time>[0-2]?\d:[0-5]\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TransferSubjectRegex = new(
        @"\bCollection\s+from\s+(?<from>.+?)\s+to\s+(?<to>.+?)\s+(?<date>\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?)(?:\s*,\s*(?<ref>\d{5,}))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CoventDropRegex = new(
        @"^(?<name>[^\r\n-][^\r\n]{1,100}?)\s*-\s*(?<qty>\d{1,3})\s+pallets?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
    {
        var subject = (request.Subject ?? string.Empty).Trim();
        var body = NormaliseBody(request.BodyText, request.BodyHtml);
        var combined = $"{subject}\n{body}";

        if (CancellationRegex.IsMatch(combined))
        {
            return new EmailIntakeParseResult(
                [],
                ["Cancellation email detected. It was deliberately not created as a new transport order."],
                "Cancellation/amendment detected. Review against the existing order rather than creating a duplicate.");
        }

        if (subject.Contains("Covent Garden", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("APS Produce", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCoventGarden(request, subject, body);
        }

        if (subject.Contains("Amazon", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("APS Produce", StringComparison.OrdinalIgnoreCase))
        {
            return ParseAmazon(request, subject, body);
        }

        var transfer = TransferSubjectRegex.Match(subject);
        if (transfer.Success)
            return ParseTransfer(request, transfer, body);

        return null;
    }

    private static EmailIntakeParseResult ParseAmazon(
        MailboxEmailIntakeRequest request,
        string subject,
        string body)
    {
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var bookingRef = Match(BookingReferenceRegex, body, "ref") ?? StableEmailReference(request.MessageId);
        var collectionDate = DateAfterKeyword(body, "Collection", received.Year)
            ?? EarliestDate(body, received.Year);
        var deliveryDate = FirstNumericDate(body)
            ?? DateAfterKeyword(body, "Delivery", received.Year)
            ?? LatestDate($"{subject}\n{body}", received.Year)
            ?? collectionDate;
        var availableTime = Match(CollectionTimeRegex, body, "time");
        var pallets = FirstPalletQuantity(body);
        var collectionSite = FirstLineAfterHeader(body, "Collection") ?? "APS Produce";
        if (collectionSite.Contains("Tuesday", StringComparison.OrdinalIgnoreCase) ||
            collectionSite.Contains("Wednesday", StringComparison.OrdinalIgnoreCase))
            collectionSite = "APS Produce";
        var destination = FirstLineAfterHeader(body, "Delivery") ?? "Amazon delivery";
        var warnings = new List<string>();
        if (collectionDate is null) warnings.Add("Collection date was not identified.");
        if (deliveryDate is null) warnings.Add("Delivery date was not identified.");
        if (pallets is null) warnings.Add("Pallet quantity was not identified.");
        if (destination.Equals("Amazon delivery", StringComparison.OrdinalIgnoreCase)) warnings.Add("Amazon destination requires confirmation.");

        var workingDate = collectionDate ?? deliveryDate ?? DateOnly.FromDateTime(received.Date);
        var reference = BuildReference(bookingRef, destination);
        var naturalKey = NaturalKey(request, "AMAZON", destination, workingDate, bookingRef);
        var payload = BasePayload(
            request,
            reference,
            bookingRef,
            "AMAZON",
            collectionDate ?? workingDate,
            deliveryDate ?? workingDate,
            pallets,
            collectionSite,
            destination,
            "Delivery",
            availableTime,
            warnings,
            "APS/Amazon body email");

        return new EmailIntakeParseResult(
            [new ParsedEmailOrder("amazon-body-1", naturalKey, payload, warnings)],
            [],
            null);
    }

    private static EmailIntakeParseResult ParseCoventGarden(
        MailboxEmailIntakeRequest request,
        string subject,
        string body)
    {
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var dates = AllDates($"{subject}\n{body}", received.Year).Distinct().OrderBy(date => date).ToList();
        var collectionDate = DateAfterKeyword(body, "Collection", received.Year)
            ?? dates.FirstOrDefault();
        if (collectionDate == default) collectionDate = DateOnly.FromDateTime(received.Date);
        var deliveryDate = dates.Count > 1 ? dates.Last() : collectionDate;
        var availableTime = Match(CollectionTimeRegex, body, "time");
        var drops = CoventDropRegex.Matches(body)
            .Cast<Match>()
            .Select(match => new
            {
                Name = CleanDropName(match.Groups["name"].Value),
                Pallets = int.TryParse(match.Groups["qty"].Value, out var qty) ? qty : 0
            })
            .Where(drop => drop.Pallets > 0 && !string.IsNullOrWhiteSpace(drop.Name))
            .ToList();

        if (drops.Count == 0)
        {
            return new EmailIntakeParseResult(
                [],
                ["Covent Garden email detected but no individual pallet lines could be parsed."],
                "Covent Garden format needs manual review because no delivery rows were identified.");
        }

        var baseReference = StableEmailReference(request.MessageId);
        var orders = new List<ParsedEmailOrder>();
        for (var index = 0; index < drops.Count; index++)
        {
            var drop = drops[index];
            var warnings = new List<string>
            {
                "Delivery instruction spans the evening/overnight period; exact delivery time was not stated in the email."
            };
            var reference = BuildReference(baseReference, drop.Name);
            var naturalKey = NaturalKey(request, "COVENTGARDEN", drop.Name, collectionDate, null);
            var payload = BasePayload(
                request,
                reference,
                null,
                "COVENTGARDEN",
                collectionDate,
                deliveryDate,
                drop.Pallets,
                "APS Produce",
                drop.Name,
                "Market delivery",
                availableTime,
                warnings,
                "APS/Covent Garden multi-drop body email");
            orders.Add(new ParsedEmailOrder($"covent-drop-{index + 1}", naturalKey, payload, warnings));
        }

        return new EmailIntakeParseResult(orders, [], null);
    }

    private static EmailIntakeParseResult ParseTransfer(
        MailboxEmailIntakeRequest request,
        Match transfer,
        string body)
    {
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var collection = transfer.Groups["from"].Value.Trim();
        var destination = transfer.Groups["to"].Value.Trim();
        var date = ParseFlexibleNumericDate(transfer.Groups["date"].Value, received.Year)
            ?? DateOnly.FromDateTime(received.Date);
        var transportRef = transfer.Groups["ref"].Success
            ? transfer.Groups["ref"].Value.Trim()
            : StableEmailReference(request.MessageId);
        var pallets = FirstPalletQuantity(body);
        var combined = $"{request.Subject}\n{body}";
        var customer = combined.Contains("IFCO", StringComparison.OrdinalIgnoreCase) ? "IFCO" : SenderCustomer(request.SenderAddress);
        var warnings = new List<string>();
        if (pallets is null) warnings.Add("Pallet quantity was not identified.");
        var reference = BuildReference(transportRef, destination);
        var naturalKey = NaturalKey(request, customer, destination, date, transportRef);
        var payload = BasePayload(
            request,
            reference,
            transportRef,
            customer,
            date,
            date,
            pallets,
            collection,
            destination,
            "Collection transfer",
            null,
            warnings,
            "Route stated in email subject");

        return new EmailIntakeParseResult(
            [new ParsedEmailOrder("transfer-body-1", naturalKey, payload, warnings)],
            [],
            null);
    }

    private static JsonElement BasePayload(
        MailboxEmailIntakeRequest request,
        string orderReference,
        string? customerPo,
        string customer,
        DateOnly collectionDate,
        DateOnly deliveryDate,
        int? pallets,
        string collection,
        string destination,
        string jobType,
        string? availableTime,
        IReadOnlyList<string> warnings,
        string parser)
    {
        var instructions = string.Join(" · ", new[]
        {
            $"Order type: {jobType}",
            string.IsNullOrWhiteSpace(customerPo) ? null : $"PO ref: {customerPo}",
            string.IsNullOrWhiteSpace(availableTime) ? null : $"Available time: {availableTime}",
            $"Source email: {request.Subject}",
            $"Parser: {parser}",
            warnings.Count == 0 ? null : $"Intake warning: {string.Join("; ", warnings)}"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = orderReference,
            ["customerPo"] = customerPo,
            ["customerCode"] = customer,
            ["collectionDate"] = collectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = pallets,
            ["sellerName"] = collection,
            ["marketName"] = customer,
            ["stallNumber"] = destination,
            ["jobType"] = jobType,
            ["availableTime"] = availableTime,
            ["driverInstructions"] = instructions.Length <= 1000 ? instructions : instructions[..1000],
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["intakeNaturalKey"] = NaturalKey(request, customer, destination, collectionDate, customerPo),
            ["intakeConfidence"] = warnings.Count == 0 ? "High" : "Medium",
            ["intakeWarnings"] = warnings,
            ["intakeParser"] = parser
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    private static string NormaliseBody(string? bodyText, string? bodyHtml)
    {
        var input = !string.IsNullOrWhiteSpace(bodyText) ? bodyText! : bodyHtml ?? string.Empty;
        input = Regex.Replace(input, @"(?i)<br\s*/?>|</p>|</div>|</tr>|</li>", "\n");
        input = Regex.Replace(input, @"<[^>]+>", " ");
        input = WebUtility.HtmlDecode(input);
        input = input.Replace("**", string.Empty, StringComparison.Ordinal);
        input = Regex.Replace(input, @"[ \t]+", " ");
        input = Regex.Replace(input, @"\r?\n[ \t]*", "\n");
        return input.Trim();
    }

    private static string? FirstLineAfterHeader(string body, string keyword)
    {
        var lines = body.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            for (var next = index + 1; next < lines.Count; next++)
            {
                var candidate = lines[next].Trim(' ', '*');
                if (candidate.Length == 0) continue;
                return candidate;
            }
        }
        return null;
    }

    private static DateOnly? DateAfterKeyword(string body, string keyword, int defaultYear)
    {
        var index = body.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var slice = body[index..Math.Min(body.Length, index + 180)];
        return FirstNumericDate(slice) ?? FirstNamedDate(slice, defaultYear);
    }

    private static DateOnly? FirstNumericDate(string value)
    {
        var match = NumericDateRegex.Match(value);
        if (!match.Success) return null;
        return SafeDate(match.Groups["day"].Value, match.Groups["month"].Value, match.Groups["year"].Value);
    }

    private static DateOnly? FirstNamedDate(string value, int defaultYear)
    {
        var match = NamedDateRegex.Match(value);
        return match.Success ? NamedMatchToDate(match, defaultYear) : null;
    }

    private static DateOnly? EarliestDate(string value, int defaultYear) =>
        AllDates(value, defaultYear).OrderBy(date => date).Cast<DateOnly?>().FirstOrDefault();

    private static DateOnly? LatestDate(string value, int defaultYear) =>
        AllDates(value, defaultYear).OrderByDescending(date => date).Cast<DateOnly?>().FirstOrDefault();

    private static IEnumerable<DateOnly> AllDates(string value, int defaultYear)
    {
        foreach (Match match in NumericDateRegex.Matches(value))
        {
            var date = SafeDate(match.Groups["day"].Value, match.Groups["month"].Value, match.Groups["year"].Value);
            if (date is not null) yield return date.Value;
        }
        foreach (Match match in NamedDateRegex.Matches(value))
        {
            var date = NamedMatchToDate(match, defaultYear);
            if (date is not null) yield return date.Value;
        }
    }

    private static DateOnly? NamedMatchToDate(Match match, int defaultYear)
    {
        if (!int.TryParse(match.Groups["day"].Value, out var day)) return null;
        if (!DateTime.TryParseExact(match.Groups["month"].Value, "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var monthDate)) return null;
        var year = int.TryParse(match.Groups["year"].Value, out var explicitYear) ? explicitYear : defaultYear;
        try { return new DateOnly(year, monthDate.Month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateOnly? SafeDate(string dayText, string monthText, string yearText)
    {
        if (!int.TryParse(dayText, out var day) || !int.TryParse(monthText, out var month) || !int.TryParse(yearText, out var year)) return null;
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateOnly? ParseFlexibleNumericDate(string value, int defaultYear)
    {
        var parts = value.Split('.', '/', '-');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var day) || !int.TryParse(parts[1], out var month)) return null;
        var year = defaultYear;
        if (parts.Length > 2 && int.TryParse(parts[2], out var parsedYear)) year = parsedYear < 100 ? 2000 + parsedYear : parsedYear;
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static int? FirstPalletQuantity(string body)
    {
        var match = GenericPalletRegex.Match(body);
        return match.Success && int.TryParse(match.Groups["qty"].Value, out var value) ? value : null;
    }

    private static string? Match(Regex regex, string input, string group)
    {
        var match = regex.Match(input);
        return match.Success ? match.Groups[group].Value.Trim() : null;
    }

    private static string CleanDropName(string value) =>
        Regex.Replace(value.Trim(' ', '*'), @"\s+", " ");

    private static string BuildReference(string baseReference, string destination)
    {
        var left = SafeToken(baseReference, 38);
        var right = SafeToken(destination, 36);
        var result = $"{left}/{right}";
        return result[..Math.Min(80, result.Length)];
    }

    private static string StableEmailReference(string messageId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        return $"EMAIL-{Convert.ToHexString(bytes)[..12]}";
    }

    private static string SafeToken(string value, int max)
    {
        var clean = Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9/-]+", "-").Trim('-');
        if (clean.Length == 0) clean = "ORDER";
        return clean[..Math.Min(max, clean.Length)];
    }

    private static string NaturalKey(
        MailboxEmailIntakeRequest request,
        string customer,
        string destination,
        DateOnly collectionDate,
        string? customerPo)
    {
        var subject = Regex.Replace(request.Subject ?? string.Empty, @"^(?:(?:RE|FW|FWD)\s*:\s*)+", string.Empty, RegexOptions.IgnoreCase).Trim().ToUpperInvariant();
        return string.Join("|", new[]
        {
            (request.SenderAddress ?? string.Empty).Trim().ToLowerInvariant(),
            subject,
            collectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            customer.ToUpperInvariant(),
            destination.Trim().ToUpperInvariant(),
            (customerPo ?? string.Empty).Trim().ToUpperInvariant()
        });
    }

    private static string SenderCustomer(string? sender)
    {
        var domain = (sender ?? string.Empty).Split('@').LastOrDefault() ?? "EMAIL";
        var stem = domain.Split('.').FirstOrDefault() ?? "EMAIL";
        var clean = new string(stem.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "EMAIL" : clean[..Math.Min(40, clean.Length)];
    }
}
