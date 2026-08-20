# SLH TMS — Production Info Mailbox Order Intake

## Purpose

Production flow name: **SLH TMS - Info Mailbox Order Intake - PROD**

Architecture:

`Info shared mailbox -> Power Automate -> complete email/attachment envelope -> SLH TMS /api/v1/order-intake/email -> parser/mapping/validation/duplicate control -> StagedImports PendingReview -> Order Review -> approval -> live Orders -> Planner`

The flow must never call the live Orders endpoint. The only intake action is the existing custom-connector action **IntakeInfoMailboxEmail** (`POST /api/v1/order-intake/email`).

## Connections and security

- Outlook connector: Microsoft 365 Outlook connection with Full Access to the shared Info mailbox.
- TMS connector: existing **SLH TMS API** custom connector imported from `openapi-power-automate.yaml`.
- Authentication: OAuth 2.0 / Microsoft Entra delegated `Tms.Access` scope. Use an SLH-managed `@lyonshaulage.com` connection identity. Do not place client secrets, bearer tokens or API keys in actions/expressions.
- Turn **Secure Inputs** and **Secure Outputs** on for `Get_Source_Attachment_Content`, `Build_TMS_Mailbox_Envelope`, `POST_Info_Email_To_TMS_Staging`, and response parsing actions that may contain order/customer data.
- Source email is read-only. Do not move, delete, categorise, mark read/unread or rewrite it.

## Trigger

### `When_New_Email_Arrives_In_Info_Mailbox`

Connector: **Microsoft 365 Outlook**

Action: **When a new email arrives in a shared mailbox (V2)**

Configuration:

- Original Mailbox Address: `info@lyonshaulage.com`
- Folder: `Inbox`
- Only with Attachments: `No`
- Include Attachments: `No`
- Subject filter: blank
- Sender filter: blank
- Importance: Any

Do not filter the trigger to known customers. Unknown order-looking messages must still reach the TMS review path.

Trigger settings:

- Concurrency control: On
- Degree of parallelism: `5`
- Split On: leave connector default

`Include Attachments = No` is deliberate. Attachment bytes are retrieved with `Get Attachment (V2)` so bursts of attachment-heavy email do not make the trigger wait for every file download.

## Scope 1 — RECEIVE

Create scope **`Scope_01_Receive_Source_Email`**.

### `Initialise_Correlation_Id`

Connector: Variables

Action: Initialise variable

- Name: `varCorrelationId`
- Type: String
- Value expression:

```text
guid()
```

### `Initialise_Attachment_Array`

Connector: Variables

- Name: `varAttachments`
- Type: Array
- Value: `[]`

### `Get_Source_Email_Metadata`

Connector: Microsoft 365 Outlook

Action: **Get email (V2)**

- Message Id: trigger **Message Id / Id**
- Original Mailbox Address: `info@lyonshaulage.com`
- Include Attachments: `No`
- Internet Message Id: trigger Internet Message Id when the designer exposes it; otherwise blank because Message Id is authoritative.

This action is the source for sender, recipients, conversation ID, subject, received time, body, importance, web link and attachment metadata.

Retry policy:

- Type: Exponential
- Count: 4
- Interval: `PT10S`

### `Convert_Email_HTML_To_Text`

Connector: Content Conversion

Action: **Html to text**

Content: Body from `Get_Source_Email_Metadata`.

If the source body is already text, the converted result may equal the source body; both the original body and text representation are sent to the TMS.

### `Select_To_Recipient_Addresses`

Connector: Data Operations

Action: Select

From:

```text
body('Get_Source_Email_Metadata')?['toRecipients']
```

Switch the Select mapping to text mode and use:

```text
item()?['emailAddress']?['address']
```

### `Select_CC_Recipient_Addresses`

Same configuration using:

```text
body('Get_Source_Email_Metadata')?['ccRecipients']
```

and:

```text
item()?['emailAddress']?['address']
```

## Scope 2 — ATTACHMENTS

Create scope **`Scope_02_Capture_Source_Attachments`** after Scope 1 succeeds.

### `For_Each_Source_Attachment`

Connector: Control

Action: Apply to each

Input:

```text
coalesce(body('Get_Source_Email_Metadata')?['attachments'], createArray())
```

Concurrency: On, degree `4`.

Inside the loop:

