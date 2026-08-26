using ExcelDataReader;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Handles recurring Info-mailbox order formats which are both high-volume and
/// unsafe to collapse into the generic single-order parser. These formats are
/// deliberately source-specific so a changed supplier template is sent to
/// review instead of silently creating the wrong number of jobs.
/// </summary>
public sealed class MarketsSainsburyWaitroseParser
{
    private static readonly Regex NumericDateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])[./-](?<month>0?[1-9]|1[0-2])(?:[./-](?<year>20\d{2}|\d{2}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TangmerePairRegex = new(
        @"(?<name>[A-Z][A-Z &'/-]{1,60}?)\s+(?<qty>\d{1,3})(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BarfootsWaveRegex = new(
        @"(?<depot>Aylesford|Bracknell|Brinklow|Leyland)\s+WAVE\s+(?<wave>\d+)\s+(?:from\s+(?<collection>[A-Z][A-Z0-9 &'()/-]{1,80}?)\s+)?(?<qty>\d{1,3})\s+pallets?\s+PO\s+(?<po>[A-Z0-9/-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FowlerRowRegex = new(
        @"(?m)^\s*(?<depot>AYLESFORD|BRACKNELL|BRINKLOW|LEYLAND)\s*[|\t ]+\s*(?<po>[A-Z]\d{4,})\s*[|\t ]+\s*(?<qty>\d{1,3})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static MarketsSainsburyWaitroseParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
    {
        var subject = (request.Subject ?? string.Empty).Trim();
        var body = NormaliseBody(request.BodyText, request.BodyHtml);
        var sender = (request.SenderAddress ?? string.Empty).Trim();

        if (LooksLikeWaitroseRetraction(subject, body))
        {
            return new EmailIntakeParseResult(
                [],
                ["Waitrose instruction was explicitly withdrawn by the sender."],
                "Waitrose retraction detected. Do not create a transport order from the quoted earlier instruction.");
        }

        if (sender.EndsWith("@pmtransport.co.uk", StringComparison.OrdinalIgnoreCase) &&
            subject.StartsWith("Tangmere market", StringComparison.OrdinalIgnoreCase))
            return ParseTangmere(request, subject, body);

        if (sender.EndsWith("@pmtransport.co.uk", StringComparison.OrdinalIgnoreCase) &&
            subject.Equals("Additional market", StringComparison.OrdinalIgnoreCase))
            return ParseAdditionalMarket(request, body);

        if (IsSainsburyPrePlanSubject(subject))
            return ParseSainsburyPrePlanWorkbook(request, subject);

        if (subject.StartsWith("Tamworth Transhipments", StringComparison.OrdinalIgnoreCase) &&
            sender.EndsWith("@sainsburys.co.uk", StringComparison.OrdinalIgnoreCase))
            return ParseTamworthTranshipment(request, subject, body);

        if (subject.Contains("Sainsbury", StringComparison.OrdinalIgnoreCase) &&
            sender.EndsWith("@newey.com", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("transport requirements", StringComparison.OrdinalIgnoreCase))
        {
            return new EmailIntakeParseResult(
                [],
                ["Newey Sainsbury weekly transport requirement detected. The order rows are embedded in an inline image and require review/image extraction."],
                "Newey Sainsbury weekly image template requires mapping review; do not silently ignore this transport requirement.");
        }

        if (sender.EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase) &&
            subject.Contains("Wholesale Market Pallet Bookings", StringComparison.OrdinalIgnoreCase))
            return ParseBarfootsWholesaleWorkbook(request, subject);

        if (sender.EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase) &&
            subject.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("WAVE", StringComparison.OrdinalIgnoreCase))
            return ParseBarfootsWaitroseWaves(request, subject, body);

        if (sender.EndsWith("@fowlerwelch.co.uk", StringComparison.OrdinalIgnoreCase) &&
            subject.StartsWith("Waitrose ", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("PALLET COUNT", StringComparison.OrdinalIgnoreCase))
            return ParseFowlerWaitrose(request, subject, body);

        return null;
    }

    private static bool LooksLikeWaitroseRetraction(string subject, string body)
    {
        if (!subject.Contains("WAITROSE", StringComparison.OrdinalIgnoreCase)) return false;
        var lead = body.Length <= 300 ? body : body[..300];
        return lead.Contains("please ignore this", StringComparison.OrdinalIgnoreCase)
               || lead.Contains("please disregard", StringComparison.OrdinalIgnoreCase)
               || lead.Contains("do not action", StringComparison.OrdinalIgnoreCase);
    }

    private static EmailIntakeParseResult ParseTangmere(MailboxEmailIntakeRequest request, string subject, string body)
    {
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var date = ExtractDate(subject, received.Year) ?? DateOnly.FromDateTime(received.Date);
        var rows = ParseTangmereRows(body);
        if (rows.Count == 0)
            return new EmailIntakeParseResult([], ["PM Transport Tangmere market email was recognised but no market pallet rows could be parsed."], "Tangmere market format requires review.");

        var orders = rows.Select((row, index) =>
        {
            var warnings = new List<string>
            {
                "Delivery date was not stated separately; market delivery date is provisionally set to the collection date."
            };
            return BuildOrder(
                request,
                $"pm-tangmere-{Normalise(row.Market)}-{Normalise(row.Customer)}-{index + 1}",
                $"pmtransport|{date:yyyy-MM-dd}|{Normalise(row.Market)}|{Normalise(row.Customer)}|{row.Pallets}",
                "PMTRANSPORT",
                null,
                date,
                date,
                row.Pallets,
                "Tangmere",
                row.Market,
                row.Customer,
                "Wholesale market delivery",
                null,
                null,
                warnings,
                "PM Transport Tangmere market matrix",
                plannerReady: true);
        }).ToList();

        return new EmailIntakeParseResult(orders, [], null);
    }

    internal static List<MarketRow> ParseTangmereRows(string body)
    {
        var text = Regex.Replace(body.Replace("\r", "\n"), @"[|\t]+", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var westernPos = text.IndexOf("WESTERN", StringComparison.OrdinalIgnoreCase);
        var spitalPos = text.IndexOf("SPITALFIELD", StringComparison.OrdinalIgnoreCase);
        if (westernPos < 0 || spitalPos < 0 || spitalPos <= westernPos) return [];

        var western = text[(westernPos + "WESTERN".Length)..spitalPos];
        var spital = text[(spitalPos + "SPITALFIELD".Length)..];
        var signaturePos = spital.IndexOf("Kind Regards", StringComparison.OrdinalIgnoreCase);
        if (signaturePos >= 0) spital = spital[..signaturePos];

        var rows = new List<MarketRow>();
        rows.AddRange(ParseMarketPairs(western, "Western"));
        rows.AddRange(ParseMarketPairs(spital, "Spitalfields"));
        return rows;
    }

    private static IEnumerable<MarketRow> ParseMarketPairs(string value, string market)
    {
        foreach (Match match in TangmerePairRegex.Matches(value))
        {
            var name = CleanName(match.Groups["name"].Value);
            if (name.Equals("SPITALFIELD", StringComparison.OrdinalIgnoreCase) || name.Length < 2) continue;
            if (!int.TryParse(match.Groups["qty"].Value, out var qty) || qty <= 0) continue;
            yield return new MarketRow(market, name, qty);
        }
    }

    private static EmailIntakeParseResult ParseAdditionalMarket(MailboxEmailIntakeRequest request, string body)
    {
        var match = Regex.Match(body, @"another\s+(?<qty>\d{1,3})\s*p(?:t|allets?)\s+(?<name>[A-Z0-9 &'/-]+?)\s+spit", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["qty"].Value, out var qty) || qty <= 0)
            return new EmailIntakeParseResult([], ["Additional market email was recognised but the incremental pallet quantity could not be read."], "Additional market change requires review.");

        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var date = DateOnly.FromDateTime(received.Date);
        var customer = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups["name"].Value.Trim().ToLowerInvariant());
        var readyTime = Regex.IsMatch(body, @"ready\s+(?:about\s+)?6\s*pm", RegexOptions.IgnoreCase) ? "18:00" : null;
        var warnings = new List<string>
        {
            "Collection/delivery date inferred from the email received date because the incremental market email did not repeat the date."
        };
        var order = BuildOrder(
            request,
            $"pm-additional-{Normalise(customer)}-{qty}",
            $"pmtransport|additional|{date:yyyy-MM-dd}|spitalfields|{Normalise(customer)}|{qty}",
            "PMTRANSPORT",
            null,
            date,
            date,
            qty,
            "Tangmere",
            "Spitalfields",
            customer,
            "Additional market delivery",
            readyTime,
            null,
            warnings,
            "PM Transport additional market",
            plannerReady: true);
        return new EmailIntakeParseResult([order], [], null);
    }

    private static bool IsSainsburyPrePlanSubject(string subject) =>
        (subject.StartsWith("[CrosspointPCC]", StringComparison.OrdinalIgnoreCase) ||
         subject.StartsWith("[DaventryBond]", StringComparison.OrdinalIgnoreCase)) &&
        subject.Contains("STUART LYONS", StringComparison.OrdinalIgnoreCase);

    private static EmailIntakeParseResult ParseSainsburyPrePlanWorkbook(MailboxEmailIntakeRequest request, string subject)
    {
        var attachment = FindWorkbook(request);
        if (attachment is null)
            return new EmailIntakeParseResult([], ["Sainsbury pre-plan email detected but workbook content was not supplied to the TMS."], "Sainsbury pre-plan requires attachment content for safe order creation.");

        try
        {
            var sheets = ReadWorkbook(attachment.EffectiveContentBase64!);
            var deliveryDate = ExtractDate(subject, (request.ReceivedAtUtc ?? DateTimeOffset.UtcNow).Year);
            var orders = new List<ParsedEmailOrder>();
            foreach (var sheet in sheets)
                orders.AddRange(ParseSainsburyPrePlanRows(request, sheet.Rows, deliveryDate, sheet.Name, attachment.Name));

            if (orders.Count == 0)
                return new EmailIntakeParseResult([], ["Sainsbury pre-plan workbook was read but no populated STUART LYONS rows were found."], "Sainsbury pre-plan contains no usable Stuart Lyons rows.");
            return new EmailIntakeParseResult(orders, [], null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EmailIntakeParseResult([], [$"Sainsbury pre-plan workbook could not be parsed: {ex.GetBaseException().Message}"], "Sainsbury pre-plan parsing failed; manual review is required.");
        }
    }

    internal static List<ParsedEmailOrder> ParseSainsburyPrePlanRows(
        MailboxEmailIntakeRequest request,
        IReadOnlyList<object?[]> rows,
        DateOnly? deliveryDate,
        string sheetName = "Sheet1",
        string? attachmentName = null)
    {
        var headerIndex = FindHeader(rows, ["collectiondate", "collectingdepothaulier", "collectionsite", "destination", "pallets"]);
        if (headerIndex < 0) return [];
        var map = HeaderMap(rows[headerIndex]);
        var results = new List<ParsedEmailOrder>();

        for (var index = headerIndex + 1; index < rows.Count; index++)
        {
            var row = rows[index];
            var haulier = CellText(row, Find(map, "collectingdepothaulier"));
            if (!string.Equals(haulier, "STUART LYONS", StringComparison.OrdinalIgnoreCase)) continue;
            var collectionSite = CellText(row, Find(map, "collectionsite"));
            var destination = CellText(row, Find(map, "destination"));
            var scion = CellText(row, Find(map, "scionordernumber"));
            var pallets = CellInt(row, Find(map, "pallets"));
            var collectionDay = CellDate(row, Find(map, "collectiondate"));
            if (string.IsNullOrWhiteSpace(collectionSite) || string.IsNullOrWhiteSpace(destination) || pallets is null or <= 0 || collectionDay is null) continue;
            var effectiveDeliveryDate = deliveryDate ?? collectionDay.Value;
            var collectionTime = CellTime(row, Find(map, "collectiontime"));
            var deliveryTime = CellTime(row, Find(map, "actualdeliverytime")) ?? CellTime(row, Find(map, "originaldeliverytime"));
            var warnings = new List<string>
            {
                "Sainsbury PCC/Bond pre-plan is provisional and must be reconciled against the later 14:00 backhaul plan before treating route/times as final."
            };
            var naturalKey = $"sainsbury-preplan|{Normalise(scion)}|{collectionDay:yyyy-MM-dd}|{Normalise(collectionSite)}|{Normalise(destination)}";
            results.Add(BuildOrder(
                request,
                $"sainsbury-preplan-{index + 1}-{Normalise(scion)}",
                naturalKey,
                "SAINSBURY",
                scion,
                collectionDay.Value,
                effectiveDeliveryDate,
                pallets.Value,
                collectionSite,
                "SAINSBURY",
                destination,
                "Sainsbury backhaul pre-plan",
                collectionTime,
                deliveryTime,
                warnings,
                $"Sainsbury PCC/Bond pre-plan / {sheetName}",
                plannerReady: false,
                attachmentName: attachmentName,
                rowNumber: index + 1));
        }
        return results;
    }

    private static EmailIntakeParseResult ParseTamworthTranshipment(MailboxEmailIntakeRequest request, string subject, string body)
    {
        if (!body.Contains("STUART LYONS", StringComparison.OrdinalIgnoreCase) || !body.Contains("Basingstoke", StringComparison.OrdinalIgnoreCase))
            return new EmailIntakeParseResult([], ["Tamworth transhipment email was recognised but no Stuart Lyons route row was found."], "Tamworth transhipment requires review.");
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var date = ExtractDate(subject + " " + body, received.Year) ?? DateOnly.FromDateTime(received.Date);
        var time = Regex.Match(body, @"\b(?<time>[0-2]?\d:[0-5]\d)\b").Groups["time"].Value;
        var warnings = new List<string> { "Pallet quantity was not supplied on the Tamworth transhipment email." };
        var order = BuildOrder(
            request,
            "sainsbury-tamworth-transhipment",
            $"sainsbury|tamworth|basingstoke|{date:yyyy-MM-dd}|{time}",
            "SAINSBURY",
            null,
            date,
            date,
            null,
            "Tamworth",
            "SAINSBURY",
            "Basingstoke",
            "Sainsbury transhipment",
            time,
            null,
            warnings,
            "Sainsbury Tamworth transhipment body",
            plannerReady: false);
        return new EmailIntakeParseResult([order], [], null);
    }

    private static EmailIntakeParseResult ParseBarfootsWholesaleWorkbook(MailboxEmailIntakeRequest request, string subject)
    {
        var attachment = FindWorkbook(request);
        if (attachment is null)
            return new EmailIntakeParseResult([], ["Barfoots wholesale-market email detected but workbook content was not supplied to the TMS."], "Wholesale market workbook attachment is required.");
        try
        {
            var sheets = ReadWorkbook(attachment.EffectiveContentBase64!);
            var orders = new List<ParsedEmailOrder>();
            foreach (var sheet in sheets)
                orders.AddRange(ParseBarfootsWholesaleRows(request, sheet.Rows, sheet.Name, attachment.Name));
            if (orders.Count == 0)
                return new EmailIntakeParseResult([], ["Wholesale market workbook was read but no positive pallet rows were found."], "Wholesale market workbook requires review.");
            return new EmailIntakeParseResult(orders, [], null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EmailIntakeParseResult([], [$"Wholesale market workbook could not be parsed: {ex.GetBaseException().Message}"], "Wholesale market workbook parsing failed; manual review is required.");
        }
    }

    internal static List<ParsedEmailOrder> ParseBarfootsWholesaleRows(
        MailboxEmailIntakeRequest request,
        IReadOnlyList<object?[]> rows,
        string sheetName = "Sheet1",
        string? attachmentName = null)
    {
        string? collection = null;
        Dictionary<string, int>? map = null;
        var results = new List<ParsedEmailOrder>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var first = CellText(row, 0);
            if (!string.IsNullOrWhiteSpace(first) && first.StartsWith("COLLECTION ", StringComparison.OrdinalIgnoreCase))
            {
                collection = first["COLLECTION ".Length..].Trim();
                map = null;
                continue;
            }

            var candidateMap = HeaderMap(row);
            if (candidateMap.ContainsKey("market") && candidateMap.ContainsKey("marketcustomer") && candidateMap.ContainsKey("noofpallets"))
            {
                map = candidateMap;
                continue;
            }
            if (map is null || string.IsNullOrWhiteSpace(collection)) continue;

            var pallets = CellInt(row, Find(map, "noofpallets"));
            if (pallets is null or <= 0) continue;
            var market = CellText(row, Find(map, "market"));
            var customer = CellText(row, Find(map, "marketcustomer"));
            var address = CellText(row, Find(map, "deliveryaddress"));
            var so = CellText(row, Find(map, "so"));
            var deliveryDate = CellDate(row, Find(map, "deliverydate"));
            if (string.IsNullOrWhiteSpace(market) || string.IsNullOrWhiteSpace(customer) || deliveryDate is null) continue;
            var deliveryTime = CellTime(row, Find(map, "deliverytime"));
            var warnings = new List<string>
            {
                "Collection date is inferred as the previous day because the Barfoots wholesale booking workbook supplies delivery date but not a separate collection date."
            };
            var collectionDate = deliveryDate.Value.AddDays(-1);
            var naturalKey = $"barfoots-market|{Normalise(so)}|{deliveryDate:yyyy-MM-dd}|{Normalise(market)}|{Normalise(customer)}";
            results.Add(BuildOrder(
                request,
                $"barfoots-market-{index + 1}-{Normalise(so)}",
                naturalKey,
                "BARFOOTS",
                so,
                collectionDate,
                deliveryDate.Value,
                pallets.Value,
                collection,
                market,
                customer,
                "Wholesale market delivery",
                null,
                deliveryTime,
                warnings,
                $"Barfoots wholesale booking / {sheetName}",
                plannerReady: true,
                attachmentName: attachmentName,
                rowNumber: index + 1,
                deliveryAddress: address));
        }
        return results;
    }

    private static EmailIntakeParseResult ParseBarfootsWaitroseWaves(MailboxEmailIntakeRequest request, string subject, string body)
    {
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var deliveryDate = ExtractDate(subject, received.Year);
        if (deliveryDate is null)
            return new EmailIntakeParseResult([], ["Barfoots Waitrose wave email did not contain a depot date."], "Waitrose wave booking requires review.");

        var matches = BarfootsWaveRegex.Matches(body).Cast<Match>().ToList();
        if (matches.Count == 0)
            return new EmailIntakeParseResult([], ["Barfoots Waitrose wave email was recognised but no wave rows could be read."], "Waitrose wave booking requires review.");

        string? lastCollection = null;
        var orders = new List<ParsedEmailOrder>();
        foreach (var match in matches)
        {
            if (match.Groups["collection"].Success) lastCollection = CleanName(match.Groups["collection"].Value);
            if (string.IsNullOrWhiteSpace(lastCollection)) continue;
            var depot = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups["depot"].Value.ToLowerInvariant());
            var wave = int.Parse(match.Groups["wave"].Value, CultureInfo.InvariantCulture);
            var qty = int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture);
            var po = match.Groups["po"].Value.Trim().ToUpperInvariant();
            var warnings = new List<string> { "Collection date inferred as the email received date from the Barfoots Waitrose wave template." };
            var collectionDate = DateOnly.FromDateTime(received.Date);
            orders.Add(BuildOrder(
                request,
                $"barfoots-waitrose-{Normalise(depot)}-w{wave}-{Normalise(po)}",
                $"waitrose|{po}|{deliveryDate:yyyy-MM-dd}|{Normalise(depot)}|wave{wave}",
                "WAITROSE",
                po,
                collectionDate,
                deliveryDate.Value,
                qty,
                lastCollection,
                "WAITROSE",
                depot,
                $"Waitrose Wave {wave}",
                null,
                null,
                warnings,
                "Barfoots Waitrose wave body",
                plannerReady: true));
        }
        return orders.Count > 0
            ? new EmailIntakeParseResult(orders, [], null)
            : new EmailIntakeParseResult([], ["Waitrose wave rows were present but collection site could not be resolved."], "Waitrose wave booking requires review.");
    }

    private static EmailIntakeParseResult ParseFowlerWaitrose(MailboxEmailIntakeRequest request, string subject, string body)
    {
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var deliveryDate = ExtractDate(subject + " " + body, received.Year);
        if (deliveryDate is null) return new EmailIntakeParseResult([], ["Fowler Welch Waitrose table had no readable delivery date."], "Waitrose depot table requires review.");
        var orders = new List<ParsedEmailOrder>();
        foreach (Match match in FowlerRowRegex.Matches(body.Replace("|", " ")))
        {
            var depot = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups["depot"].Value.ToLowerInvariant());
            var po = match.Groups["po"].Value.ToUpperInvariant();
            if (!int.TryParse(match.Groups["qty"].Value, out var qty) || qty <= 0) continue;
            var collectionDate = DateOnly.FromDateTime(received.Date);
            var warnings = new List<string> { "Collection site is mapped to Fowler Welch Hilsea from the verified sender/domain; collection time was not supplied." };
            orders.Add(BuildOrder(
                request,
                $"fowler-waitrose-{Normalise(depot)}-{Normalise(po)}",
                $"waitrose|{po}|{deliveryDate:yyyy-MM-dd}|{Normalise(depot)}",
                "WAITROSE",
                po,
                collectionDate,
                deliveryDate.Value,
                qty,
                "Fowler Welch Hilsea",
                "WAITROSE",
                depot,
                "Waitrose depot delivery",
                null,
                null,
                warnings,
                "Fowler Welch Waitrose depot table",
                plannerReady: true));
        }
        return orders.Count > 0
            ? new EmailIntakeParseResult(orders, [], null)
            : new EmailIntakeParseResult([], ["Fowler Welch Waitrose email was recognised but depot rows were not parsed."], "Waitrose depot table requires review.");
    }

    private static ParsedEmailOrder BuildOrder(
        MailboxEmailIntakeRequest request,
        string sourceKey,
        string naturalKey,
        string customerCode,
        string? customerPo,
        DateOnly collectionDate,
        DateOnly deliveryDate,
        int? pallets,
        string collectionSite,
        string marketName,
        string destination,
        string jobType,
        string? collectionTime,
        string? deliveryTime,
        IReadOnlyList<string> warnings,
        string parser,
        bool plannerReady,
        string? attachmentName = null,
        int? rowNumber = null,
        string? deliveryAddress = null)
    {
        var baseReference = customerPo ?? StableEmailReference(request.MessageId);
        var reference = BuildReference(baseReference, destination, sourceKey);
        var confidence = warnings.Count == 0 ? "High" : "Medium";
        var instructions = string.Join(" · ", new[]
        {
            $"Order type: {jobType}",
            string.IsNullOrWhiteSpace(customerPo) ? null : $"Source ref: {customerPo}",
            string.IsNullOrWhiteSpace(collectionTime) ? null : $"Collection time: {collectionTime}",
            string.IsNullOrWhiteSpace(deliveryTime) ? null : $"Delivery time: {deliveryTime}",
            string.IsNullOrWhiteSpace(deliveryAddress) ? null : $"Delivery address: {deliveryAddress}",
            $"Source email: {request.Subject}",
            string.IsNullOrWhiteSpace(attachmentName) ? null : $"Source attachment: {attachmentName}",
            $"Parser: {parser}",
            warnings.Count == 0 ? null : $"Intake warning: {string.Join("; ", warnings)}"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = reference,
            ["customerPo"] = customerPo,
            ["customerCode"] = customerCode,
            ["collectionDate"] = collectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = pallets,
            ["sellerName"] = collectionSite,
            ["marketName"] = marketName,
            ["stallNumber"] = destination,
            ["deliveryAddress"] = deliveryAddress,
            ["requestedTime"] = collectionTime,
            ["deliveryRequestedTime"] = deliveryTime,
            ["jobType"] = jobType,
            ["driverInstructions"] = instructions.Length <= 1000 ? instructions : instructions[..1000],
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["sourceAttachmentName"] = attachmentName,
            ["sourceRow"] = rowNumber,
            ["intakeNaturalKey"] = naturalKey,
            ["intakeConfidence"] = confidence,
            ["intakeWarnings"] = warnings,
            ["intakeParser"] = parser,
            ["plannerReady"] = plannerReady,
            ["intakeStatus"] = plannerReady ? "Ready" : "ReviewRequired"
        };
        return new ParsedEmailOrder(sourceKey, naturalKey, JsonSerializer.SerializeToElement(payload), warnings);
    }

    private static MailboxAttachmentRequest? FindWorkbook(MailboxEmailIntakeRequest request) =>
        (request.Attachments ?? []).FirstOrDefault(item =>
            item.IsInline != true &&
            !string.IsNullOrWhiteSpace(item.EffectiveContentBase64) &&
            (Path.GetExtension(item.Name ?? string.Empty).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(item.Name ?? string.Empty).Equals(".xls", StringComparison.OrdinalIgnoreCase)));

    private static List<WorkbookSheet> ReadWorkbook(string base64)
    {
        var bytes = DecodeBase64(base64);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var sheets = new List<WorkbookSheet>();
        do
        {
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++) values[index] = reader.GetValue(index);
                rows.Add(values);
            }
            sheets.Add(new WorkbookSheet(reader.Name ?? "Sheet", rows));
        }
        while (reader.NextResult());
        return sheets;
    }

    private static int FindHeader(IReadOnlyList<object?[]> rows, IReadOnlyCollection<string> required)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            var map = HeaderMap(rows[index]);
            if (required.All(map.ContainsKey)) return index;
        }
        return -1;
    }

    private static Dictionary<string, int> HeaderMap(object?[] row)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < row.Length; index++)
        {
            var key = Normalise(CellText(row[index]));
            if (key.Length > 0 && !map.ContainsKey(key)) map[key] = index;
        }
        return map;
    }

    private static int Find(Dictionary<string, int> map, string key) => map.TryGetValue(key, out var index) ? index : -1;
    private static string? CellText(object?[] row, int index) => index < 0 || index >= row.Length ? null : CellText(row[index]);
    private static string? CellText(object? value) => value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();

    private static int? CellInt(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is double d) return Convert.ToInt32(Math.Round(d, MidpointRounding.AwayFromZero));
        if (row[index] is int i) return i;
        return int.TryParse(CellText(row[index]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static DateOnly? CellDate(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is DateTime dt) return DateOnly.FromDateTime(dt);
        if (row[index] is double serial && serial > 1 && serial < 100000) return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        return DateTime.TryParse(CellText(row[index]), CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AssumeLocal, out var parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;
    }

    private static string? CellTime(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is DateTime dt) return dt.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (row[index] is double serial)
        {
            var time = TimeSpan.FromDays(serial - Math.Floor(serial));
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}";
        }
        var text = CellText(row[index]);
        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed) ? $"{(int)parsed.TotalHours:00}:{parsed.Minutes:00}" : text;
    }

    private static DateOnly? ExtractDate(string value, int defaultYear)
    {
        var match = NumericDateRegex.Match(value);
        if (!match.Success) return null;
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        var yearText = match.Groups["year"].Value;
        var year = string.IsNullOrWhiteSpace(yearText) ? defaultYear : yearText.Length == 2 ? 2000 + int.Parse(yearText, CultureInfo.InvariantCulture) : int.Parse(yearText, CultureInfo.InvariantCulture);
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string NormaliseBody(string? text, string? html)
    {
        if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var withoutTags = Regex.Replace(html, @"<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(withoutTags, @"[ \t]+", " ")).Trim();
    }

    private static string CleanName(string value) => Regex.Replace(value, @"\s+", " ").Trim(' ', '-', '–', '—', '|');
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string BuildReference(string source, string destination, string sourceKey)
    {
        var cleanSource = SafeToken(source, 32);
        var cleanDestination = SafeToken(destination, 28);
        var suffix = SafeToken(sourceKey, 16);
        var value = $"{cleanSource}/{cleanDestination}/{suffix}";
        return value[..Math.Min(80, value.Length)];
    }

    private static string StableEmailReference(string messageId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        return $"EMAIL-{Convert.ToHexString(bytes)[..12]}";
    }

    private static string SafeToken(string value, int max)
    {
        var clean = Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9/-]+", "-").Trim('-', '/');
        if (clean.Length == 0) clean = "ORDER";
        return clean[..Math.Min(clean.Length, max)];
    }

    private static byte[] DecodeBase64(string value)
    {
        var trimmed = value.Trim();
        var comma = trimmed.IndexOf(',');
        if (comma >= 0 && trimmed[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[(comma + 1)..];
        return Convert.FromBase64String(trimmed);
    }

    internal sealed record MarketRow(string Market, string Customer, int Pallets);
    private sealed record WorkbookSheet(string Name, List<object?[]> Rows);
}
