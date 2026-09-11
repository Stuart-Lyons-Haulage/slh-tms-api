# TMS ↔ SQL ↔ Microsoft Lists master-data operating model

## Authority and direction

SQL is the operational authority. The TMS reads master data from SQL when orders, routes, runs, timings and allocations are built. Route construction must never depend on a live Microsoft Lists request.

Microsoft Lists is the office-maintained governance surface. Staff maintain proposed changes there; the TMS reconciliation boundary validates and applies approved changes into SQL, recording the original List item identity, business key, before/after values, operator/source and outcome.

The effective flow is:

```text
Office staff → Microsoft Lists → Power Automate/API reconciliation → SQL audit + projection → TMS route/planning reads
```

There is no direct Lists-to-route path and no destructive synchronisation.

## Required record identity

Every List row must carry:

- `EntityType`
- `SharePointItemId`
- `BusinessKey`
- `TmsId` when already known
- `ChangeAction` (`Create`, `Update`, `Review`, or `Retire`)
- `SourceModifiedUtc`
- `SyncStatus` and `SyncMessage`

The API uses deterministic idempotency keys based on entity type, List item ID and source version. Replays are safe and do not create duplicate operational records.

## Initial load and workbook comparison

The initial load must be built from the TMS SQL/API extract, then compared with `Up tO date Master.xlsm`. The workbook is planner evidence and correction input, not a replacement authority. Differences are classified as:

`Keep`, `Update`, `Create`, `DuplicateReview`, `RetireReview`, or `Conflict`.

Site numbers are allocated only after matching existing SQL site identities. Duplicate or uncertain records remain visible for review; they are not purged automatically.

## Route-build invariant

At route-build time the TMS reads the SQL projection, including the reconciled site/customer/driver/vehicle/trailer and timing data. If Lists or Power Automate is unavailable, route building continues from the last valid SQL projection and exposes freshness/audit status to staff.

## Cleanup gate

Cleanup requires a completed comparison report, stable identity mapping, retained historical references, and an explicit reviewed deletion/retirement action. Orders, runs, movements, audit records and source evidence are never removed as part of master-data cleanup.