### `Is_Inline_Attachment`

Condition:

```text
equals(coalesce(items('For_Each_Source_Attachment')?['isInline'], false), true)
```

#### If Yes — inline evidence only

Do not download logo/signature image bytes. Append metadata to `varAttachments` with `contentBase64 = null`.

Action name: **`Append_Inline_Attachment_Evidence`**

Object:

```json
{
  "attachmentId": "<current attachment id>",
  "name": "<current attachment name>",
  "contentType": "<current content type>",
  "contentBase64": null,
  "isInline": true,
  "sizeBytes": "<current size>"
}
```

#### If No — retrieve original file

### `Get_Source_Attachment_Content`

Connector: Microsoft 365 Outlook

Action: **Get Attachment (V2)**

- Message Id: `body('Get_Source_Email_Metadata')?['id']`
- Attachment Id: `items('For_Each_Source_Attachment')?['id']`
- Original Mailbox Address: `info@lyonshaulage.com`

Retry:

- Exponential
- Count 4
- Interval `PT10S`

Secure Inputs/Outputs: On.

### `Append_Source_Attachment_Evidence`

Append to `varAttachments`:

```json
{
  "attachmentId": "<current attachment id>",
  "name": "<current attachment name>",
  "contentType": "<current content type>",
  "contentBase64": "<Content Bytes from Get Source Attachment Content>",
  "isInline": false,
  "sizeBytes": "<current size>"
}
```

Power Automate normally supplies `Content Bytes` as the base64 value required by the custom connector; do not wrap it in another `base64()` call unless inspection of a test run proves the designer returned binary rather than the base64 string.

## Scope 3 — TRANSFORM

Create **`Scope_03_Build_TMS_Intake_Envelope`**.

### `Build_TMS_Mailbox_Envelope`

Connector: Data Operations

Action: Compose

Use this object, inserting dynamic content from `Get_Source_Email_Metadata`:

```json
{
  "messageId": "<Outlook id>",
  "internetMessageId": "<internetMessageId>",
  "conversationId": "<conversationId>",
  "mailbox": "info@lyonshaulage.com",
  "senderAddress": "<from.emailAddress.address>",
  "senderName": "<from.emailAddress.name>",
  "toRecipients": "<outputs of Select_To_Recipient_Addresses>",
  "ccRecipients": "<outputs of Select_CC_Recipient_Addresses>",
  "subject": "<subject>",
  "receivedAtUtc": "<receivedDateTime>",
  "bodyText": "<Converted Email HTML To Text output>",
  "bodyHtml": "<original Body content>",
  "bodyFormat": "<Body content type or html/text>",
  "importance": "<importance>",
  "webLink": "<webLink>",
  "attachmentCount": "<length of source attachment metadata array>",
  "correlationId": "<varCorrelationId>",
  "flowRunId": "<workflow run name>",
  "attachments": "<varAttachments>"
}
```

Recommended expressions where dynamic content is awkward:

Message ID:

```text
body('Get_Source_Email_Metadata')?['id']
```

Internet Message ID:

```text
body('Get_Source_Email_Metadata')?['internetMessageId']
```

Conversation ID:

```text
body('Get_Source_Email_Metadata')?['conversationId']
```

Sender address:

```text
body('Get_Source_Email_Metadata')?['from']?['emailAddress']?['address']
```

Sender name:

```text
body('Get_Source_Email_Metadata')?['from']?['emailAddress']?['name']
```

Received UTC:

```text
body('Get_Source_Email_Metadata')?['receivedDateTime']
```

Attachment count:

```text
length(coalesce(body('Get_Source_Email_Metadata')?['attachments'], createArray()))
```

Correlation ID:

```text
variables('varCorrelationId')
```

Flow run ID:

```text
workflow()?['run']?['name']
```

Attachments:

```text
variables('varAttachments')
```

Do not build a customer-specific JSON payload in Power Automate. The whole source envelope goes to the TMS parser pipeline. This prevents the flow becoming coupled to one customer's spreadsheet layout.

## Scope 4 — SUBMIT

Create **`Scope_04_Submit_To_TMS_Staging`**.

### `POST_Info_Email_To_TMS_Staging`

Connector: **SLH TMS API** custom connector

Action: **IntakeInfoMailboxEmail**

Backend route:

`POST /api/v1/order-intake/email`

