# Provider integration ownership

## Rule

External provider APIs integrate with the TMS SQL boundary. They do not write directly to Microsoft Lists. Microsoft Lists is the controlled office-maintenance surface for governed master data; SQL remains the operational source used by route building and dispatch.

```text
Fleetio ─┐
Tachomaster ─┼─> TMS API ─> SQL projections ─> routes, runs and dispatch
Sage ───────┘          ▲
                       │
Office staff ─> Microsoft Lists ─> reconciliation API
```

## Provider boundaries

| Provider | Primary data | Direction | Operational rule |
|---|---|---|---|
| Fleetio | Vehicles, fleet identity, maintenance/status references | Provider → TMS API → SQL | Vehicle identity is matched by normalized registration; conflicts remain reviewable. |
| Tachomaster | Driver identity, tachograph and compliance references | Provider → TMS API → SQL | Driver identity is matched by stable employee/Tacho identity; names alone are not sufficient for automatic merges. |
| Sage | Customer/accounting references, invoices and finance status | TMS API ↔ Sage, with SQL audit | Finance state is not stored in Lists as an operational authority. |
| Microsoft Lists | Office-governed drivers, vehicles, trailers, sites, customers, markets and fuel cards | Lists → reconciliation API → SQL | List changes are validated, idempotent and audited before becoming operational. |

## Fuel cards

Fuel-card records are a dedicated vehicle-linked projection keyed by normalized vehicle registration and TMS vehicle ID where known. They must not be mixed into general customer/site exports. Full card and PIN values are restricted to authorized integration paths and are never written to logs, comments or general extracts.

## Conflict and outage behaviour

- SQL keeps the last valid projection when a provider, SharePoint or Power Automate flow is unavailable.
- Provider changes are staged when identity matching is uncertain.
- Route building never waits on a live provider or SharePoint request.
- No provider sync deletes SQL history, active-run references or audit evidence.
- Retirement requires a reviewed decision and preserves historical foreign-key references.

## Acceptance checks before production use

1. Provider authentication and least-privilege scopes are verified.
2. A full initial extract is reconciled against SQL and the planner workbook.
3. Duplicate and conflict counts are zero or explicitly reviewed.
4. A replay of each provider payload is idempotent.
5. A provider outage leaves route building operational from SQL.
6. SharePoint List edits round-trip to SQL with audit evidence.
