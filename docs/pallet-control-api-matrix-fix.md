# Pallet Control API Matrix Shape

The `/api/v1/planning-control/pallets` response must keep the pallet matrix as:

- `planningGroups`: collection sites down the left.
- `destinations`: delivery points across the top.
- `cells`: keyed by collection site and delivery point.

AM/PM/overnight remains on each order row as `planningSection`, `planningWindow` and `runsOvernight` so the UI can group Orders to Plan without changing the matrix axes.
