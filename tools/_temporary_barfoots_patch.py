from pathlib import Path

path = Path('Services/EmailOrderIntakeService.cs')
text = path.read_text(encoding='utf-8')
start_marker = '        var rows = Regex.Matches(\n                body,\n                @"(?<depot>Aylesford'
end_marker = '        if (rows.Count == 0) return [];'
start = text.index(start_marker)
end = text.index(end_marker, start)
replacement = r'''        var waveMatches = Regex.Matches(
                body,
                @"(?<depot>Aylesford|Bracknell|Brinklow|Leyland)\s+WAVE\s+(?<wave>\d+)(?:\s+from\s+(?<collection>[A-Z][A-Z0-9 &'()/-]{1,80}?))?\s+(?<qty>\d{1,3})\s+pallets?\s+PO\s+(?<po>[A-Z0-9/-]+)",
                RegexOptions.IgnoreCase)
            .Cast<Match>()
            .ToList();
        var rows = new List<(string Depot, int Wave, string Collection, int Pallets, string Po)>();
        string? currentCollection = null;
        foreach (var match in waveMatches)
        {
            var statedCollection = match.Groups["collection"].Success
                ? CleanSourceLine(match.Groups["collection"].Value)
                : null;
            if (!string.IsNullOrWhiteSpace(statedCollection)) currentCollection = statedCollection;
            if (string.IsNullOrWhiteSpace(currentCollection)) continue;
            var pallets = int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture);
            if (pallets <= 0) continue;
            rows.Add((
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups["depot"].Value.ToLowerInvariant()),
                int.Parse(match.Groups["wave"].Value, CultureInfo.InvariantCulture),
                currentCollection,
                pallets,
                match.Groups["po"].Value.Trim().ToUpperInvariant()));
        }
'''
path.write_text(text[:start] + replacement + text[end:], encoding='utf-8')
