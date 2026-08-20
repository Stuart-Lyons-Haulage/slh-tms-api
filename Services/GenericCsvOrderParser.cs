using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Generic CSV parser for customer/supplier files that follow a recognisable
/// tabular order layout. Customer-specific CSV formats can remain ahead of this
/// parser in the intake chain.
/// </summary>
public sealed class GenericCsvOrderParser
{
    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
    {
        var csvAttachments = (request.Attachments ?? [])
            .Where(attachment => attachment.IsInline != true && Path.GetExtension(attachment.Name ?? string.Empty).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            .Where(attachment => !string.IsNullOrWhiteSpace(attachment.ContentBase64))
            .ToList();
        if (csvAttachments.Count == 0) return null;

        var orders = new List<ParsedEmailOrder>();
        var globalWarnings = new List<string>();
        foreach (var attachment in csvAttachments)
        {
            try { orders.AddRange(ParseAttachment(request, attachment)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                globalWarnings.Add($"CSV attachment '{attachment.Name}' could not be extracted: {ex.GetBaseException().Message}");
            }
        }
        return orders.Count > 0 ? new EmailIntakeParseResult(orders, globalWarnings, null) : null;
    }

    private static IEnumerable<ParsedEmailOrder> ParseAttachment(MailboxEmailIntakeRequest request, MailboxAttachmentRequest attachment)
    {
        var bytes = DecodeBase64(attachment.ContentBase64!);
        var text = DetectText(bytes);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        if (lines.Count < 2) return [];

        var delimiter = DetectDelimiter(lines[0]);
        var headers = ParseLine(lines[0], delimiter).Select(NormaliseKey).ToList();
        var deliveryIndex = Find(headers, "deliverysite", "deliverylocation", "depotdescription", "depot", "destination");
        var collectionIndex = Find(headers, "collectionsite", "collectionlocation", "collection", "collectfrom", "supplier");
        var palletIndex = Find(headers, "pallets", "pallet", "palletcount", "palletqty", "palletquantity");
        var collectionDateIndex = Find(headers, "collectiondate", "collectdate", "date");
        var deliveryDateIndex = Find(headers, "deliverydate", "date");
        var poIndex = Find(headers, "ponumber", "po", "purchaseorder", "purchaseordernumber", "orderreference", "orderref");
        if (palletIndex < 0 || (deliveryIndex < 0 && collectionIndex < 0)) return [];

        var customerIndex = Find(headers, "customer", "customercode");
        var supplierIndex = Find(headers, "supplier", "suppliername");
        var casesIndex = Find(headers, "cases", "casecount");
        var quantityIndex = Find(headers, "quantity", "qty");
        var temperatureIndex = Find(headers, "temperature", "temprequirement", "temperatureRequirement");
        var trailerIndex = Find(headers, "trailertype", "trailer");
        var collectionTimeIndex = Find(headers, "collectiontime", "availabletime", "readytime");
        var deliveryTimeIndex = Find(headers, "deliverytime", "requesttime", "requestedtime", "bookingtime");
        var productIndex = Find(headers, "product", "description", "commodity");
        var notesIndex = Find(headers, "notes", "instructions", "specialinstructions", "comments");

        var results = new List<ParsedEmailOrder>();
        for (var lineIndex = 1; lineIndex < lines.Count; lineIndex++)
        {
            var cells = ParseLine(lines[lineIndex], delimiter);
            var pallets = Int(Cell(cells, palletIndex));
            if (pallets is null || pallets <= 0) continue;
            var collection = Cell(cells, collectionIndex);
            var delivery = Cell(cells, deliveryIndex);
            if (string.IsNullOrWhiteSpace(collection) && string.IsNullOrWhiteSpace(delivery)) continue;

            var customer = Cell(cells, customerIndex) ?? InferCustomer(request.Subject, request.SenderAddress, delivery);
            var supplier = Cell(cells, supplierIndex);
            var po = Cell(cells, poIndex);
            var collectionDate = Date(Cell(cells, collectionDateIndex));
            var deliveryDate = Date(Cell(cells, deliveryDateIndex)) ?? collectionDate;
            var warnings = new List<string>();
            if (collectionDate is null) warnings.Add("CSV collection date was missing or invalid.");
            if (string.IsNullOrWhiteSpace(po)) warnings.Add("CSV row did not contain a PO/order reference.");
            if (string.IsNullOrWhiteSpace(collection)) warnings.Add("CSV collection site was blank.");
            if (string.IsNullOrWhiteSpace(delivery)) warnings.Add("CSV delivery site was blank.");

            var technicalReference = po ?? StableEmailReference($"{request.MessageId}|{attachment.Name}|{lineIndex + 1}");
            var naturalKey = !string.IsNullOrWhiteSpace(po)
                ? $"{Normalise(customer)}|PO|{Normalise(po)}"
                : $"{Normalise(customer)}|{collectionDate:yyyy-MM-dd}|{deliveryDate:yyyy-MM-dd}|{Normalise(collection ?? string.Empty)}|{Normalise(delivery ?? string.Empty)}";
            var payload = new Dictionary<string, object?>
            {
                ["customer_supplier"] = string.Join(" / ", new[] { supplier, customer }.Where(value => !string.IsNullOrWhiteSpace(value))),
                ["customer"] = customer,
                ["supplier"] = supplier,
                ["customerCode"] = customer,
                ["jobType"] = "Delivery",
                ["po"] = po,
                ["purchase_order"] = po,
                ["poNumber"] = technicalReference,
                ["orderReference"] = po,
                ["customerPo"] = po,
                ["collectionDate"] = collectionDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["collectionTime"] = Cell(cells, collectionTimeIndex),
                ["collectionLocation"] = collection,
                ["collectionSite"] = collection,
                ["sellerName"] = collection,
                ["deliveryDate"] = deliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["deliveryTime"] = Cell(cells, deliveryTimeIndex),
                ["deliveryLocation"] = delivery,
                ["deliverySite"] = delivery,
                ["stallNumber"] = delivery,
                ["pallets"] = pallets,
                ["cases"] = Int(Cell(cells, casesIndex)),
                ["quantity"] = Decimal(Cell(cells, quantityIndex)),
                ["product"] = Cell(cells, productIndex),
                ["temperature"] = Cell(cells, temperatureIndex),
                ["temperatureRequirement"] = Cell(cells, temperatureIndex),
                ["trailerType"] = Cell(cells, trailerIndex),
                ["loadNotes"] = Cell(cells, notesIndex),
                ["specialInstructions"] = Cell(cells, notesIndex),
                ["planningDate"] = (deliveryDate ?? collectionDate)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["sourceMessageId"] = request.MessageId,
                ["sourceInternetMessageId"] = request.InternetMessageId,
                ["sourceSender"] = request.SenderAddress,
                ["sourceSenderName"] = request.SenderName,
                ["sourceSubject"] = request.Subject,
                ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
                ["sourceWebLink"] = request.WebLink,
                ["sourceAttachmentName"] = attachment.Name,
                ["sourceRow"] = lineIndex + 1,
                ["intakeNaturalKey"] = naturalKey,
                ["intakeMatchKeys"] = !string.IsNullOrWhiteSpace(po) ? new[] { $"PO|{Normalise(customer)}|{Normalise(po)}" } : new[] { $"ROUTE|{naturalKey}" },
                ["intakeConfidence"] = warnings.Count == 0 ? "High" : "Medium",
                ["intakeWarnings"] = warnings,
                ["intakeParser"] = "Generic CSV order table v1",
                ["mappingTemplate"] = "Generic CSV"
            };
            results.Add(new ParsedEmailOrder($"csv-{SafeToken(attachment.Name ?? "file")}-{lineIndex + 1}", naturalKey, JsonSerializer.SerializeToElement(payload), warnings));
        }
        return results;
    }

    private static char DetectDelimiter(string line)
    {
        var candidates = new[] { ',', ';', '\t' };
        return candidates.OrderByDescending(candidate => line.Count(character => character == candidate)).First();
    }

    private static List<string> ParseLine(string line, char delimiter)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
                else quoted = !quoted;
                continue;
            }
            if (character == delimiter && !quoted)
            {
                values.Add(current.ToString().Trim()); current.Clear();
            }
            else current.Append(character);
        }
        values.Add(current.ToString().Trim());
        return values;
    }

    private static string DetectText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] DecodeBase64(string input)
    {
        var value = input.Trim();
        var comma = value.IndexOf(',');
        if (comma >= 0 && value[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) value = value[(comma + 1)..];
        return Convert.FromBase64String(value);
    }

    private static int Find(IReadOnlyList<string> headers, params string[] names)
    {
        foreach (var name in names)
        {
            var index = headers.ToList().FindIndex(header => header == NormaliseKey(name));
            if (index >= 0) return index;
        }
        return -1;
    }

    private static string? Cell(IReadOnlyList<string> cells, int index) => index < 0 || index >= cells.Count || string.IsNullOrWhiteSpace(cells[index]) ? null : cells[index].Trim();
    private static int? Int(string? value) => int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static decimal? Decimal(string? value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static DateOnly? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en-GB"), CultureInfo.InvariantCulture })
            if (DateOnly.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var result)) return result;
        return null;
    }
    private static string InferCustomer(string? subject, string? sender, string? destination)
    {
        var text = $"{subject} {destination}".ToUpperInvariant();
        foreach (var customer in new[] { "WAITROSE", "ALDI", "MORRISONS", "COOP", "SAINSBURY", "OCADO", "IFCO", "NWF" })
            if (text.Contains(customer, StringComparison.OrdinalIgnoreCase)) return customer;
        var domain = (sender ?? string.Empty).Split('@').LastOrDefault()?.Split('.').FirstOrDefault() ?? "EMAIL";
        return SafeToken(domain).ToUpperInvariant();
    }
    private static string NormaliseKey(string value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string Normalise(string value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string SafeToken(string value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } clean ? clean[..Math.Min(clean.Length, 30)] : "ORDER";
    private static string StableEmailReference(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return $"EMAIL-{Convert.ToHexString(bytes)[..12]}";
    }
}
