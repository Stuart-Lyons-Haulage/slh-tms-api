using System.Net;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

/// <summary>Reads the complete body rather than the Outlook bodyPreview field.</summary>
public static class MailboxBodyNormalizer
{
    public static string Normalize(string? bodyText, string? bodyHtml)
    {
        if (string.IsNullOrWhiteSpace(bodyHtml))
            return bodyText?.Trim() ?? string.Empty;

        // Preserve rows and cell boundaries so HTML tables and multi-drop lists
        // remain parseable. Do not concatenate the preview: it duplicates rows.
        var text = Regex.Replace(bodyHtml, @"<(script|style)\b[^>]*>.*?</\1\s*>", "",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"</t[dh]>\s*<t[dh]\b[^>]*>", " | ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>|</(?:p|div|tr|li|h[1-6])\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\r?\n[ \t]*", "\n").Trim();
        return string.IsNullOrWhiteSpace(text) ? bodyText?.Trim() ?? string.Empty : text;
    }
}
