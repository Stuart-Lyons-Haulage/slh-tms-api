from __future__ import annotations

from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parents[1]


def insert_tests() -> None:
    path = ROOT / "Slh.Tms.Api.Tests" / "EmailOrderIntakeServiceTests.cs"
    text = path.read_text(encoding="utf-8")
    if "ExplicitCollectFrom_OverridesSenderDomainCollectionMapping" in text:
        return
    tests = r'''

    [Fact]
    public void ExplicitCollectFrom_OverridesSenderDomainCollectionMapping()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-explicit-collection", null, "info@lyonshaulage.com", "planner@summerberry.co.uk", "Planner",
            "Customer order 27/08/2026", DateTimeOffset.Parse("2026-08-26T08:00:00Z"),
            """
            Customer: Spinneys
            Collection: 26/08/2026 17:00
            Collect from: Groves Farm
            Delivery date: 27/08/2026
            Pallets: 9
            Delivery address: Heron Foods
            Melton Mowbray
            """, null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("Groves Farm", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-08-27", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("17:00", order.Payload.GetProperty("requestedTime").GetString());
    }

    [Fact]
    public void ExplicitBodyDeliveryTo_OverridesConflictingSubjectDestination()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-body-destination", null, "info@lyonshaulage.com", "planner@example.com", "Planner",
            "COOP delivery 27/08/2026", DateTimeOffset.Parse("2026-08-26T08:00:00Z"),
            "Collect from: Groves Farm\nDelivery to Heron Melton - Snow Hill, Melton Mowbray.\nPallets: 7\nCollection time: 16:00\n27/08/2026", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("Heron Melton", order.Payload.GetProperty("stallNumber").GetString());
    }

    [Fact]
    public void MasterDataBodySignal_OverridesConflictingSubjectCustomer()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-master-precedence", null, "info@lyonshaulage.com", "planner@example.com", "Planner",
            "Waitrose collection 27/08/2026", DateTimeOffset.Parse("2026-08-26T08:00:00Z"),
            "Sainsbury Waltham Point has 12 pallets. Collect from: Groves Farm. Collection time: 15:00.", null, null, null),
            ["Sainsbury Waltham Point"]);

        var order = Assert.Single(result.Orders);
        Assert.Equal("SAINSBURY", order.Payload.GetProperty("customerCode").GetString());
    }

    [Fact]
    public void BarfootsWaitroseWaveBody_StagesEveryWaveAsSeparateOrder()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-barfoots-waves", null, "info@lyonshaulage.com", "planner@barfoots.co.uk", "Planner",
            "Waitrose from Sefter & Leythorne for depot 27/08/26", DateTimeOffset.Parse("2026-08-26T10:19:58Z"),
            """
            Please see attached Waitrose confirmed pallet booking:
            Aylesford WAVE 1 from Sefter 2 pallets PO O78057 & Aylesford Wave 3 10 pallets PO O78077.
            Leyland WAVE 1 from Sefter 1 pallet PO B78374 & Leyland Wave 3 3 pallets PO B78353.
            """, null, null, null));

        Assert.Equal(4, result.Orders.Count);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Aylesford" && order.Payload.GetProperty("pallets").GetInt32() == 10 && order.Payload.GetProperty("customerPo").GetString() == "O78077");
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Leyland" && order.Payload.GetProperty("pallets").GetInt32() == 3 && order.Payload.GetProperty("customerPo").GetString() == "B78353");
        Assert.All(result.Orders, order =>
        {
            Assert.Equal("WAITROSE", order.Payload.GetProperty("customerCode").GetString());
            Assert.Equal("Sefter", order.Payload.GetProperty("sellerName").GetString());
            Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
            Assert.Equal("2026-08-27", order.Payload.GetProperty("deliveryDate").GetString());
            Assert.Equal("Medium", order.Payload.GetProperty("intakeConfidence").GetString());
        });
    }

    [Fact]
    public void SainsburyImageLedTransportRequirements_AreEligibleForMappingReview()
    {
        var request = new MailboxEmailIntakeRequest(
            "message-sainsbury-image", null, "info@lyonshaulage.com", "planner@newey.com", "Planner",
            "Sainsbury's Week 36", DateTimeOffset.Parse("2026-08-26T10:20:22Z"),
            "Please see our transport requirements for week 36.", null, null,
            [new MailboxAttachmentRequest("image001.png", "image/png", "AA==", true)]);
        var parsed = service.Parse(request);
        Assert.NotNull(parsed.IgnoredReason);

        var method = typeof(Slh.Tms.Api.Controllers.OrderIntakeController).GetMethod(
            "ShouldStageMappingException",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        Assert.True((bool)method!.Invoke(null, [request, parsed])!);
    }
'''
    pos = text.rfind("\n}")
    if pos < 0:
        raise RuntimeError("Could not find EmailOrderIntakeServiceTests class terminator")
    path.write_text(text[:pos] + tests + text[pos:], encoding="utf-8")


