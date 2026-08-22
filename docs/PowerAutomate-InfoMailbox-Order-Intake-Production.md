# SLH TMS — Production Info Mailbox Order Intake

## Purpose

This is the production build standard for the Stuart Lyons Haulage Info mailbox order-intake flow.

The control path is deliberately:

`Info shared mailbox -> Power Automate -> source evidence -> TMS email preview parser -> validation/master matching -> duplicate classification -> TMS staging -> planner review -> approve/reject -> live orders -> planning board`

Power Automate is the orchestration layer. The TMS API/database remains the system of record. No mailbox flow is permitted to create a live order directly.

## Existing TMS components reused

Do not create replacement ingestion services.

- `POST /api/v1/order-intake/email/preview` — parses a complete email but does not create staging records.
- `POST /api/v1/order-intake/duplicate-check` — PO-first read-only classification of the candidate against staged/live orders.
- `POST /api/v1/staging` — creates one durable `PendingReview` staging record using a deterministic idempotency key.
- `GET /api/v1/staging` — review/status queries.
- `PUT /api/v1/staging/{id}/payload` — planner correction of a pending staged order.
- `POST /api/v1/staging/{id}/approve` — approval and promotion to live order.
- `POST /api/v1/staging/{id}/reject` — rejection while retaining evidence/status.
- Existing SLH TMS custom connector — OAuth/Entra `Tms.Access`; never store a bearer token or client secret inside the flow.

`POST /api/v1/order-intake/email` remains available as the simple server-side intake route. The production flow below deliberately uses `preview -> duplicate check -> /staging` so the flow can attach durable SharePoint evidence, validation outcomes and duplicate classification to every staged payload before review.

## Environment values

Store these as Solution Environment Variables, not Compose constants.

- `SLH_InfoMailboxUPN` — exact shared mailbox address.
- `SLH_TMS_Evidence_Site` — SharePoint site connection/reference.
- `SLH_TMS_Evidence_Root` — controlled folder, recommended `Transport Operations System/04 TMS Build/Automation/Order Intake Evidence` unless Operations chooses a separate production records library.
- `SLH_TMS_API_Base` — existing production API base URL.
- `SLH_TMS_Review_Portal` — TMS portal route used by planners to open Order Review.
- `SLH_TMS_Admin_Recipients` — operational exception recipients/group.

Use connection references for:

- Microsoft 365 Outlook
- SharePoint
- SLH TMS custom connector
- Content Conversion
- AI Builder only if the tenant has an approved document-text extraction entitlement

## Flow identity and settings

Flow name: `SLH-TMS | Info Mailbox | Order Intake | PROD`

Owner: production automation/service account plus at least one named SLH administrator.

Trigger concurrency: 4. The TMS uses deterministic idempotency; controlled parallelism prevents a large customer batch blocking all other email while avoiding an unnecessary burst against Outlook/API.

Default API retry: exponential, 4 attempts, minimum interval `PT10S`, maximum interval `PT2M`.

Never enable an unbounded Do Until retry loop.

---

# Action sequence

## Trigger — `When_New_Email_Arrives_Info_Shared_Mailbox`

Connector: Microsoft 365 Outlook

Action: **When a new email arrives in a shared mailbox (V2)**

Configuration:

- Original Mailbox Address: environment variable `SLH_InfoMailboxUPN`
- Folder: Inbox
- Only with Attachments: No
- Include Attachments: No — retrieve files individually to avoid large trigger payload failures
- Importance: Any

Do not move, mark read, delete, categorise or otherwise modify the source email.

Capture from the trigger/dynamic content:

- Outlook Message ID
- Internet Message ID
- Conversation ID
- From address/name
- To recipients
- CC recipients
- Subject
- Received Date Time
- Body
- Body content type
- Importance
- Has Attachments
- Web Link where exposed

### Trigger settings

Concurrency Control: On

Degree of Parallelism: `4`

Split On: leave at connector default.

---

# Scope 1 — `Scope_Receive_Source`

