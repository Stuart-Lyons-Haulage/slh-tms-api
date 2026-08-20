using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Contracts;

/// <summary>
/// Power Automate transport contract. It deliberately contains richer Outlook
/// evidence than the parser contract so existing parsers remain backwards
/// compatible while staging receives a complete audit trail.
/// </summary>
public sealed record MailboxEmailIntakeEnvelope
{
    public required string MessageId { get; init; }
    public string? InternetMessageId { get; init; }
    public string? ConversationId { get; init; }
    public string? Mailbox { get; init; }
    public string? SenderAddress { get; init; }
    public string? SenderName { get; init; }
    public List<string>? ToRecipients { get; init; }
    public List<string>? CcRecipients { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset? ReceivedAtUtc { get; init; }
    public string? BodyText { get; init; }
    public string? BodyHtml { get; init; }
    public string? BodyFormat { get; init; }
    public string? Importance { get; init; }
    public string? WebLink { get; init; }
    public int? AttachmentCount { get; init; }
    public string? CorrelationId { get; init; }
    public string? FlowRunId { get; init; }
    public List<MailboxIntakeAttachmentEnvelope>? Attachments { get; init; }

    public MailboxEmailIntakeRequest ToParserRequest() => new(
        MessageId,
        InternetMessageId,
        Mailbox,
        SenderAddress,
        SenderName,
        Subject,
        ReceivedAtUtc,
        BodyText,
        BodyHtml,
        WebLink,
        Attachments?.Select(attachment => new MailboxAttachmentRequest(
            attachment.Name,
            attachment.ContentType,
            attachment.ContentBase64,
            attachment.IsInline)).ToList());
}

public sealed record MailboxIntakeAttachmentEnvelope
{
    public string? AttachmentId { get; init; }
    public string? Name { get; init; }
    public string? ContentType { get; init; }
    public string? ContentBase64 { get; init; }
    public bool? IsInline { get; init; }
    public long? SizeBytes { get; init; }
}