def apply_service_changes() -> None:
    path = ROOT / "Services" / "EmailOrderIntakeService.cs"
    text = path.read_text(encoding="utf-8")
    if "ApplyPrecedenceOverrides(" in text:
        return

    old = '''        if (orders.Count == 0)\n            return new EmailIntakeParseResult([], globalWarnings, "No transport order could be identified from this email.");\n\n        return new EmailIntakeParseResult(orders, globalWarnings, null);'''
    new = '''        if (orders.Count == 0)\n            return new EmailIntakeParseResult([], globalWarnings, "No transport order could be identified from this email.");\n\n        orders = orders\n            .Select(order => ApplyPrecedenceOverrides(order, request, body, masterSiteNames ?? []))\n            .ToList();\n        return new EmailIntakeParseResult(orders, globalWarnings, null);'''
    if old not in text:
        raise RuntimeError("Parse return marker not found")
    text = text.replace(old, new, 1)

    old = '''        var doubleHWaitrose = ParseDoubleHWaitroseColumnTable(request, rawPo, body, sourceText, receivedAt);\n        if (doubleHWaitrose.Count > 0) return doubleHWaitrose;\n\n        var waitrose = ParseWaitroseDepotTable(request, rawPo, body, sourceText, sourceDate, receivedAt);'''
    new = '''        var doubleHWaitrose = ParseDoubleHWaitroseColumnTable(request, rawPo, body, sourceText, receivedAt);\n        if (doubleHWaitrose.Count > 0) return doubleHWaitrose;\n\n        var barfootsWaitrose = ParseBarfootsWaitroseWaveBody(request, body, receivedAt);\n        if (barfootsWaitrose.Count > 0) return barfootsWaitrose;\n\n        var waitrose = ParseWaitroseDepotTable(request, rawPo, body, sourceText, sourceDate, receivedAt);'''
    if old not in text:
        raise RuntimeError("Structured parser marker not found")
    text = text.replace(old, new, 1)

    marker = "    private static List<ParsedEmailOrder> ParseInternalMorrisonsCollections("
    method = r'''    private static List<ParsedEmailOrder> ParseBarfootsWaitroseWaveBody(
        MailboxEmailIntakeRequest request,
        string body,
        DateTimeOffset receivedAt)
    {
        var source = $"{request.Subject}\n{request.SenderAddress}\n{body}";
        if (!source.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) ||
            !source.Contains("WAVE", StringComparison.OrdinalIgnoreCase) ||
            !source.Contains("pallet", StringComparison.OrdinalIgnoreCase) ||
            !source.Contains("PO", StringComparison.OrdinalIgnoreCase) ||
            (!(request.SenderAddress ?? string.Empty).EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase) &&
             !source.Contains("Barfoots", StringComparison.OrdinalIgnoreCase)))
            return [];

        var deliveryDate = ExtractDate(request.Subject ?? string.Empty, receivedAt)
                           ?? ExtractDateAfter(body, @"depot\s+date[^0-9\r\n]*");
        if (deliveryDate is null) return [];

        var explicitCollectionDate = ExtractDateAfter(body, @"collection(?:\s+date)?[^0-9\r\n]*");
        var collectionDate = explicitCollectionDate ?? LocalDate(receivedAt);
        var rows = Regex.Matches(
                body,
                @"(?<depot>Aylesford|Bracknell|Brinklow|Leyland)\s+WAVE\s+(?<wave>\d+)\s+from\s+(?<collection>[A-Z][A-Z0-9 &'()/-]{1,80}?)\s+(?<qty>\d{1,3})\s+pallets?\s+PO\s+(?<po>[A-Z0-9/-]+)",
                RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => new
            {
                Depot = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups["depot"].Value.ToLowerInvariant()),
                Wave = int.Parse(match.Groups["wave"].Value, CultureInfo.InvariantCulture),
                Collection = CleanSourceLine(match.Groups["collection"].Value),
                Pallets = int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture),
                Po = match.Groups["po"].Value.Trim().ToUpperInvariant()
            })
            .Where(row => row.Pallets > 0)
            .ToList();
        if (rows.Count == 0) return [];

        return rows.Select(row =>
        {
            var warnings = new List<string>();
            if (explicitCollectionDate is null)
                warnings.Add("Collection date inferred as the email received date from the Barfoots Waitrose wave template; confirm if collection occurs on a different day.");
            return BuildStructuredOrder(
                request,
                $"barfoots-waitrose-{NormaliseKey(row.Depot)}-wave-{row.Wave}-{NormaliseKey(row.Po)}",
                "WAITROSE",
                row.Po,
                collectionDate,
                deliveryDate.Value,
                row.Pallets,
                row.Collection,
                row.Depot,
                $"Waitrose Wave {row.Wave} depot delivery",
                warnings);
        }).ToList();
    }

'''
    if marker not in text:
        raise RuntimeError("Barfoots insertion marker not found")
    text = text.replace(marker, method + marker, 1)

    marker = "    private static bool HasEnoughBodyOrderEvidence("
    methods = r'''    private static ParsedEmailOrder ApplyPrecedenceOverrides(
        ParsedEmailOrder order,
        MailboxEmailIntakeRequest request,
        string body,
        IReadOnlyCollection<string> masterSiteNames)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(order.Payload.GetRawText())
                      ?? new Dictionary<string, object?>();
        var warnings = order.Warnings.ToList();
        var fieldSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var receivedAt = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;

        var collectFrom = ExtractMatch(CollectFromRegex, body, "site");
        var collectionLabel = ExtractLabelValue(body, "collection", "collect", "pickup");
        var explicitCollectionSite = !string.IsNullOrWhiteSpace(collectFrom)
            ? CleanSourceLine(collectFrom)
            : ExtractCollectionSiteFromLabel(collectionLabel);
        if (!string.IsNullOrWhiteSpace(explicitCollectionSite))
        {
            payload["sellerName"] = explicitCollectionSite;
            fieldSources["collectionSite"] = "body.explicit";
            warnings.RemoveAll(warning =>
                warning.Contains("Collection site was not explicit", StringComparison.OrdinalIgnoreCase) ||
                warning.StartsWith("Collection site inferred as ", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var senderSite = InferCollectionSiteFromSender(request.SenderAddress);
            var existingSite = PayloadText(payload, "sellerName");
            fieldSources["collectionSite"] = !string.IsNullOrWhiteSpace(senderSite) && string.Equals(senderSite, existingSite, StringComparison.OrdinalIgnoreCase)
                ? "master.sender-domain"
                : "template-or-fallback";
        }

        var deliveryAddress = ExtractLabelBlock(body, "addressofdelivery", "adressofdelivery", "deliveryaddress", "deliverto", "destination", "shipto");
        var bodyDeliveryTo = ExtractMatch(DeliveryToRegex, body, "site");
        var explicitDestination = CleanDeliveryAddressForSite(deliveryAddress)
                                  ?? DeliverySiteName(bodyDeliveryTo);
        if (!string.IsNullOrWhiteSpace(explicitDestination))
        {
            payload["stallNumber"] = explicitDestination;
            fieldSources["deliverySite"] = "body.explicit";
            if (!string.IsNullOrWhiteSpace(deliveryAddress))
                payload["deliveryAddress"] = deliveryAddress;
            warnings.RemoveAll(warning =>
                warning.Contains("Delivery address/destination was not explicit", StringComparison.OrdinalIgnoreCase) ||
                warning.Contains("Delivery/return destination was not explicit", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            fieldSources["deliverySite"] = "template-or-subject";
        }

        var explicitCollectionDate = ParseDateText(collectionLabel, receivedAt)
                                     ?? ExtractDateAfter(body, @"collection(?:\s+date)?[^.\r\n]*?")
                                     ?? ExtractDateAfter(body, @"collect[^.\r\n]*?");
        if (explicitCollectionDate is not null)
        {
            payload["collectionDate"] = explicitCollectionDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            fieldSources["collectionDate"] = "body.explicit";
        }
        else
        {
            fieldSources["collectionDate"] = "template-or-subject";
        }

        var deliveryLabel = ExtractLabelValue(body, "deliverydate", "depotdate", "deliver", "delivery");
        var explicitDeliveryDate = ParseDateText(deliveryLabel, receivedAt)
                                   ?? ExtractDateAfter(body, @"depot\s+date[^.\r\n]*?")
                                   ?? ExtractDateAfter(body, @"delivery\s+date[^.\r\n]*?");
        if (explicitDeliveryDate is not null)
        {
            payload["deliveryDate"] = explicitDeliveryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            fieldSources["deliveryDate"] = "body.explicit";
        }
        else
        {
            fieldSources["deliveryDate"] = "template-or-subject";
        }

        var explicitCollectionTime = NormaliseTime(ExtractTime(collectionLabel) ?? ExtractMatch(CollectionTimeRegex, body, "time"));
        if (!string.IsNullOrWhiteSpace(explicitCollectionTime))
        {
            payload["requestedTime"] = explicitCollectionTime;
            fieldSources["collectionTime"] = "body.explicit";
        }
        else
        {
            fieldSources["collectionTime"] = "template-or-fallback";
        }

        var explicitDeliveryTime = NormaliseTime(ExtractMatch(DeliveryDeadlineRegex, body, "time"));
        if (!string.IsNullOrWhiteSpace(explicitDeliveryTime))
        {
            payload["deliveryRequestedTime"] = explicitDeliveryTime;
            payload["deliveryTimeConstraint"] = "Not later than";
            fieldSources["deliveryTime"] = "body.explicit";
        }
        else
        {
            fieldSources["deliveryTime"] = "template-or-fallback";
        }

        var explicitCustomer = CleanCustomerName(ExtractLabelValue(body, "customer"));
        var masterSite = FindMasterSiteMention(body, masterSiteNames);
        var bodySignal = DetectKnownSignal(body, []);
        var subjectSignal = DetectKnownSignal(request.Subject ?? string.Empty, []);
        var existingCustomer = PayloadText(payload, "customerCode");
        string? resolvedCustomer;
        string customerSource;
        if (!string.IsNullOrWhiteSpace(explicitCustomer))
        {
            resolvedCustomer = CustomerCode(explicitCustomer);
            customerSource = "body.explicit";
            payload["marketName"] = explicitCustomer;
        }
        else if (!string.IsNullOrWhiteSpace(masterSite))
        {
            resolvedCustomer = InferCustomerCode(masterSite, null, masterSite);
            customerSource = "master-data.body-site";
            if (!string.IsNullOrWhiteSpace(existingCustomer) && !string.Equals(existingCustomer, resolvedCustomer, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Customer resolved as {resolvedCustomer} from master-data site match, taking precedence over conflicting lower-priority hints.");
        }
        else if (bodySignal is not null)
        {
            resolvedCustomer = bodySignal.CustomerCode;
            customerSource = "template.body-signal";
        }
        else if (IsTemplateSource(order.SourceKey) && !string.IsNullOrWhiteSpace(existingCustomer) && !string.Equals(existingCustomer, "EMAIL", StringComparison.OrdinalIgnoreCase))
        {
            resolvedCustomer = existingCustomer;
            customerSource = "template";
        }
        else if (subjectSignal is not null)
        {
            resolvedCustomer = subjectSignal.CustomerCode;
            customerSource = "subject";
        }
        else
        {
            resolvedCustomer = existingCustomer ?? InferCustomerCode(request.Subject, request.SenderAddress, PayloadText(payload, "stallNumber"));
            customerSource = "fallback";
        }
        if (!string.IsNullOrWhiteSpace(resolvedCustomer))
            payload["customerCode"] = resolvedCustomer;
        fieldSources["customer"] = customerSource;

        warnings = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        payload["intakeWarnings"] = warnings;
        payload["intakeConfidence"] = PrecedenceConfidence(warnings);
        payload["intakeFieldSources"] = fieldSources;

        var naturalKey = BuildPrecedenceNaturalKey(order, request, payload);
        payload["intakeNaturalKey"] = naturalKey;
        return new ParsedEmailOrder(order.SourceKey, naturalKey, JsonSerializer.SerializeToElement(payload), warnings);
    }

    private static string? ExtractCollectionSiteFromLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = DateRegex.Replace(value, " ");
        clean = MonthNameDateRegex.Replace(clean, " ");
        clean = Regex.Replace(clean, @"\b(?:[01]?\d|2[0-3])(?:[:.]\d{2})?\s*(?:am|pm)?\b", " ", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\b(?:today|tomorrow)\b", " ", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '–', '—', ':');
        return clean.Any(char.IsLetter) && clean.Length >= 3 ? CleanSourceLine(clean) : null;
    }

    private static string? DeliverySiteName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = CleanSourceLine(value).Trim(' ', '-', '–', '—');
        var separator = clean.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0) clean = clean[..separator].Trim();
        else
        {
            var comma = clean.IndexOf(',');
            if (comma > 0) clean = clean[..comma].Trim();
        }
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static string? FindMasterSiteMention(string body, IReadOnlyCollection<string> masterSiteNames) =>
        masterSiteNames
            .Where(site => !string.IsNullOrWhiteSpace(site) && site.Trim().Length >= 3)
            .OrderByDescending(site => site.Length)
            .FirstOrDefault(site => body.Contains(site.Trim(), StringComparison.OrdinalIgnoreCase))
            ?.Trim();

    private static bool IsTemplateSource(string sourceKey) =>
        !sourceKey.StartsWith("body-", StringComparison.OrdinalIgnoreCase) &&
        !sourceKey.StartsWith("labelled-body-", StringComparison.OrdinalIgnoreCase);

    private static string? PayloadText(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null) return null;
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String) return element.GetString();
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            return element.ToString();
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string PrecedenceConfidence(IReadOnlyList<string> warnings)
    {
        var hardWarnings = warnings.Count(warning =>
            !warning.StartsWith("Collection site inferred as ", StringComparison.OrdinalIgnoreCase) &&
            !warning.StartsWith("No customer PO/reference", StringComparison.OrdinalIgnoreCase) &&
            !warning.StartsWith("Customer resolved as ", StringComparison.OrdinalIgnoreCase));
        return hardWarnings == 0 ? "High" : hardWarnings <= 2 ? "Medium" : "Low";
    }

    private static string BuildPrecedenceNaturalKey(
        ParsedEmailOrder order,
        MailboxEmailIntakeRequest request,
        Dictionary<string, object?> payload)
    {
        var customer = PayloadText(payload, "customerCode") ?? "EMAIL";
        var customerPo = PayloadText(payload, "customerPo") ?? order.SourceKey;
        var collectionDate = PayloadText(payload, "collectionDate") ?? string.Empty;
        var deliveryDate = PayloadText(payload, "deliveryDate") ?? collectionDate;
        var collection = PayloadText(payload, "sellerName");
        var destination = PayloadText(payload, "stallNumber");
        var pallets = PayloadText(payload, "pallets");
        return $"{(request.SenderAddress ?? string.Empty).Trim().ToLowerInvariant()}|{NormaliseKey(customer)}|{NormaliseKey(customerPo)}|{collectionDate}|{deliveryDate}|{NormaliseKey(collection)}|{NormaliseKey(destination)}|{pallets}";
    }

'''
    if marker not in text:
        raise RuntimeError("Precedence insertion marker not found")
    text = text.replace(marker, methods + marker, 1)

    old = '''        if (jobType == "Tray collection") return null;\n        if (clean.Contains("COOP", StringComparison.OrdinalIgnoreCase) || clean.Contains("CO-OP", StringComparison.OrdinalIgnoreCase)) return "COOP";\n        var subjectDeliveryTo = Regex.Match(clean, @"^Delivery\\s+to\\s+(?<dest>.+?)(?:\\s+\\d{1,2}[./-]\\d{1,2}(?:[./-]\\d{2,4})?)?$", RegexOptions.IgnoreCase);\n        if (subjectDeliveryTo.Success) return CleanSourceLine(subjectDeliveryTo.Groups["dest"].Value.Trim(' ', '-', '–', '—'));\n        var bodyDeliveryTo = ExtractMatch(DeliveryToRegex, body, "site");\n        if (!string.IsNullOrWhiteSpace(bodyDeliveryTo))\n            return CleanSourceLine(bodyDeliveryTo.Trim(' ', '-', '–', '—'));'''
    new = '''        if (jobType == "Tray collection") return null;\n        var bodyDeliveryTo = ExtractMatch(DeliveryToRegex, body, "site");\n        if (!string.IsNullOrWhiteSpace(bodyDeliveryTo))\n            return DeliverySiteName(bodyDeliveryTo);\n        var subjectDeliveryTo = Regex.Match(clean, @"^Delivery\\s+to\\s+(?<dest>.+?)(?:\\s+\\d{1,2}[./-]\\d{1,2}(?:[./-]\\d{2,4})?)?$", RegexOptions.IgnoreCase);\n        if (subjectDeliveryTo.Success) return CleanSourceLine(subjectDeliveryTo.Groups["dest"].Value.Trim(' ', '-', '–', '—'));\n        if (clean.Contains("COOP", StringComparison.OrdinalIgnoreCase) || clean.Contains("CO-OP", StringComparison.OrdinalIgnoreCase)) return "COOP";'''
    if old not in text:
        raise RuntimeError("Destination precedence marker not found")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