Run after: trigger success.

## `Initialise_Correlation_Id`

Action: Initialize variable

- Name: `varCorrelationId`
- Type: String
- Value expression:

```text
guid()
```

## `Initialise_Import_Batch_Id`

Type: String

```text
concat(
  formatDateTime(triggerOutputs()?['body/receivedDateTime'],'yyyyMMddHHmmss'),
  '-',
  substring(variables('varCorrelationId'),0,8)
)
```

If the trigger exposes Received Time under a different dynamic-content token, insert the token through the designer rather than hard-coding its connector-internal path.

## `Initialise_Attachment_Array`

Type: Array

```json
[]
```

Variable: `varAttachments`

## `Initialise_Attachment_References`

Type: Array, value `[]`

Variable: `varAttachmentReferences`

## `Initialise_Extracted_Attachment_Text`

Type: String, value empty.

Variable: `varExtractedAttachmentText`

## `Initialise_Validation_Issues`

Type: Array, value `[]`

Variable: `varValidationIssues`

## `Initialise_Staging_Results`

Type: Array, value `[]`

Variable: `varStagingResults`

## `Compose_Message_Key`

Prefer Internet Message ID, fall back to Outlook Message ID.

```text
coalesce(
  triggerOutputs()?['body/internetMessageId'],
  triggerOutputs()?['body/id']
)
```

Use designer dynamic content for these fields if connector token names differ.

## `Compose_Evidence_Relative_Folder`

```text
concat(
  variables('SLH_TMS_Evidence_Root'),
  '/',
  formatDateTime(triggerOutputs()?['body/receivedDateTime'],'yyyy'),
  '/',
  formatDateTime(triggerOutputs()?['body/receivedDateTime'],'MM'),
  '/',
  variables('varImportBatchId')
)
```

## `Create_Evidence_Folder`

Connector: SharePoint

Action: Create new folder

Site: `SLH_TMS_Evidence_Site`

Folder Path: output from `Compose_Evidence_Relative_Folder`

If a retry finds that the folder already exists, treat that as success and continue. Do not generate a second evidence folder for the same flow run retry.

## `Compose_Source_Metadata_JSON`

Action: Compose

Use this object. Use the trigger's dynamic content for recipient arrays rather than converting them to a lossy string when possible.

```json
{
  "correlationId": "<varCorrelationId>",
  "importBatchId": "<varImportBatchId>",
  "sourceMailbox": "<SLH_InfoMailboxUPN>",
  "outlookMessageId": "<Message Id>",
  "internetMessageId": "<Internet Message Id>",
  "conversationId": "<Conversation Id>",
  "senderAddress": "<From address>",
  "senderName": "<From name>",
  "toRecipients": "<To recipients>",
  "ccRecipients": "<CC recipients>",
  "subject": "<Subject>",
  "receivedAtUtc": "<Received date/time>",
  "bodyFormat": "<Body content type>",
  "importance": "<Importance>",
  "hasAttachments": "<Has attachments>",
  "webLink": "<Web link>",
  "capturedAtUtc": "@{utcNow()}"
}
```

Do **not** put attachment base64 into this audit object.

## `Save_Source_Metadata_JSON`

Connector: SharePoint — Create file

File name: `source-message.json`

Content:

```text
string(outputs('Compose_Source_Metadata_JSON'))
```

## `Export_Source_Email_EML`

Connector: Microsoft 365 Outlook

Action: Export email (V2), where available for the shared-mailbox connection.

Message Id: trigger Message Id.

If the action accepts Original Mailbox Address, use `SLH_InfoMailboxUPN`.

Run-after behaviour: this action is desirable evidence but is **not allowed to fail the whole order intake**. If delegated EML export is not supported by the tenant/connector, record a Warning and rely on `source-message.json`, Outlook IDs/WebLink and retained attachments.

## `Save_Source_Email_EML`

SharePoint Create file, name `source-email.eml`, using output of `Export_Source_Email_EML`.

