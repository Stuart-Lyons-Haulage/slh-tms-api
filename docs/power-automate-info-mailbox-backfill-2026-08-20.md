# Go-live backfill — Info mailbox from 20 August 2026

The production new-mail trigger only sees messages arriving after the flow is enabled. To honour the initial go-live requirement, run this one-off backfill after the production flow has been tested and before treating the queue as complete.

The backfill MUST call the same `IntakeInfoMailboxEmail` custom-connector action as the live flow. Do not create a second extraction/mapping implementation.

## Temporary flow

Name: **SLH TMS - Info Mailbox Backfill - 2026-08-20**

Trigger: **Manually trigger a flow**.

### `Get_Info_Mailbox_Emails_For_Backfill`

Connector: Microsoft 365 Outlook

Action: **Get emails (V3)**

- Folder: Inbox
- Original Mailbox Address: `info@lyonshaulage.com`
- Fetch Only Unread: No
- Include Attachments: No
- Top: use the largest practical page size available in the environment and enable pagination if the action exposes it.
- Search Query: if the tenant's Outlook connector accepts AQS received-date search, restrict it to 20 August 2026. Otherwise retrieve the Inbox page(s) and use the Filter Array below.

### `Filter_To_20_August_From_Midnight_BST`

Connector: Data Operations

Action: Filter array

From: messages returned by `Get_Info_Mailbox_Emails_For_Backfill`.

Advanced-mode expression:

```text
@and(
  greaterOrEquals(ticks(item()?['receivedDateTime']), ticks('2026-08-19T23:00:00Z')),
  less(ticks(item()?['receivedDateTime']), ticks('2026-08-20T23:00:00Z'))
)
```

20 August 2026 is in British Summer Time, therefore local 00:00–24:00 is 19 August 23:00Z to 20 August 23:00Z.

### `For_Each_Backfill_Email`

Apply to each message from the filtered array.

Concurrency: On; degree `3`.

For each message, execute the same source-email processor used by the live flow:

1. `Get_Source_Email_Metadata` — Get email (V2), Original Mailbox Address = Info.
2. `Convert_Email_HTML_To_Text`.
3. Capture To/CC recipients.
4. Enumerate attachments.
5. Get non-inline attachment bytes using Get Attachment (V2).
6. Build the same `MailboxEmailIntakeRequest` envelope.
7. Call custom connector `IntakeInfoMailboxEmail`.
8. Assess `failed` rows and surface any partial failure.

Do not alter, move, mark read or categorise any message.

## Safe replay

It is safe to run this backfill more than once:

- same Outlook Message ID + source row returns the existing staging record;
- a resent order on another email is checked by PO/reference, route/date/location and business fingerprint;
- exact duplicates retain the second source as duplicate evidence without creating a second live order;
- amended content remains Pending Review and supersedes only an older pending version where a strong reference proves the relationship.

## Acceptance check after the backfill

In TMS Order Review:

- compare the 20 August Info mailbox against staged source links;
- confirm the HHP Waitrose email (`A58971`, 5 pallets, 196 cases) is represented;
- confirm the Waitrose pallet-count email creates four rows (Aylesford 3, Bracknell 4, Brinklow 3, Leyland 1), with missing collection information held for review rather than invented;
- confirm attachment-based orders such as the Aldi XLSM are either parsed into Pending Review orders or shown as extraction exceptions;
- confirm no backfilled order became live without planner approval;
- confirm pallet counts in staging exactly match the source before approval.

After the backfill is reconciled, turn the temporary flow Off. Keep it in the Solution for controlled replay/recovery, or remove it once the production team is satisfied the live trigger is operating reliably.
