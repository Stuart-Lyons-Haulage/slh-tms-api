# SLH TMS V2

This folder is the clean V2 application. V1 remains untouched and deployable while V2 is built and proven side-by-side.

## Non-negotiable rules

1. Master data is canonical. Orders, planning, tracking and integrations reference master IDs; they do not create their own copies of customers, sites, drivers, vehicles, trailers or markets.
2. One writer per concern. A capability has one canonical service and one canonical persistence path.
3. Integrations adapt into the domain. TachoMaster, Sage HR, RoadTech, Outlook and Roadrunner never own core TMS entities.
4. Evidence is immutable. Raw inbound email/attachment evidence is stored separately from extracted order data.
5. Review is explicit. Low-confidence or unresolved intake is reviewable; parsers must not guess missing master identities.
6. V2 is additive until cutover. V1 production data and URLs are not modified by V2 migrations.
7. No patch middleware or hidden fallback writers. Resilience must be observable and tested.
8. Every production promotion requires regression tests for the relevant business flow.

## Initial bounded areas

- Master Data: customers, sites, markets, drivers, vehicles, trailers and aliases/external identities.
- Intake: source evidence, extraction, classification, resolution, validation and review.
- Orders: canonical transport orders and revisions.
- Planning: runs, stops, order allocations and resource allocations.
- Live Operations: tracking observations, ETA and exceptions.
- Integrations: Outlook, TachoMaster, Sage HR, RoadTech/Roadrunner and future adapters.

V2 starts with Master Data and Intake contracts only. Planning and live integrations are reintroduced after the foundation passes acceptance gates.

See `docs/V2_ARCHITECTURE.md` for the implementation blueprint.
