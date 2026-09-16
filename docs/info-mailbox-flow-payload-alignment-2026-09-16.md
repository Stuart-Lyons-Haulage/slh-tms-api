# Info mailbox Power Automate payload alignment

Date: 2026-09-16

This documents the production payload shape expected by `POST /api/v1/order-intake/email` after the Info Mailbox flow repair.

## Required Power Automate shape

The flow must send a single JSON body containing:

- `messageId`: Outlook message `id` from the shared mailbox trigger.
- `internetMessageId`: Outlook internet message id when available.
- `conversationId`: Outlook conversation id when available.
- `mailbox`: `info@lyonshaulage.com`.
- `senderAddress` and `senderName`.
- `subject`.
- `receivedAtUtc`.
- `bodyText`: full body text or HTML-derived body; do not rely only on `bodyPreview`.
- `bodyHtml`: original Outlook body.
- `bodyFormat`: normally `html`.
- `webLink`.
- `correlationId`: `guid()` from Power Automate.
- `attachments`: the `NormalizedAttachments` array.

## Attachment object

Each `attachments[]` item must be an object, not a concatenated string:

```json
{
  "id": "<attachment id>",
  "name": "<original file name>",
  "contentType": "<content type>",
  "size": 12345,
  "isInline": false,
  "contentBytes": "<base64 from Get Attachment (V2)>"
}
```

`contentBytes` must come from **Get Attachment (V2)**, not the initial shared mailbox trigger attachment summary.

## Runtime behaviour

- Inline images/signatures are ignored in the planner document view.
- PDF/CSV/XLS/XLSX/XLSM files are retained as order evidence.
- Site Master matching is enrichment-only. A missing Site Master match must not block staging.
- Sender-route defaults must not bypass Site Master enrichment.
- Low confidence intake should remain visible in Order Review for planner action.
