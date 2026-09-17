# Pallet Control Matrix Fix

Operational rule restored:

- The pallet matrix rows are collection sites.
- The pallet matrix columns are delivery points.
- AM/PM/overnight is planning metadata only and must not replace the collection-site matrix rows.
- Orders to Plan should group cards under AM/PM headings and display cards as: Collect, pallet count, Deliver.

Implementation notes:

- `PalletPlanningControlController` should expose `planningGroup` as the collection site and `destination` as the delivery point.
- `planningSection`, `planningWindow`, `runsOvernight` and related fields remain on each order row for grouping/filtering in the UI.
- Allocation/idempotency behaviour should remain keyed by order/load/source line so amended quantities update the existing order instead of creating duplicate planning demand.