Run After: Export succeeded only.

---

# Scope 2 — `Scope_Extract_Attachments`

Run after: `Scope_Receive_Source` succeeded or completed with only the non-blocking EML-export warning.

## `Condition_Has_Attachments`

Use Has Attachments from the trigger.

If false: skip to body conversion.

If true:

## `List_Source_Attachments`

Microsoft 365 Outlook — Get/List attachments for the source shared-mailbox message.

Use Message Id and Original Mailbox Address = `SLH_InfoMailboxUPN`.

## `For_Each_Source_Attachment`

Apply to each attachment returned above.

Concurrency: `1` inside an email. This keeps attachment evidence and appended text deterministic.

### `Condition_Skip_Inline_Attachment`

If Is Inline = true, do not send it to the TMS parser unless it is a genuine order document. Normal signature images are evidence only.

### `Get_Source_Attachment_Content`

Microsoft 365 Outlook — Get attachment (V2)

- Message Id: source message
- Attachment Id: current attachment
- Original Mailbox Address: `SLH_InfoMailboxUPN`

### `Save_Original_Attachment`

SharePoint — Create file

Path: evidence folder

Name: original attachment name. If SharePoint rejects illegal characters, use a sanitised evidence filename but retain the original name in metadata.

Content: attachment binary returned by Outlook.

### `Append_Attachment_Reference`

Append to array variable `varAttachmentReferences`:

```json
{
  "name": "<original attachment name>",
  "contentType": "<content type>",
  "sharePointReference": "<Create file item/web link if returned>",
  "isInline": false
}
```

### `Append_TMS_Attachment_Object`

Append to `varAttachments`:

```json
{
  "name": "<original attachment name>",
  "contentType": "<content type>",
  "contentBase64": "<Get attachment content $content/base64>",
  "isInline": false
}
```

Mark **Secure Inputs** and **Secure Outputs** on `Get_Source_Attachment_Content` and this append action so file contents do not appear in normal run history.

### `Switch_Attachment_Extension`

Normalise extension with:

```text
toLower(last(split(items('For_Each_Source_Attachment')?['name'],'.')))
```

Branches:

#### `xls`, `xlsx`, `xlsm`

No transformation. Pass the original base64 to TMS. The existing TMS parser reads workbook sheets/rows and can return multiple orders from one attachment.

#### `csv`

Keep original binary in `varAttachments`.

Additionally, decode textual CSV only for search/classification evidence:

```text
base64ToString(body('Get_Source_Attachment_Content')?['$content'])
```

Append to `varExtractedAttachmentText` with a marker containing the attachment name.

The TMS already has a dedicated NWF pallet-order CSV parser. Other CSV layouts that cannot be confidently mapped must become review exceptions rather than guessed orders.

#### `pdf`

If approved AI Builder document-text extraction is available:

Action name: `Extract_PDF_Text_AI_Builder`

Extract readable text and append to `varExtractedAttachmentText` between clear markers:

```text
--- ATTACHMENT TEXT: <filename> ---
<text>
--- END ATTACHMENT TEXT ---
```

If AI Builder is unavailable or extraction fails:

- original PDF remains saved;
- append a Warning object to `varValidationIssues` with code `AttachmentExtractionFailed` or `UnsupportedAttachment`;
- continue processing other attachments;
- do not discard the email.

#### `txt`, `htm`, `html`

Decode text and append to `varExtractedAttachmentText`.

#### Other extensions

Save as evidence, append Warning `UnsupportedAttachment`, and continue.

No single attachment failure is allowed to prevent other valid attachments/orders from being processed.

---

# Scope 3 — `Scope_Transform_Source`

## `Convert_HTML_Body_To_Text`

Connector: Content Conversion

Action: HTML to text

Input: source email body when body format is HTML.

For plain-text messages, use original body.

## `Compose_Combined_Body_Text`

