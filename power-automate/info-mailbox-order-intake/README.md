# SLH TMS Info mailbox flow

This directory is the source-controlled definition for `SLH-TMS | Info Mailbox | Order Intake | PROD`.

The flow is deliberately stopped in source. Import it into the existing SLH managed solution, bind the two existing connection references, run the acceptance matrix, and enable it only after every test produces `PendingReview` staging records and no live orders.

## Existing components used

- Outlook shared mailbox: `info@lyonshaulage.com`
- TMS custom connector operation: `IntakeInfoMailboxEmail`
- Hosted route: `POST /api/v1/order-intake/email`
- OAuth scope: existing Entra `Tms.Access`
- Review/promotion: existing `/api/v1/staging/{id}/approve` and `/reject`
- Import history: SQL `StagedImportEvents` snapshots plus the source mailbox identifiers
- Live-order trace: SQL `TransportOrders.SourceStagedImportId`

No Microsoft List, SharePoint store, Power Automate Approval, secret, bearer token, client secret or live-order endpoint is present in the definition. The Info mailbox retains the original email/attachments under the organisation's mailbox retention policy; TMS SQL is the authoritative import, review and promotion history.

## Import

1. In Power Apps/Power Automate Solutions, open the existing SLH TMS solution.
2. Add a cloud flow from definition and use `workflow.json` as the source definition. If the tenant requires an exported solution wrapper, create an empty solution-aware flow once, unpack it with `pac solution unpack`, replace only that flow's `properties.definition` and `connectionReferences` with this file, then repack with `pac solution pack`.
3. Bind `slh_sharedoffice365_info` to the existing Microsoft 365 Outlook connection that has delegated access to the Info mailbox.
4. Bind `slh_sharedslhtms_prod` to the existing SLH TMS custom connector OAuth connection.
5. Set `SLH_InfoMailboxUPN` to `info@lyonshaulage.com`.
6. Confirm secure inputs/outputs remain enabled on attachment retrieval and TMS submission.
7. Confirm database script `031_Order_Import_Audit_History.sql` has applied successfully.
8. Save with the flow stopped. Run the tests in the production runbook, then enable it.

Power Automate may rewrite connector-internal token names on first save. Use designer dynamic content for Message Id, Internet Message Id, Conversation Id, From, From Name, To, CC, Subject, Received Time, Body, Body Preview, Importance and Web Link if the imported tenant connector exposes a different internal token. Do not change the semantic request field names.

## Validation

```bash
python power-automate/info-mailbox-order-intake/validate_workflow.py
python power-automate/info-mailbox-order-intake/test_validate_workflow.py
```

The full action-by-action deployment, duplicate, exception, security and acceptance-test specification is in `docs/PowerAutomate-InfoMailbox-Order-Intake-Production.md`.
