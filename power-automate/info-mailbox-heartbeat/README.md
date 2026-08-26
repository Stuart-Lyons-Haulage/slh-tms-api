# SLH TMS Info Mailbox Heartbeat

This package adds an independent scheduled health probe for the shared `info@lyonshaulage.com` mailbox.

## What the heartbeat proves

Every 5 minutes the flow:

1. Uses the existing Office 365 Outlook connection to perform a Microsoft Graph `GET` against the shared Inbox and read the newest message timestamp.
2. Only after that mailbox read succeeds, calls the existing SLH TMS custom connector operation `RecordInfoMailboxHeartbeat`.
3. The API upserts one promoted `infomailboxheartbeat` runtime marker. It does not create an order or a growing history row for every ping.

A current heartbeat therefore proves the shared-mailbox Outlook/Graph read path and the TMS API write path both worked on that run. It is intentionally separate from the new-email order-intake trigger, so quiet customer traffic does not look like an outage.

## Health thresholds

The flow runs every 5 minutes. `/api/v1/system-sync/state` reports:

- `current` for a heartbeat no more than 10 minutes old;
- `pending` between 10 and 20 minutes old, allowing one or two delayed runs;
- `stale` after 20 minutes;
- `pending` before the first heartbeat has ever been recorded.

The response also exposes the last heartbeat, the newest Inbox message timestamp observed by the probe, and the most recent Info-mailbox order received by TMS. These timestamps answer different questions and should not be conflated.

## Deployment order

1. Deploy the API change containing `/api/v1/order-intake/email/heartbeat`.
2. Refresh/import `openapi-power-automate.yaml` so the custom connector exposes `RecordInfoMailboxHeartbeat`.
3. Import `workflow.json`, bind `shared_office365` to the Info mailbox Outlook connection and `shared_slhtms` to the production TMS connector.
4. Confirm `SLH_InfoMailboxUPN` is `info@lyonshaulage.com`.
5. Turn the scheduled flow on and confirm the first run succeeds.
6. Check `/api/v1/system-sync/state`: the `Info mailbox` provider should become `current` and show `lastHeartbeatUtc`.

Do not use the timestamp of the last customer order email as the heartbeat. A quiet Inbox is valid; a missing scheduled mailbox probe is the health signal.