```text
concat(
  outputs('Convert_HTML_Body_To_Text'),
  if(
    empty(variables('varExtractedAttachmentText')),
    '',
    concat('\n\n',variables('varExtractedAttachmentText'))
  )
)
```

This allows PDF/text/HTML attachment text to participate in the same modular server-side parsing without creating customer-specific branches in Power Automate.

## `Build_TMS_Email_Preview_Request`

The request must match the existing backend DTO exactly:

```json
{
  "messageId": "<Outlook Message Id>",
  "internetMessageId": "<Internet Message Id>",
  "mailbox": "<SLH_InfoMailboxUPN>",
  "senderAddress": "<From address>",
  "senderName": "<From name>",
  "subject": "<Subject>",
  "receivedAtUtc": "<Received date/time>",
  "bodyText": "<Compose_Combined_Body_Text>",
  "bodyHtml": "<original HTML body or null>",
  "webLink": "<Outlook Web Link or null>",
  "attachments": "<varAttachments>"
}
```

Do not invent null fields. Preserve null when the source genuinely does not contain a value.

## `Preview_Info_Mailbox_Email`

Connector: existing `SLH TMS API` custom connector

Operation: `PreviewInfoMailboxEmail`

Backend: `POST /api/v1/order-intake/email/preview`

Body: `Build_TMS_Email_Preview_Request`

Retry: exponential, 4 attempts.

Secure Inputs/Outputs: enabled because request includes attachment content.

## `Parse_TMS_Preview_Response`

Data Operations — Parse JSON

Schema:

```json
{
  "type": "object",
  "properties": {
    "ignored": { "type": "boolean" },
    "ignoredReason": { "type": ["string", "null"] },
    "warnings": {
      "type": "array",
      "items": { "type": "string" }
    },
    "orderCount": { "type": "integer" },
    "orders": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "sourceKey": { "type": "string" },
          "naturalKey": { "type": "string" },
          "payload": { "type": "object" },
          "warnings": { "type": "array", "items": { "type": "string" } }
        },
        "required": ["sourceKey", "payload", "warnings"]
      }
    }
  },
  "required": ["ignored", "warnings", "orderCount", "orders"]
}
```

---

# Scope 4 — `Scope_Determine_Order_Intent`

## `Condition_Parser_Found_Orders`

Expression:

```text
greater(body('Parse_TMS_Preview_Response')?['orderCount'],0)
```

### Yes

Continue to per-order validation.

### No

Do **not** automatically throw the email away.

## `Compose_Order_Likelihood_Text`

Combine subject, normalised body and attachment names.

## `Condition_Likely_Order_But_Unmapped`

Treat as likely order where one or more strong indicators are present, for example:

- known customer/supplier name/domain;
- `PO`, `purchase order`, `order`, `booking`, `collection`, `delivery`, `pallet`, `cases`;
- supported order-style attachment names/extensions;
- parser warning indicates recognised format/extraction failure.

Internal SLH planner-output emails and clearly operational correspondence already identified by the TMS parser may be audited as `NotOrder` without staging.

For uncertain correspondence, prefer **Pending Review** over discard.

### `Stage_Unmapped_Order_Exception`

Use existing custom connector operation `SubmitStagedImport` -> `POST /api/v1/staging`.

Payload:

```json
{
  "entityType": "order",
  "idempotencyKey": "<deterministic email exception key>",
  "source": "PowerAutomate/InfoMailbox/Unmapped",
  "payload": {
    "poNumber": "<stable EMAIL-... reference>",
    "customerCode": "UNMAPPED",
    "collectionDate": null,
    "deliveryDate": null,
    "pallets": null,
    "plannerReady": false,
    "intakeStatus": "Exception",
    "reviewStatus": "Pending Review",
    "validationStatus": "Critical",
    "intakeConfidence": "Low",
    "intakeWarnings": ["Order-like email could not be confidently mapped."],
    "sourceMailbox": "<mailbox>",
    "sourceMessageId": "<message id>",
    "sourceInternetMessageId": "<internet message id>",
    "sourceSender": "<sender>",
    "sourceSubject": "<subject>",
    "sourceReceivedAtUtc": "<received>",
    "sourceWebLink": "<web link>",
    "sourceEvidenceReference": "<evidence folder reference>",
    "sourceAttachments": "<varAttachmentReferences>",
    "importBatchId": "<varImportBatchId>",
    "correlationId": "<varCorrelationId>"
  }
}
```

