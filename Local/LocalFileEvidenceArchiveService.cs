using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Local;

public sealed class LocalFileEvidenceArchiveService(IConfiguration configuration, ILogger<LocalFileEvidenceArchiveService> logger)
{
    private readonly bool enabled = configuration.GetValue<bool>("LocalTest:Enabled") &&
                                    configuration.GetValue<bool?>("LocalStorage:Enabled") != false;
    private readonly string root = configuration["LocalStorage:Root"] ??
                                   Path.Combine(AppContext.BaseDirectory, "local-data", "customer-files");

    public async Task ArchiveAsync(
        MailboxEmailIntakeRequest request,
        EmailIntakeParseResult parsed,
        CancellationToken ct)
    {
        if (!enabled) return;

        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var customers = parsed.Orders
            .Select(order => Text(order.Payload, "customerCode"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Sanitize(value!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var customer = customers.Count switch
        {
            0 => "_Unclassified",
            1 => customers[0],
            _ => "_MultiCustomer"
        };

        var messageKey = MessageKey(request.MessageId);
        var folder = Path.Combine(
            Path.GetFullPath(root),
            "Customers",
            customer,
            received.Year.ToString("0000"),
            received.Month.ToString("00"),
            received.Day.ToString("00"),
            messageKey);

        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "attachments"));
        Directory.CreateDirectory(Path.Combine(folder, "orders"));

        var manifest = new
        {
            request.MessageId,
            request.InternetMessageId,
            request.ConversationId,
            request.Mailbox,
            request.SenderAddress,
            request.SenderName,
            request.Subject,
            request.ReceivedAtUtc,
            request.WebLink,
            parsed.IgnoredReason,
            warnings = parsed.Warnings,
            orders = parsed.Orders.Select((order, index) => new
            {
                index = index + 1,
                order.SourceKey,
                order.NaturalKey,
                order.Warnings,
                payload = order.Payload
            }).ToList(),
            attachments = (request.Attachments ?? []).Select(a => new
            {
                a.Name,
                a.ContentType,
                a.ContentId,
                a.Size,
                a.IsInline,
                stored = a.IsInline != true &&
                         IsSupportedDocument(a.Name) &&
                         !string.IsNullOrWhiteSpace(a.EffectiveContentBase64)
            }).ToList(),
            archivedAtUtc = DateTimeOffset.UtcNow
        };

        await File.WriteAllTextAsync(
            Path.Combine(folder, "source-email.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8,
            ct);

        if (!string.IsNullOrWhiteSpace(request.BodyText))
            await File.WriteAllTextAsync(Path.Combine(folder, "body.txt"), request.BodyText, Encoding.UTF8, ct);
        if (!string.IsNullOrWhiteSpace(request.BodyHtml))
            await File.WriteAllTextAsync(Path.Combine(folder, "body.html"), request.BodyHtml, Encoding.UTF8, ct);

        var orderIndex = 0;
        foreach (var order in parsed.Orders)
        {
            orderIndex++;
            await File.WriteAllTextAsync(
                Path.Combine(folder, "orders", $"{orderIndex:000}-{Sanitize(order.SourceKey)}.json"),
                JsonSerializer.Serialize(order.Payload, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8,
                ct);
        }

        foreach (var attachment in request.Attachments ?? [])
        {
            if (attachment.IsInline == true || !IsSupportedDocument(attachment.Name) ||
                string.IsNullOrWhiteSpace(attachment.EffectiveContentBase64))
                continue;

            try
            {
                var bytes = Convert.FromBase64String(attachment.EffectiveContentBase64);
                var filename = SanitizeFileName(attachment.Name ?? "attachment.bin");
                await File.WriteAllBytesAsync(Path.Combine(folder, "attachments", filename), bytes, ct);
            }
            catch (FormatException ex)
            {
                logger.LogWarning(ex, "Local evidence attachment {AttachmentName} did not contain valid base64.", attachment.Name);
            }
        }
    }

    private static string? Text(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : property.Value.ToString();
        return null;
    }

    private static bool IsSupportedDocument(string? name)
    {
        var ext = Path.GetExtension(name ?? string.Empty);
        return ext.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string MessageKey(string messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Trim(' ', '.');
    }

    private static string SanitizeFileName(string value)
    {
        var cleaned = Sanitize(value);
        return string.IsNullOrWhiteSpace(cleaned) ? "attachment.bin" : cleaned;
    }
}