def apply_controller_changes() -> None:
    path = ROOT / "Controllers" / "OrderIntakeController.cs"
    text = path.read_text(encoding="utf-8")
    if 'sender.EndsWith("@newey.com"' not in text:
        marker = '''               sender.EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase) ||\n               value.Contains("NWAY", StringComparison.OrdinalIgnoreCase) ||'''
        replacement = '''               sender.EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase) ||\n               sender.EndsWith("@newey.com", StringComparison.OrdinalIgnoreCase) ||\n               sender.EndsWith("@sainsburys.co.uk", StringComparison.OrdinalIgnoreCase) ||\n               value.Contains("Sainsbury", StringComparison.OrdinalIgnoreCase) ||\n               value.Contains("NWAY", StringComparison.OrdinalIgnoreCase) ||'''
        if marker not in text:
            raise RuntimeError("Recognized source marker not found")
        text = text.replace(marker, replacement, 1)
    if 'value.Contains("transport requirements"' not in text:
        marker = '''            value.Contains("delivery to", StringComparison.OrdinalIgnoreCase))'''
        replacement = '''            value.Contains("delivery to", StringComparison.OrdinalIgnoreCase) ||\n            value.Contains("transport requirements", StringComparison.OrdinalIgnoreCase))'''
        if marker not in text:
            raise RuntimeError("Order intent marker not found")
        text = text.replace(marker, replacement, 1)
    if 'return "SAINSBURY";' not in text:
        marker = '''        if (value.Contains("Barfoots", StringComparison.OrdinalIgnoreCase) ||\n            (request.SenderAddress ?? string.Empty).EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase))\n            return "BARFOOTS";\n        if (value.Contains("Aldi", StringComparison.OrdinalIgnoreCase)) return "ALDI";'''
        replacement = '''        if (value.Contains("Barfoots", StringComparison.OrdinalIgnoreCase) ||\n            (request.SenderAddress ?? string.Empty).EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase))\n            return "BARFOOTS";\n        if (value.Contains("Sainsbury", StringComparison.OrdinalIgnoreCase) ||\n            (request.SenderAddress ?? string.Empty).EndsWith("@sainsburys.co.uk", StringComparison.OrdinalIgnoreCase) ||\n            (request.SenderAddress ?? string.Empty).EndsWith("@newey.com", StringComparison.OrdinalIgnoreCase))\n            return "SAINSBURY";\n        if (value.Contains("Aldi", StringComparison.OrdinalIgnoreCase)) return "ALDI";'''
        if marker not in text:
            raise RuntimeError("Mapping customer marker not found")
        text = text.replace(marker, replacement, 1)
    path.write_text(text, encoding="utf-8")