Note: this is intentionally not approvable until a planner corrects the missing mandatory order fields. It remains visible and auditable instead of being discarded.

---

# Scope 5 — `Scope_Validate_And_Stage_Orders`

## `For_Each_Parsed_Order`

Input:

```text
body('Parse_TMS_Preview_Response')?['orders']
```

Concurrency: 1. Individual order failures are handled inside the loop and do not terminate sibling orders.

### `Compose_Parsed_Order_Payload`

```text
items('For_Each_Parsed_Order')?['payload']
```

### `Validate_Core_Order_Fields`

Record issues; do not guess values.

Critical examples:

- no meaningful customer identity;
- invalid/missing collection date where route cannot be determined;
- no collection and no delivery identity;
- parsed positive-pallet source line cannot retain a valid pallet count;
- corrupted attachment/parser output.

Warnings:

- missing PO/reference where customer normally supplies one;
- unknown customer/site;
- missing delivery date where operationally recoverable;
- missing pallet count when order is otherwise identifiable;
- non-standard temperature/trailer information;
- low/medium parser confidence.

Information:

- spelling/alias normalisation;
- source/customer template observations.

### `Condition_Source_Has_Pallet_Quantity_But_Payload_Missing`

This is a mandatory pallet continuity guard.

If the parser identifies an order from a workbook/CSV row containing pallets, `payload.pallets` must be populated. Known NWF CSV also retains `palletQty`.

If source evidence contains an explicit pallet quantity but `payload.pallets` is null/empty:

- append `PalletMappingFailure` Critical/Warning depending on whether the order can otherwise be identified;
- keep the record Pending Review;
- never silently continue to live planning.

### `Master_Match_Customer`

Custom connector: `GetCustomers`

Query `q`: candidate `customerCode`/customer.

Rules:

- one strong exact/alias match -> validation Information `CustomerMatched`;
- zero -> Warning `UnknownCustomer`;
- multiple -> Warning `AmbiguousCustomer`.

Do not create new customer master records from the mailbox flow.

### `Master_Match_Collection_Site`

Custom connector: `GetSites`

Query: candidate collection/seller site.

Apply case/space/postcode normalisation only for comparison. Keep the raw source value in evidence.

### `Master_Match_Delivery_Site`

Same as above using delivery/stall/destination.

Unknown/ambiguous sites are staged with warnings; never allocate to the nearest-looking site automatically.

### `Check_PO_First_Duplicate_Status`

Backend: `POST /api/v1/order-intake/duplicate-check`

Use the existing TMS OAuth connection. After this API change is deployed, expose this operation in the existing SLH TMS custom connector; until that connector operation is refreshed, use **HTTP with Microsoft Entra ID** connection referencing the same TMS API resource — never an embedded secret/token.

Body mapping:

```json
{
  "customer": "<payload.customerCode/customer>",
  "po": "<payload.customerPo/po/poRef>",
  "purchaseOrder": "<payload.purchaseOrder>",
  "orderReference": "<payload.orderReference/poNumber>",
  "collectionDate": "<payload.collectionDate>",
  "deliveryDate": "<payload.deliveryDate>",
  "collectionLocation": "<payload.collectionLocation/collectionSite/sellerName>",
  "deliveryLocation": "<payload.deliveryLocation/deliverySite/stallNumber>",
  "pallets": "<payload.pallets>",
  "sourceMessageId": "<Message Id>",
  "sourceAttachmentName": "<payload.sourceAttachmentName>"
}
```

Classification handling:

- `Exact duplicate` -> retain this incoming candidate as Pending Review and label `duplicateClassification=Exact duplicate`; planner can reject/compare. Do not delete either record.
- `Possible duplicate` -> Pending Review + Warning.
- `Amendment/update` -> Pending Review + Warning and existing match references; do not overwrite an existing live/planned order automatically.
- `New order` -> normal Pending Review.

### `Build_Enriched_Staging_Payload`

Start with the TMS parser payload and add/set only metadata/control fields. Do not replace extracted business values with guesses.

Required additions:

```json
{
  "sourceMailbox": "<SLH_InfoMailboxUPN>",
  "sourceEvidenceReference": "<SharePoint evidence folder/item reference>",
  "sourceAttachments": "<varAttachmentReferences>",
  "importSource": "PowerAutomate/InfoMailbox",
  "importBatchId": "<varImportBatchId>",
  "importedAt": "<utcNow()>",
  "correlationId": "<varCorrelationId>",
  "reviewStatus": "Pending Review",
  "validationStatus": "<Critical|Warning|Information|Valid>",
  "validationIssues": "<current order issues>",
  "duplicateClassification": "<duplicate-check classification>",
  "duplicateMatches": "<duplicate-check matches>",
  "mappingTemplate": "<payload.intakeParser or parser source>",
  "extractionConfidence": "<payload.intakeConfidence>"
}
```

Power Automate `setProperty()` may be nested to enrich the object. Do not serialise the payload into a JSON string; `/staging` expects a JSON object.

### `Compose_Deterministic_Staging_Key`

Use the source message plus parser `sourceKey` so retries return the same staging record.

```text
substring(
  concat(
    'info-mailbox:',
    outputs('Compose_Message_Key'),
    ':',
    items('For_Each_Parsed_Order')?['sourceKey']
  ),
  0,
  if(
    greater(
      length(concat('info-mailbox:',outputs('Compose_Message_Key'),':',items('For_Each_Parsed_Order')?['sourceKey'])),
      200
    ),
    200,
    length(concat('info-mailbox:',outputs('Compose_Message_Key'),':',items('For_Each_Parsed_Order')?['sourceKey']))
  )
)
```

If message IDs in the tenant make the key too volatile, replace `Compose_Message_Key` with a deterministic hash generated by an approved backend helper; do not use `guid()` for idempotency.

### `POST_Order_To_TMS_Staging`

Existing custom connector operation: `SubmitStagedImport`

```json
{
  "entityType": "order",
  "idempotencyKey": "<Compose_Deterministic_Staging_Key>",
  "source": "PowerAutomate/InfoMailbox",
  "payload": "<Build_Enriched_Staging_Payload object>"
}
```

Expected response: HTTP 202 for new record or HTTP 200 for an existing idempotent submission.

Expected `status`: `PendingReview` unless this exact record has already been reviewed in an earlier run.

Retry: exponential 4 attempts for 408/429/5xx/connectivity.

Do not retry permanent 400 validation failures indefinitely.

### `Validate_Staging_Response`

Confirm:

- `stagingId` present;
- `status` present;
- new automated records are `PendingReview`;
- source evidence/idempotency key recorded in audit.

If status unexpectedly shows Promoted for a genuinely new automated mailbox record, create a Critical import exception immediately; the flow itself must never invoke `/approve`.

### `Append_Staging_Result`

Append staging ID/status/review URL/duplicate classification to `varStagingResults`.

### Per-order `Scope_Order_Error_Handler`

Run After: validation/matching/staging action failed or timed out.

- record which `sourceKey` failed;
- retain source evidence;
- append failure details without secrets/base64;
- if TMS staging API is reachable, stage an `intakeStatus=Exception` record;
- continue to the next parsed order.

---

# Scope 6 — `Scope_Audit_Result`

## `Compose_Final_Import_Audit`

Include:

- correlation ID;
- import batch ID;
- source mailbox/message/internet/conversation IDs;
- sender/subject/received time;
- attachment names/references;
- preview parser warnings;
- validation issues;
- master-match outcomes;
- duplicate classifications;
- API attempts/results;
- staging IDs/statuses/review URLs;
- timestamps;
- outcome: `NoOrder`, `PendingReview`, `Partial`, or `Failed`.

Never include API access tokens, client secrets or attachment base64.

## `Save_Final_Import_Audit_JSON`

SharePoint Create file:

`import-result.json`

Run after staging scope succeeded, failed, skipped or timed out. This audit action should execute on every triggered message.

---

# Scope 7 — `Scope_Error_Handler`

Configure Run After on the top-level processing scopes: failed, timed out.

## `Compose_Safe_Error_Summary`

Use result expressions against the failed scopes, but remove/avoid action bodies containing attachment base64 or authentication details.

## `Save_Failure_Audit`

Write `import-failure.json` to the evidence folder where possible.

## `Notify_TMS_Import_Failure`

Use Outlook/Teams operational notification only for actionable permanent failures, e.g.:

- authentication failure;
- API unavailable after retries;
- source evidence could not be saved;
- all order extraction failed on an order-like email.

Notification contains correlation ID, sender, subject, time and evidence/review link only — no credentials or raw attachment content.

---

# Planner review and promotion

Do **not** use a separate Power Automate Approvals record as the authoritative approval state. The existing TMS staging workflow is the single source of truth.

Planner opens the TMS Order Review/Pending Review screen and compares the staged payload against the source email/evidence.

- Amend: `PUT /api/v1/staging/{id}/payload` while still Pending Review.
- Approve: `POST /api/v1/staging/{id}/approve` -> `StagingService` promotes the order.
- Reject: `POST /api/v1/staging/{id}/reject` -> staged evidence remains retained with reason.
- Promotion failure: staged record becomes `Failed`; it is not silently deleted.

Power Automate must never call the approval endpoint merely because confidence is High.

---

# Standard order schema mapping

The parser payload must retain the existing TMS compatibility keys used by promotion (`poNumber`, `customerCode`, `collectionDate`, `deliveryDate`, `pallets`, `sellerName`, `marketName`, `stallNumber`, `driverInstructions`) and may additionally carry the richer canonical mailbox schema below.

Recommended canonical fields:

```json
{
  "customer_supplier": null,
  "customer": null,
  "supplier": null,
  "job_type": null,
  "PO": null,
  "purchase_order": null,
  "order_reference": null,
  "customer_reference": null,
  "booking_reference": null,
  "collection_date": null,
  "collection_time": null,
  "collection_time_from": null,
  "collection_time_to": null,
  "collection_location": null,
  "collection_site": null,
  "collection_address": null,
  "collection_postcode": null,
  "delivery_date": null,
  "delivery_time": null,
  "delivery_time_from": null,
  "delivery_time_to": null,
  "delivery_location": null,
  "delivery_site": null,
  "delivery_address": null,
  "delivery_postcode": null,
  "pallets": null,
  "cases": null,
  "quantity": null,
  "product": null,
  "temperature": null,
  "temperature_requirement": null,
  "trailer_type": null,
  "trailer_notes": null,
  "load_notes": null,
  "special_instructions": null,
  "deadline": null,
  "priority": null,
  "status": null,
  "planning_date": null,
  "source_mailbox": null,
  "source_sender": null,
  "source_email_subject": null,
  "source_email_received_at": null,
  "source_email_message_id": null,
  "source_attachment_name": null,
  "source_attachment_type": null,
  "source_attachment_reference": null,
  "import_source": "PowerAutomate/InfoMailbox",
  "import_batch_id": null,
  "imported_at": null,
  "extraction_confidence": null,
  "mapping_template": null,
  "validation_status": null,
  "review_status": "Pending Review"
}
```

Absent source values stay null.

---

# Customer format strategy

Power Automate contains **no customer-specific extraction labyrinth**.