Body: fields from `Build_TMS_Mailbox_Envelope`.

Do not call `SubmitStagedImport` per extracted row; the mailbox endpoint already splits multiple orders/attachments, validates them, applies duplicate/amendment logic and stages each row independently.

Retry policy:

- Type: Exponential
- Count: `4`
- Interval: `PT20S`
- Do not create a Do Until retry loop.

Retries are safe because the backend uses Outlook Message ID + source-row keys for idempotency.

Secure Inputs/Outputs: On.

## Scope 5 — AUDIT RESULT

Create **`Scope_05_Assess_TMS_Result`** after Scope 4 succeeds.

### `Parse_TMS_Intake_Result`

Connector: Data Operations

Action: Parse JSON

Schema:

```json
{
  "type": "object",
  "properties": {
    "ignored": { "type": "boolean" },
    "staged": { "type": "integer" },
    "existing": { "type": "integer" },
    "exactDuplicates": { "type": "integer" },
    "superseded": { "type": "integer" },
    "failed": { "type": "integer" },
    "warnings": { "type": "array", "items": { "type": "string" } },
    "records": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "stagingId": { "type": ["string", "null"] },
          "sourceKey": { "type": ["string", "null"] },
          "status": { "type": "string" },
          "existing": { "type": ["boolean", "null"] },
          "duplicateClassification": { "type": ["string", "null"] },
          "duplicateOf": { "type": ["string", "null"] },
          "plannerReady": { "type": ["boolean", "null"] },
          "intakeStatus": { "type": ["string", "null"] },
          "validationStatus": { "type": ["string", "null"] },
          "pallets": { "type": ["number", "null"] },
          "warnings": { "type": ["array", "null"], "items": { "type": "string" } },
          "reviewUrl": { "type": ["string", "null"] },
          "error": { "type": ["string", "null"] },
          "correlationId": { "type": ["string", "null"] }
        }
      }
    }
  }
}
```

### `Any_Order_Row_Failed`

Condition:

```text
greater(coalesce(body('Parse_TMS_Intake_Result')?['failed'], 0), 0)
```

If Yes, terminate the flow as **Failed** with:

```text
concat('TMS mailbox intake partially failed. Correlation ', variables('varCorrelationId'), '. Source email remains in Info mailbox; successful sibling orders were staged independently.')
```

This makes the failure visible in Power Automate while preserving successful rows and the untouched source email. Re-running the flow is safe.

If No, finish successfully. Do not alter the source email.

## Scope 6 — ERROR HANDLER

Create **`Scope_99_Handle_Import_Exception`**.

Configure **Run After** for Scopes 1–5:

- has failed
- has timed out
- is skipped where caused by an upstream failure

Inside:

### `Compose_Import_Failure_Context`

Inputs:

```json
{
  "correlationId": "<varCorrelationId>",
  "messageId": "<trigger message id>",
  "mailbox": "info@lyonshaulage.com",
  "subject": "<trigger subject>",
  "flowRunId": "<workflow run name>"
}
```

Secure Outputs: On.

### `Terminate_Import_Failed`

Status: Failed

Message:

```text
concat('SLH TMS Info mailbox intake failed. Correlation ', variables('varCorrelationId'), '. The source email has not been modified. Re-run is idempotent.')
```

Do not send raw attachment content, bearer tokens or complete source bodies in an error notification.

## Backend behaviour called by the flow

The mailbox endpoint owns extraction and data integrity:

1. NWF pallet-order CSV parser.
2. NWF workbook snapshot parser.
3. NWF tracker parser.
4. Known customer body parsers (including HHP/Waitrose and Waitrose multi-depot pallet-count emails).
5. Sainsbury haulier plan parser.
6. Other specialist mailbox parsers.
7. Generic CSV parser.
8. Generic Excel/XLS/XLSX/XLSM and body parser.
9. If an order-looking email/PDF cannot be confidently parsed, create a Pending Review exception record with `plannerReady=false`; never guess missing fields.

Every extracted order is enriched before staging with:

- original Outlook IDs, sender, recipients, subject, received time and web link;
- original attachment ID/name/type/size and SHA-256 where bytes are available;
- deterministic import batch ID;
- parser/mapping-template name;
- extraction confidence;
- structured validation issues (`Critical`, `Warning`, `Information` model);
- TMS master-data match IDs/names where an exact safe match exists;
- duplicate/amendment match keys;
- business fingerprint;
- `reviewStatus = Pending Review`;
- `plannerReady` flag.