def apply_flow_changes() -> None:
    path = ROOT / "power-automate" / "info-mailbox-order-intake" / "workflow.json"
    workflow = json.loads(path.read_text(encoding="utf-8"))
    actions = workflow["properties"]["definition"]["actions"]
    submit = actions["Scope_Submit_To_TMS"]["actions"]
    graph_uri = "@concat('https://graph.microsoft.com/v1.0/users/', encodeUriComponent(parameters('SLH_InfoMailboxUPN')), '/messages/', encodeUriComponent(triggerOutputs()?['body/id']))"
    host = {"apiId": "/providers/Microsoft.PowerApps/apis/shared_office365", "connectionName": "shared_office365", "operationId": "HttpRequest"}
    retry = {"type": "exponential", "count": 2, "interval": "PT5S", "minimumInterval": "PT5S", "maximumInterval": "PT30S"}
    submit["Condition_Mark_Source_Email"] = {
        "type": "If",
        "runAfter": {"Record_Import_Result": ["Succeeded"]},
        "expression": {"and": [{"not": {"equals": ["@body('POST_To_TMS_Staging')?['outlookCategory']", None]}}]},
        "actions": {
            "GET_Source_Email_For_Category": {
                "type": "OpenApiConnection",
                "inputs": {"host": host, "parameters": {"Uri": graph_uri, "Method": "GET", "ContentType": "application/json"}},
                "runtimeConfiguration": {"retryPolicy": retry, "secureData": {"properties": ["outputs"]}},
            },
            "PATCH_Source_Email_Category": {
                "type": "OpenApiConnection",
                "runAfter": {"GET_Source_Email_For_Category": ["Succeeded"]},
                "inputs": {"host": host, "parameters": {
                    "Uri": graph_uri,
                    "Method": "PATCH",
                    "Body": "@concat('{\"categories\":', string(union(coalesce(body('GET_Source_Email_For_Category')?['categories'], json('[]')), createArray(body('POST_To_TMS_Staging')?['outlookCategory']))), '}')",
                    "ContentType": "application/json",
                }},
                "runtimeConfiguration": {"retryPolicy": retry},
            },
        },
        "else": {"actions": {}},
    }
    error = actions["Scope_Error_Handler"]["actions"]
    error["GET_Source_Email_For_Review_Category"] = {
        "type": "OpenApiConnection",
        "runAfter": {"Handle_Import_Exception": ["Succeeded"]},
        "inputs": {"host": host, "parameters": {"Uri": graph_uri, "Method": "GET", "ContentType": "application/json"}},
        "runtimeConfiguration": {"retryPolicy": retry, "secureData": {"properties": ["outputs"]}},
    }
    error["PATCH_Source_Email_TMS_Review"] = {
        "type": "OpenApiConnection",
        "runAfter": {"GET_Source_Email_For_Review_Category": ["Succeeded"]},
        "inputs": {"host": host, "parameters": {
            "Uri": graph_uri,
            "Method": "PATCH",
            "Body": "@concat('{\"categories\":', string(union(coalesce(body('GET_Source_Email_For_Review_Category')?['categories'], json('[]')), createArray('TMS Review'))), '}')",
            "ContentType": "application/json",
        }},
        "runtimeConfiguration": {"retryPolicy": retry},
    }
    error["Terminate_Failed_Import"]["runAfter"] = {"PATCH_Source_Email_TMS_Review": ["Succeeded", "Failed", "TimedOut", "Skipped"]}
    path.write_text(json.dumps(workflow, indent=2) + "\n", encoding="utf-8")

    validator_path = path.parent / "validate_workflow.py"
    validator = validator_path.read_text(encoding="utf-8")
    if "shared-mailbox category update must use" not in validator:
        marker = '    connection_names = set(properties.get("connectionReferences", {}))\n'
        checks = '''    if '\"operationId\":\"HttpRequest\"' not in serialized:\n        errors.append("shared-mailbox category update must use the Outlook Microsoft Graph HTTP action")\n    if "outlookCategory" not in serialized or "TMS Review" not in serialized:\n        errors.append("TMS result categories are missing")\n    if "graph.microsoft.com/v1.0/users/" not in serialized or "/messages/" not in serialized:\n        errors.append("shared-mailbox category update must target the source message through Microsoft Graph")\n    if "AssignCategory" in serialized:\n        errors.append("AssignCategory is not valid for this shared-mailbox workflow; use Graph /users/{mailbox}/messages/{id}")\n\n'''
        if marker not in validator:
            raise RuntimeError("Validator marker not found")
        validator_path.write_text(validator.replace(marker, checks + marker, 1), encoding="utf-8")

    test_path = path.parent / "test_validate_workflow.py"
    tests = test_path.read_text(encoding="utf-8")
    if "test_rejects_missing_shared_mailbox_category_workflow" not in tests:
        addition = r'''

    def test_rejects_missing_shared_mailbox_category_workflow(self):
        workflow = json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))
        del workflow["properties"]["definition"]["actions"]["Scope_Submit_To_TMS"]["actions"]["Condition_Mark_Source_Email"]
        errors = validate(workflow)
        self.assertTrue(any("category" in item.lower() for item in errors))

    def test_rejects_assign_category_for_shared_mailbox(self):
        workflow = json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))
        workflow["properties"]["definition"]["actions"]["Fake_Assign_Category"] = {
            "type": "OpenApiConnection",
            "inputs": {"host": {"operationId": "AssignCategory"}},
        }
        errors = validate(workflow)
        self.assertTrue(any("AssignCategory" in item for item in errors))
'''
        marker = '\n\nif __name__ == "__main__":'
        pos = tests.rfind(marker)
        if pos < 0:
            raise RuntimeError("Validator test marker not found")
        test_path.write_text(tests[:pos] + addition + tests[pos:], encoding="utf-8")


def apply_implementation() -> None:
    apply_service_changes()
    apply_controller_changes()
    apply_flow_changes()


def main() -> None:
    if len(sys.argv) != 2 or sys.argv[1] not in {"tests", "implementation"}:
        raise SystemExit("usage: _temporary_info_mailbox_fix.py tests|implementation")
    if sys.argv[1] == "tests":
        insert_tests()
    else:
        apply_implementation()


if __name__ == "__main__":
    main()