The TMS parser chain is modular. Existing server-side handlers include generic body/workbook parsing plus specialist/NWF/Sainsbury formats. Add a new backend parser module/test when a materially new customer template is learned; the mailbox flow remains unchanged.

Known naming signals may include TSBC/COOP, HHP/Waitrose, Aldi, Morrisons, Crosspoint/PCC, IFCO, JS, NWF and later formats.

Recognition stages:

1. sender/domain;
2. subject;
3. attachment filename/type;
4. structured body/attachment content;
5. TMS customer/site master lookup.

Low confidence -> Pending Review, never guessed promotion.

---

# Wave 1 / Wave 3 rule

The mailbox flow must not decide run allocation.

A valid order that is not on the current Wave 1 plan is still staged and retained. It may carry an Information/Warning `UnmatchedCurrentPlan` or `PotentialLaterWave`, but it is never suppressed or forced onto a run.

Wave 1 and Wave 3 orders therefore coexist in staging/live Orders and are selected later by the Planning Board.

---

# Failure matrix

- Outlook trigger/read failure: platform retries; source remains in mailbox.
- Attachment retrieval transient failure: retry; if still failed, save audit/exception and continue sibling attachments where possible.
- Unsupported attachment: retain file, stage/review if order-like.
- Parser returns zero orders on ordinary correspondence: audit `NoOrder`.
- Parser returns zero orders on likely order: stage unmapped Pending Review exception.
- Master match missing/ambiguous: Warning, stage.
- Invalid date/pallet: Warning/Critical, stage for correction; never invent value.
- Duplicate exact/possible/amendment: stage and label; no delete/overwrite.
- TMS 408/429/5xx: exponential retry max 4.
- TMS 400: permanent input issue -> exception/audit, no endless retry.
- TMS 401/403: authentication failure -> no secret logging; notify Admin.
- SharePoint evidence failure: Critical; do not claim a fully traceable import.
- One order fails in multi-order email: continue remaining orders and mark overall result `Partial`.

---

# Security

- Existing SLH TMS custom connector OAuth/Entra connection is mandatory for TMS operations.
- No client secrets/tokens in Compose, variables, emails or SharePoint audit files.
- Secure Inputs/Outputs on attachment-content and TMS request actions carrying base64.
- Power Automate environment connection references owned by the production automation identity, not a planner's personal token.
- Evidence library permissions restricted to appropriate Operations/TMS administrators/planners.
- Source email is never deleted or modified by this flow.

---

# Acceptance tests

For every test confirm: trigger, evidence, parse, master match, pallet continuity, dates/sites, duplicate result, staging ID, `PendingReview`, and no live order before approval.

1. Excel attachment normal order.
2. Email-body order.
3. Multiple orders in one workbook/CSV.
4. Multiple attachments; one failure does not block siblings.
5. Missing PO -> warning/Pending Review.
6. Unknown customer -> warning/Pending Review.
7. Unknown delivery site -> warning/Pending Review.
8. Same email replay -> same deterministic staging IDs/no duplicate staging.
9. Resent order with different message ID -> duplicate classifier catches it.
10. Amendment with same PO but changed pallets/date/site -> `Amendment/update`, no silent overwrite.
11. Invalid date -> staged exception/review.
12. Wave 1 order -> retained, no auto-allocation.
13. Valid unmatched/likely Wave 3 -> retained, no suppression.
14. API transient outage -> bounded retry, idempotent recovery.
15. Attachment extraction failure -> evidence retained, exception visible.
16. Pallet-specific regression: source says 26 pallets -> preview/staging shows exactly 26 -> after approval live order shows exactly 26.

## Go-live gate

Do not enable the production trigger until tests 1–16 pass with representative real-world redacted/source examples and an operator has verified:

- no direct live-order POST exists in the flow;
- no approval endpoint is called by the intake flow;
- same email replay is idempotent;
- cross-message PO duplicate/amendment is visible;
- attachments/evidence can be retrieved;
- pallet counts survive source -> preview -> staging -> approval -> live order;
- rejection preserves staging/evidence.
