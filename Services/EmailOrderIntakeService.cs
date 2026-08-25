using ExcelDataReader;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

public sealed class EmailOrderIntakeService
{
    private static readonly Regex DateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])[./-](?<month>0?[1-9]|1[0-2])(?:[./-](?<year>20\d{2}|\d{2}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExplicitPoRegex = new(
        @"\b(?:PORD[A-Z0-9/-]*|THE[A-Z0-9/-]+)\b|(?:\b(?:PO|Purchase\s+Order)\b\s*[:#-]\s*(?<po>[A-Z0-9][A-Z0-9/-]{2,}))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TotalPalletsRegex = new(
        @"\bTotal\s+Pallets?\s*[:=-]?\s*(?<qty>\d+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PalletQuantityRegex = new(
        @"\b(?<qty>\d{1,3})\s+pallets?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CollectionTimeRegex = new(
        @"\bCollection\s+time\s*[:=-]?\s*(?<time>[0-2]?\d[:.]\d{2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TemperatureRegex = new(
        @"\bTransport\s+at\s*(?<temp>[+-]?\d+(?:\.\d+)?)\s*(?:degrees?|°)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CollectFromRegex = new(
        @"\bCollect\s+from\s*[:=-]?\s*(?<site>[^\r\n]{2,120})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HtmlRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ReFwRegex = new(@"^(?:(?:RE|FW|FWD)\s*:\s*)+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly IReadOnlyList<KnownIntakeSignal> KnownSignals =
    [
        new("SAINSBURY", "Sainsbury", ["SAINSBURY", "SAINSBURYS", "SAINSBURY'S"]),
        new("WAITROSE", "Waitrose", ["WAITROSE", "WEIGHTROSE"]),
        new("NWF", "Natures Way", ["NATURES WAY", "NATURE'S WAY", "NWF", "NWAY"]),
        new("BARFOOTS", "Barfoots", ["BARFOOTS", "BARFOOT"]),
        new("MORRISONS", "Morrisons", ["MORRISONS", "MORRISON'S"]),
        new("ALDI", "Aldi", ["ALDI"]),
        new("COOP", "COOP", ["COOP", "CO-OP", "CO OP"]),
        new("OCADO", "Ocado", ["OCADO"])
    ];

    static EmailOrderIntakeService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public EmailIntakeParseResult Parse(MailboxEmailIntakeRequest request, IReadOnlyCollection<string>? masterSiteNames = null)
    {
        var subject = (request.Subject ?? string.Empty).Trim();
        var sender = (request.SenderAddress ?? string.Empty).Trim();
        var body = NormaliseBody(request.BodyText, request.BodyHtml);
        var attachmentNames = string.Join("\n", (request.Attachments ?? [])
            .Where(attachment => attachment.IsInline != true)
            .Select(attachment => attachment.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)));
        var sourceText = $"{subject}\n{body}\n{attachmentNames}";

        if (string.IsNullOrWhiteSpace(request.MessageId))
            return EmailIntakeParseResult.Ignored("MessageId is required for idempotent mailbox intake.");

        if (LooksTmsLoopback(subject, sender, body))
            return EmailIntakeParseResult.Ignored("Internal TMS intake/test notification ignored so system outputs cannot loop back into Orders.");

        if (LooksOperationalOnly(subject, body))
            return EmailIntakeParseResult.Ignored("Operational request detected; it was not converted into a transport order automatically.");

        var receivedAt = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var sourceDate = ExtractDate(sourceText, receivedAt);
        var rawPo = ExtractPo(sourceText);
        var globalWarnings = new List<string>();
        var orders = new List<ParsedEmailOrder>();

        foreach (var attachment in request.Attachments ?? [])
        {
            if (attachment.IsInline == true || string.IsNullOrWhiteSpace(attachment.EffectiveContentBase64))
                continue;

            var extension = Path.GetExtension(attachment.Name ?? string.Empty).ToLowerInvariant();
            if (extension is not (".xls" or ".xlsx" or ".xlsm"))
                continue;

            try
            {
                orders.AddRange(ParseWorkbook(
                    request,
                    attachment,
                    sourceDate,
                    rawPo,
                    body));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                globalWarnings.Add($"Attachment '{attachment.Name}' could not be parsed: {ex.GetBaseException().Message}");
            }
        }

        if (orders.Count == 0)
        {
            var bodyOrder = ParseBodyOrder(request, sourceDate, rawPo, body, sourceText, masterSiteNames ?? [], globalWarnings);
            if (bodyOrder is not null)
                orders.Add(bodyOrder);
        }

        if (orders.Count == 0)
            return new EmailIntakeParseResult([], globalWarnings, "No transport order could be identified from this email.");

        return new EmailIntakeParseResult(orders, globalWarnings, null);
    }

    private static IEnumerable<ParsedEmailOrder> ParseWorkbook(
        MailboxEmailIntakeRequest request,
        MailboxAttachmentRequest attachment,
        DateOnly? emailDate,
        string? rawPo,
        string body)
    {
        var bytes = DecodeBase64(attachment.EffectiveContentBase64!);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        var results = new List<ParsedEmailOrder>();
        var sheetIndex = 0;
        do
        {
            sheetIndex++;
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.GetValue(i);
                rows.Add(values);
            }

            var headerIndex = rows.FindIndex(IsBookingHeader);
            if (headerIndex < 0)
                continue;

            var header = rows[headerIndex];
            var columns = HeaderMap(header);
            var collectionIndex = FindColumn(columns, "collectionsite", "collection", "collectfrom");
            var dateIndex = FindColumn(columns, "date", "deliverydate", "bookingdate");
            var depotIndex = FindColumn(columns, "depotdescription", "depot", "destination", "deliverysite");
            var palletsIndex = FindColumn(columns, "pallets", "pallet", "qty", "quantity");
            var requestTimeIndex = FindColumn(columns, "requesttime", "requestedtime", "bookingtime", "deliverytime");
            var availableTimeIndex = FindColumn(columns, "availabletime", "collectiontime", "readytime");

            if (depotIndex < 0 || palletsIndex < 0)
                continue;

            for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var depot = CellText(row, depotIndex);
                var pallets = CellInt(row, palletsIndex);
                if (string.IsNullOrWhiteSpace(depot) || pallets is null or <= 0)
                    continue;

                var collection = CellText(row, collectionIndex);
                var rowDate = CellDate(row, dateIndex) ?? emailDate;
                if (rowDate is null)
                    continue;

                var requestedTime = CellTime(row, requestTimeIndex);
                var availableTime = CellTime(row, availableTimeIndex);
                var customer = InferCustomerCode(request.Subject, request.SenderAddress, depot);
                var destination = CleanDestination(depot, customer);
                var warnings = new List<string>();
                if (string.IsNullOrWhiteSpace(rawPo))
                    warnings.Add("No customer PO/reference was found in the email; a stable email reference was generated.");
                if (string.IsNullOrWhiteSpace(collection))
                    warnings.Add("Collection site was blank in the source workbook.");

                var baseReference = rawPo ?? StableEmailReference(request.MessageId);
                var orderReference = BuildRowReference(baseReference, customer, destination, rowDate.Value, rowIndex + 1);
                var naturalKey = NaturalKey(request, customer, destination, rowDate.Value);
                var instructions = BuildInstructions(
                    rawPo,
                    requestedTime,
                    availableTime,
                    ExtractTemperature(body),
                    request,
                    attachment.Name,
                    warnings,
                    "Delivery");

                var payload = new Dictionary<string, object?>
                {
                    ["poNumber"] = orderReference,
                    ["customerCode"] = customer,
                    ["collectionDate"] = rowDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["deliveryDate"] = rowDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["pallets"] = pallets.Value,
                    ["sellerName"] = collection,
                    ["marketName"] = customer,
                    ["stallNumber"] = destination,
                    ["driverInstructions"] = instructions,
                    ["customerPo"] = rawPo,
                    ["requestedTime"] = requestedTime,
                    ["availableTime"] = availableTime,
                    ["jobType"] = "Delivery",
                    ["sourceMessageId"] = request.MessageId,
                    ["sourceInternetMessageId"] = request.InternetMessageId,
                    ["sourceSender"] = request.SenderAddress,
                    ["sourceSenderName"] = request.SenderName,
                    ["sourceSubject"] = request.Subject,
                    ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
                    ["sourceWebLink"] = request.WebLink,
                    ["sourceAttachmentName"] = attachment.Name,
                    ["sourceSheet"] = reader.Name,
                    ["sourceRow"] = rowIndex + 1,
                    ["intakeNaturalKey"] = naturalKey,
                    ["intakeConfidence"] = warnings.Count == 0 ? "High" : "Medium",
                    ["intakeWarnings"] = warnings
                };

                results.Add(new ParsedEmailOrder(
                    $"sheet-{sheetIndex}-row-{rowIndex + 1}",
                    naturalKey,
                    JsonSerializer.SerializeToElement(payload),
                    warnings));
            }
        }
        while (reader.NextResult());

        return results;
    }

    private static ParsedEmailOrder? ParseBodyOrder(
        MailboxEmailIntakeRequest request,
        DateOnly? sourceDate,
        string? rawPo,
        string body,
        string sourceText,
        IReadOnlyCollection<string> masterSiteNames,
        List<string> globalWarnings)
    {
        if (sourceDate is null)
        {
            globalWarnings.Add("No planning date could be read from the email subject/body.");
            return null;
        }

        var signal = DetectKnownSignal(sourceText, masterSiteNames);
        var customer = signal?.CustomerCode ?? InferCustomerCode(request.Subject, request.SenderAddress, sourceText);
        var jobType = InferJobType(request.Subject, body);
        var collection = InferCollectionSite(request.Subject, body, jobType);
        var destination = InferDestination(request.Subject, jobType) ?? signal?.SiteName;
        var pallets = ExtractInt(TotalPalletsRegex, body, "qty") ?? ExtractInt(PalletQuantityRegex, sourceText, "qty");
        var requestedTime = ExtractMatch(CollectionTimeRegex, body, "time")?.Replace('.', ':');
        if (!HasEnoughBodyOrderEvidence(rawPo, collection, destination, pallets, requestedTime, jobType, signal is not null))
        {
            globalWarnings.Add("Email body contained a date but not enough order detail to stage a transport order.");
            return null;
        }
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(rawPo))
            warnings.Add("No customer PO/reference was found; a stable email reference was generated and should be checked before approval.");
        if (string.IsNullOrWhiteSpace(collection))
            warnings.Add("Collection site was not explicit in the email.");
        if (string.IsNullOrWhiteSpace(destination))
            warnings.Add("Delivery/return destination was not explicit in the email.");
        if (pallets is null && !jobType.Contains("Tray", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Pallet quantity was not explicit in the email.");

        var baseReference = rawPo ?? StableEmailReference(request.MessageId);
        var orderReference = BuildRowReference(baseReference, customer, destination ?? collection ?? jobType, sourceDate.Value, 1);
        var naturalKey = NaturalKey(request, customer, destination ?? collection ?? jobType, sourceDate.Value);
        var instructions = BuildInstructions(
            rawPo,
            requestedTime,
            null,
            ExtractTemperature(body),
            request,
            null,
            warnings,
            jobType);

        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = orderReference,
            ["customerCode"] = customer,
            ["collectionDate"] = sourceDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = sourceDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = pallets,
            ["sellerName"] = collection,
            ["marketName"] = customer,
            ["stallNumber"] = destination,
            ["driverInstructions"] = instructions,
            ["customerPo"] = rawPo,
            ["requestedTime"] = requestedTime,
            ["jobType"] = jobType,
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["intakeNaturalKey"] = naturalKey,
            ["intakeConfidence"] = warnings.Count == 0 ? "High" : warnings.Count <= 2 ? "Medium" : "Low",
            ["intakeWarnings"] = warnings
        };

        return new ParsedEmailOrder("body-1", naturalKey, JsonSerializer.SerializeToElement(payload), warnings);
    }

    private static bool HasEnoughBodyOrderEvidence(string? rawPo, string? collection, string? destination, int? pallets, string? requestedTime, string jobType, bool recognisedCustomerOrSite)
    {
        var hasReference = !string.IsNullOrWhiteSpace(rawPo);
        var hasCollection = !string.IsNullOrWhiteSpace(collection);
        var hasDestination = !string.IsNullOrWhiteSpace(destination);
        var hasQuantity = pallets is > 0;
        var hasTime = !string.IsNullOrWhiteSpace(requestedTime);
        if (jobType.Contains("Tray", StringComparison.OrdinalIgnoreCase))
            return hasReference && (hasCollection || hasDestination);
        return hasQuantity && (hasReference || hasCollection || hasDestination || hasTime || recognisedCustomerOrSite);
    }

    private static bool IsBookingHeader(object?[] row)
    {
        var keys = row.Select(value => NormaliseKey(CellText(value))).Where(value => value.Length > 0).ToHashSet();
        return keys.Contains("pallets") &&
               (keys.Contains("depotdescription") || keys.Contains("destination") || keys.Contains("depot"));
    }

    private static Dictionary<string, int> HeaderMap(object?[] row)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < row.Length; index++)
        {
            var key = NormaliseKey(CellText(row[index]));
            if (!string.IsNullOrWhiteSpace(key) && !result.ContainsKey(key))
                result[key] = index;
        }
        return result;
    }

    private static int FindColumn(Dictionary<string, int> columns, params string[] names)
    {
        foreach (var name in names)
            if (columns.TryGetValue(name, out var index))
                return index;
        return -1;
    }

    private static string? CellText(object?[] row, int index) =>
        index < 0 || index >= row.Length ? null : CellText(row[index]);

    private static string? CellText(object? value)
    {
        if (value is null || value is DBNull) return null;
        return value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } text ? text : null
        };
    }

    private static int? CellInt(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is int intValue) return intValue;
        if (row[index] is double doubleValue) return (int)Math.Round(doubleValue, MidpointRounding.AwayFromZero);
        if (decimal.TryParse(CellText(row[index]), NumberStyles.Any, CultureInfo.InvariantCulture, out var decimalValue))
            return (int)Math.Round(decimalValue, MidpointRounding.AwayFromZero);
        return null;
    }

    private static DateOnly? CellDate(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is DateTime dateTime) return DateOnly.FromDateTime(dateTime);
        if (row[index] is double serial && serial > 1 && serial < 100000)
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        return ParseDateText(CellText(row[index]), DateTimeOffset.UtcNow);
    }

    private static string? CellTime(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is DateTime dateTime) return dateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (row[index] is TimeSpan span) return $"{(int)span.TotalHours:00}:{span.Minutes:00}";
        if (row[index] is double serial && serial >= 0 && serial < 1)
        {
            var spanFromSerial = TimeSpan.FromDays(serial);
            return $"{spanFromSerial.Hours:00}:{spanFromSerial.Minutes:00}";
        }
        var text = CellText(row[index]);
        if (TimeSpan.TryParse(text?.Replace('.', ':'), CultureInfo.InvariantCulture, out var parsed))
            return $"{(int)parsed.TotalHours:00}:{parsed.Minutes:00}";
        return text;
    }

    private static DateOnly? ExtractDate(string input, DateTimeOffset receivedAt)
    {
        var match = DateRegex.Match(input ?? string.Empty);
        if (!match.Success) return null;
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        var yearText = match.Groups["year"].Value;
        var year = string.IsNullOrWhiteSpace(yearText)
            ? receivedAt.Year
            : yearText.Length == 2
                ? 2000 + int.Parse(yearText, CultureInfo.InvariantCulture)
                : int.Parse(yearText, CultureInfo.InvariantCulture);
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateOnly? ParseDateText(string? input, DateTimeOffset receivedAt) =>
        string.IsNullOrWhiteSpace(input) ? null : ExtractDate(input, receivedAt);

    private static string? ExtractPo(string input)
    {
        var match = ExplicitPoRegex.Match(input ?? string.Empty);
        if (!match.Success) return null;
        var value = match.Groups["po"].Success ? match.Groups["po"].Value : match.Value;
        return Regex.Replace(value.Trim(), @"\s+", string.Empty).ToUpperInvariant();
    }

    private static string? ExtractTemperature(string body) => ExtractMatch(TemperatureRegex, body, "temp") is { } temp ? $"{temp}°C" : null;
    private static int? ExtractInt(Regex regex, string input, string group) => int.TryParse(ExtractMatch(regex, input, group), out var value) ? value : null;
    private static string? ExtractMatch(Regex regex, string input, string group) => regex.Match(input ?? string.Empty) is { Success: true } match ? match.Groups[group].Value.Trim() : null;

    private static string InferCustomerCode(string? subject, string? senderAddress, string? depot)
    {
        var source = $"{subject} {depot}".ToUpperInvariant();
        foreach (var brand in new[] { "MORRISONS", "ALDI", "WAITROSE", "WEIGHTROSE", "COOP", "CO-OP", "OCADO", "SAINSBURYS", "SAINSBURY", "NATURES WAY", "NATURE'S WAY", "NWF", "NWAY", "BARFOOTS" })
        {
            if (source.Contains(brand, StringComparison.OrdinalIgnoreCase))
                return brand
                    .Replace("CO-OP", "COOP", StringComparison.OrdinalIgnoreCase)
                    .Replace("WEIGHTROSE", "WAITROSE", StringComparison.OrdinalIgnoreCase)
                    .Replace("SAINSBURYS", "SAINSBURY", StringComparison.OrdinalIgnoreCase)
                    .Replace("NATURE'S WAY", "NWF", StringComparison.OrdinalIgnoreCase)
                    .Replace("NATURES WAY", "NWF", StringComparison.OrdinalIgnoreCase)
                    .Replace("NWAY", "NWF", StringComparison.OrdinalIgnoreCase)
                    .Replace("BARFOOTS", "BARFOOTS", StringComparison.OrdinalIgnoreCase);
        }
        if ((senderAddress ?? string.Empty).EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase)) return "NWF";
        if ((senderAddress ?? string.Empty).EndsWith("@summerberry.co.uk", StringComparison.OrdinalIgnoreCase)) return "TSBC";
        var domain = (senderAddress ?? string.Empty).Split('@').LastOrDefault();
        var stem = domain?.Split('.').FirstOrDefault();
        var clean = new string((stem ?? "EMAIL").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "EMAIL" : clean[..Math.Min(clean.Length, 40)];
    }

    private static string CleanDestination(string depot, string customer)
    {
        var value = depot.Trim();
        if (value.StartsWith(customer, StringComparison.OrdinalIgnoreCase))
            value = value[customer.Length..].Trim(' ', '-', '–', '—');
        return string.IsNullOrWhiteSpace(value) ? depot.Trim() : value;
    }

    private static string InferJobType(string? subject, string body)
    {
        var value = $"{subject} {body}";
        if (value.Contains("tray collection", StringComparison.OrdinalIgnoreCase)) return "Tray collection";
        if (value.Contains("collection", StringComparison.OrdinalIgnoreCase) && !value.Contains("delivery", StringComparison.OrdinalIgnoreCase)) return "Collection";
        return "Delivery";
    }

    private static string? InferCollectionSite(string? subject, string body, string jobType)
    {
        var explicitSite = ExtractMatch(CollectFromRegex, body, "site");
        if (!string.IsNullOrWhiteSpace(explicitSite)) return CleanSourceLine(explicitSite);
        if (jobType == "Tray collection")
        {
            var match = Regex.Match(subject ?? string.Empty, @"Tray\s+collection\s+(?<site>.+?)(?:\s+\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?)?$", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups["site"].Value.Trim(' ', '-', '–', '—');
        }
        return null;
    }

    private static string? InferDestination(string? subject, string jobType)
    {
        var clean = ReFwRegex.Replace(subject ?? string.Empty, string.Empty).Trim();
        if (jobType == "Tray collection") return null;
        if (clean.Contains("COOP", StringComparison.OrdinalIgnoreCase) || clean.Contains("CO-OP", StringComparison.OrdinalIgnoreCase)) return "COOP";
        var delivery = Regex.Match(clean, @"^(?:\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?\s*)?(?<dest>.+?)\s+delivery$", RegexOptions.IgnoreCase);
        return delivery.Success ? delivery.Groups["dest"].Value.Trim() : null;
    }

    private static KnownIntakeSignal? DetectKnownSignal(string sourceText, IReadOnlyCollection<string> masterSiteNames)
    {
        foreach (var site in masterSiteNames.Where(site => !string.IsNullOrWhiteSpace(site)).OrderByDescending(site => site.Length))
        {
            var cleanSite = CleanSourceLine(site);
            if (cleanSite.Length < 3) continue;
            if (sourceText.Contains(cleanSite, StringComparison.OrdinalIgnoreCase))
                return new KnownIntakeSignal(InferCustomerCode(sourceText, null, cleanSite), cleanSite, [cleanSite]);
        }

        foreach (var signal in KnownSignals)
        {
            if (signal.Aliases.Any(alias => sourceText.Contains(alias, StringComparison.OrdinalIgnoreCase)))
                return signal;
        }

        return null;
    }

    private static string NaturalKey(MailboxEmailIntakeRequest request, string customer, string destination, DateOnly date)
    {
        var canonicalSubject = ReFwRegex.Replace(request.Subject ?? string.Empty, string.Empty).Trim().ToUpperInvariant();
        return $"{(request.SenderAddress ?? string.Empty).Trim().ToLowerInvariant()}|{canonicalSubject}|{date:yyyy-MM-dd}|{customer.ToUpperInvariant()}|{destination.Trim().ToUpperInvariant()}";
    }

    private static string BuildRowReference(string baseReference, string customer, string destination, DateOnly date, int row)
    {
        var baseClean = SafeToken(baseReference, 38);
        var destClean = SafeToken(destination, 24);
        var candidate = $"{baseClean}/{destClean}";
        if (candidate.Length <= 80) return candidate;
        candidate = $"{baseClean}/{SafeToken(customer, 12)}/{date:MMdd}/{row}";
        return candidate[..Math.Min(candidate.Length, 80)];
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
        return clean[..Math.Min(clean.Length, max)];
    }

    private static string BuildInstructions(
        string? rawPo,
        string? requestedTime,
        string? availableTime,
        string? temperature,
        MailboxEmailIntakeRequest request,
        string? attachmentName,
        IReadOnlyCollection<string> warnings,
        string jobType)
    {
        var items = new List<string?>
        {
            $"Order type: {jobType}",
            string.IsNullOrWhiteSpace(rawPo) ? null : $"PO ref: {rawPo}",
            string.IsNullOrWhiteSpace(requestedTime) ? null : $"Requested time: {requestedTime}",
            string.IsNullOrWhiteSpace(availableTime) ? null : $"Available time: {availableTime}",
            string.IsNullOrWhiteSpace(temperature) ? null : $"Temperature: {temperature}",
            $"Source email: {request.Subject}",
            string.IsNullOrWhiteSpace(request.SenderAddress) ? null : $"Source sender: {request.SenderAddress}",
            string.IsNullOrWhiteSpace(attachmentName) ? null : $"Source attachment: {attachmentName}",
            warnings.Count == 0 ? null : $"Intake warning: {string.Join("; ", warnings)}"
        };
        var result = string.Join(" · ", items.Where(item => !string.IsNullOrWhiteSpace(item)));
        return result.Length <= 1000 ? result : result[..1000];
    }

    private static string NormaliseBody(string? bodyText, string? bodyHtml)
    {
        if (!string.IsNullOrWhiteSpace(bodyText)) return bodyText.Trim();
        if (string.IsNullOrWhiteSpace(bodyHtml)) return string.Empty;
        var noTags = HtmlRegex.Replace(bodyHtml, " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, @"[ \t]+", " ").Trim();
    }

    private static string CleanSourceLine(string value)
    {
        var cleaned = Regex.Replace(value, @"\s+", " ").Trim();
        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }

    private static byte[] DecodeBase64(string value)
    {
        var trimmed = value.Trim();
        var comma = trimmed.IndexOf(',');
        if (comma >= 0 && trimmed[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[(comma + 1)..];
        return Convert.FromBase64String(trimmed);
    }

    private static bool LooksOperationalOnly(string subject, string body)
    {
        var value = $"{subject} {body}";
        return value.Contains("night shunting", StringComparison.OrdinalIgnoreCase)
            || value.Contains("available loads", StringComparison.OrdinalIgnoreCase)
            || value.Contains("loads available", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Monarch Available Loads", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Can you cover the below loads", StringComparison.OrdinalIgnoreCase)
            || value.Contains("let us know if you are interested", StringComparison.OrdinalIgnoreCase)
            || value.Contains("You are receiving this email because you opted in", StringComparison.OrdinalIgnoreCase)
            || value.Contains("current stock levels", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ETA for tonight", StringComparison.OrdinalIgnoreCase)
            || value.Contains("missing PO request log", StringComparison.OrdinalIgnoreCase)
            || value.Contains("fleetio.com", StringComparison.OrdinalIgnoreCase)
            || value.Contains("notifications@fleetio.com", StringComparison.OrdinalIgnoreCase)
            || value.Contains("failed inspection", StringComparison.OrdinalIgnoreCase)
            || value.Contains("walk round check", StringComparison.OrdinalIgnoreCase)
            || value.Contains("walkround check", StringComparison.OrdinalIgnoreCase)
            || value.Contains("drivers unit walk round", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksTmsLoopback(string subject, string sender, string body)
    {
        if (!sender.EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase)) return false;
        var value = $"{subject} {body}";
        return value.Contains("SLH TMS Intake Queue", StringComparison.OrdinalIgnoreCase)
            || value.Contains("TMS Intake Queue", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Live Trigger Check", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Order Capture", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseKey(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

internal sealed record KnownIntakeSignal(string CustomerCode, string SiteName, IReadOnlyList<string> Aliases);

public sealed record MailboxAttachmentRequest(
    string? Name,
    string? ContentType,
    string? ContentBase64,
    bool? IsInline = false,
    string? ContentId = null,
    long? Size = null,
    string? ContentBytes = null)
{
    [JsonIgnore]
    public string? EffectiveContentBase64 => ContentBase64 ?? ContentBytes;
}

public sealed record MailboxEmailIntakeRequest(
    string MessageId,
    string? InternetMessageId,
    string? Mailbox,
    string? SenderAddress,
    string? SenderName,
    string? Subject,
    DateTimeOffset? ReceivedAtUtc,
    string? BodyText,
    string? BodyHtml,
    string? WebLink,
    List<MailboxAttachmentRequest>? Attachments,
    string? ConversationId = null,
    JsonElement? ToRecipients = null,
    JsonElement? CcRecipients = null,
    string? BodyFormat = null,
    string? Importance = null,
    string? CorrelationId = null);

public sealed record ParsedEmailOrder(
    string SourceKey,
    string NaturalKey,
    JsonElement Payload,
    IReadOnlyList<string> Warnings);

public sealed record EmailIntakeParseResult(
    IReadOnlyList<ParsedEmailOrder> Orders,
    IReadOnlyList<string> Warnings,
    string? IgnoredReason)
{
    public static EmailIntakeParseResult Ignored(string reason) => new([], [], reason);
}