## Duplicate and amendment rules

Same Outlook Message ID/source row:

- Return existing staging record.
- No second record and no second live order.

Different email with same strong customer PO/reference and identical business fingerprint:

- Classify `Exact duplicate`.
- Retain the new source evidence in a Rejected staging row.
- Do not create/promote a second live order.

Same strong PO/reference but changed business values:

- Classify `Amendment/update`.
- New version remains Pending Review.
- Older pending version is automatically Rejected as superseded; its evidence remains.
- On planner approval, the existing live order is updated instead of silently ignoring the amendment.

Weak route/date/location match without a strong PO/reference:

- Classify `Possible duplicate`.
- Keep Pending Review; do not auto-suppress it.

No relation:

- Classify `New order`.

## Pallet-control rule

The parser/enrichment layer treats pallet quantity as an explicit operational field.

- A parsed source pallet value must be present as numeric `pallets` in staged JSON.
- If the email/attachment refers to pallets but `pallets` is absent after extraction, add validation code `PALLET_EXTRACTION_FAILED` and keep the order in review.
- Invalid/zero negative quantities are critical and cannot be approved into live planning.
- Approval copies the reviewed `pallets` value into the live `TransportOrder`, including approved amendments.

## Planner review and approval

No Power Automate approval is required. Planner control remains in the TMS **Order Review** page.

Pending Review records show the extracted payload, warnings and original Outlook source link. The planner can amend the staged payload before approval.

- Approve: TMS validates `plannerReady`, promotes/updates the live order, then the order becomes available to Planning.
- Reject: status becomes Rejected; source evidence and review reason remain.
- Critical exception / `plannerReady=false`: approval is blocked until corrected.

## PDF handling

PDF source files are always retained by Outlook ID/attachment ID/name and sent to the TMS intake envelope. The current backend does not guess values from arbitrary PDF layouts. If an order-looking PDF cannot be confidently extracted by an existing parser, the TMS creates a Pending Review exception rather than discarding it.

If SLH later enables AI Builder or Azure Document Intelligence for PDF text/table extraction, add it as a preprocessing branch that supplies extracted text to the same mailbox endpoint; do not create a separate Orders route.

## Required production tests

For every test confirm: source untouched, endpoint called once per flow execution, extracted orders land Pending Review, pallet/date/site values survive, no live order exists until TMS approval.

1. Normal Excel attachment.
2. Body-only HHP/Waitrose order.
3. Multi-order body table (Waitrose depot pallet counts).
4. Multiple attachments.
5. Missing PO -> warning/review.
6. Unknown customer -> warning/review.
7. Unknown delivery site -> warning/review.
8. Same Message ID replay -> existing/idempotent.
9. Same PO resent unchanged -> Exact duplicate, evidence retained, no second live order.
10. Same PO changed pallets/date -> Amendment/update Pending Review; approval updates live order.
11. Invalid date -> Critical, approval blocked.
12. Wave 1 order -> retained Pending Review until planner accepts.
13. Valid unmatched/Wave 3 order -> retained; not auto-allocated or discarded.
14. API 429/5xx/transient network failure -> exponential retries; source untouched; safe manual rerun.
15. Attachment extraction failure/unsupported PDF -> Pending Review exception when the message looks order-related.
16. Pallet-specific regression -> source containing pallet count must show the identical reviewed pallet count after approval in Orders/Pallet Control.

## Go-live checklist

- Re-import/update the existing custom connector from `openapi-power-automate.yaml` version 1.3.
- Replace `REPLACE_TENANT_ID` and `REPLACE_API_CLIENT_ID` in the custom connector configuration with the existing SLH Entra values; do not paste secrets into the flow.
- Create/confirm the SLH-managed Outlook and TMS connector connections.
- Set trigger mailbox to `info@lyonshaulage.com`.
- Confirm trigger Include Attachments = No.
- Enable secure inputs/outputs on attachment/API actions.
- Test the 16 cases above in a controlled flow copy.
- Confirm Order Review sees staged records and source Outlook link.
- Approve a test order and confirm the live order/pallet count; test amendment updates.
- Only then switch the production flow On.
