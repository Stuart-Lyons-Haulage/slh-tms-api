from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]

# Fix the interleaved two-market table parser using the original pipe-delimited rows.
parser_path = ROOT / "Services" / "MarketsSainsburyWaitroseParser.cs"
text = parser_path.read_text(encoding="utf-8")
pattern = re.compile(
    r"    internal static List<MarketRow> ParseTangmereRows\(string body\)\n    \{.*?\n    \}\n\n    private static IEnumerable<MarketRow> ParseMarketPairs\(string value, string market\)\n    \{.*?\n    \}\n",
    re.S,
)
replacement = r'''    internal static List<MarketRow> ParseTangmereRows(string body)
    {
        var rows = new List<MarketRow>();
        foreach (var rawLine in body.Replace("\r", string.Empty).Split('\n'))
        {
            if (!rawLine.Contains('|')) continue;
            var fields = rawLine.Split('|').Select(CleanName).ToArray();
            if (fields.Length >= 2 && int.TryParse(fields[1], out var westernQty) && westernQty > 0)
                rows.Add(new MarketRow("Western", fields[0], westernQty));
            if (fields.Length >= 5 && int.TryParse(fields[4], out var spitalQty) && spitalQty > 0)
                rows.Add(new MarketRow("Spitalfields", fields[3], spitalQty));
        }
        return rows.Where(row => !string.IsNullOrWhiteSpace(row.Customer)).ToList();
    }
'''
text, count = pattern.subn(replacement, text, count=1)
if count != 1:
    raise RuntimeError("Could not replace Tangmere parser block")
text = text.replace('CellText(row, Find(map, "deliveryaddress"))', 'CellText(row, Find(map, "deliveryaddress")) ?? CellText(row, Find(map, "deliveryaddess"))')
parser_path.write_text(text, encoding="utf-8")

controller_path = ROOT / "Controllers" / "OrderIntakeController.cs"
text = controller_path.read_text(encoding="utf-8")
text = text.replace(
    "    private readonly SainsburyHaulierPlanParser sainsburyParser = new();\n",
    "    private readonly SainsburyHaulierPlanParser sainsburyParser = new();\n    private readonly MarketsSainsburyWaitroseParser marketsSainsburyWaitroseParser = new();\n",
    1,
)
text = text.replace(
    "        ?? nwfParser.TryParse(request)\n        ?? sainsburyParser.TryParse(request)\n",
    "        ?? nwfParser.TryParse(request)\n        ?? marketsSainsburyWaitroseParser.TryParse(request)\n        ?? sainsburyParser.TryParse(request)\n",
    1,
)
text = text.replace(
    '        return Accepted(new { ignored = false, staged, existing, superseded, warnings = parsed.Warnings, outlookCategory = "TMS Imported", records });',
    '        var outlookCategory = parsed.Orders.Any(order => ReadBool(order.Payload, "plannerReady") == false) ? "TMS Review" : "TMS Imported";\n        return Accepted(new { ignored = false, staged, existing, superseded, warnings = parsed.Warnings, outlookCategory, records });',
    1,
)
text = text.replace(
    '        var recognisedSource = sender.EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase) ||\n',
    '        var recognisedSource = sender.EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase) ||\n               sender.EndsWith("@sainsburys.co.uk", StringComparison.OrdinalIgnoreCase) ||\n               sender.EndsWith("@newey.com", StringComparison.OrdinalIgnoreCase) ||\n               sender.EndsWith("@fowlerwelch.co.uk", StringComparison.OrdinalIgnoreCase) ||\n',
    1,
)
text = text.replace(
    '            value.Contains("delivery quantities", StringComparison.OrdinalIgnoreCase) ||\n',
    '            value.Contains("delivery quantities", StringComparison.OrdinalIgnoreCase) ||\n            value.Contains("transport requirements", StringComparison.OrdinalIgnoreCase) ||\n',
    1,
)
controller_path.write_text(text, encoding="utf-8")
