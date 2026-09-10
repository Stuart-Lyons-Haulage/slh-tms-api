# SLH Hub modern SharePoint design

## Objective

Create a modern Stuart Lyons Haulage internal portal that is operationally useful, visually consistent and safe for the TMS architecture.

The TMS SQL database remains the operational system of record for orders, loads, runs, allocations, tracking, ETA and audit history. SharePoint is the business-facing information, governance and reference layer.

## Visual direction

- Brand feel: professional road-haulage, modern, clean, operational and confident.
- Primary colour: deep Lyons blue.
- Accent colour: Lyons green.
- Surfaces: white and very light neutral backgrounds.
- Typography: native Microsoft 365 / Segoe UI for accessibility and consistency.
- Avoid dense folder-first navigation on the home page. Surface tasks, alerts, documents and governed master data first.

## Proposed information architecture

### Global navigation

1. Home
2. Transport Operations
3. TMS Master Data
4. Forms & Records
5. Policies & Compliance
6. Staffing
7. Accounts - Restricted
8. Archive

### Home page

The home page should be a dashboard rather than a document landing page.

#### Hero

- Open TMS Portal
- Order Review
- Planning Board
- Live Operations
- Driver Dispatch

#### Governed master-data actions

The home page should also provide clear, role-appropriate actions:

- Add customer
- Add site
- Add site alias
- Add driver
- Add vehicle
- Add trailer

Each action opens the matching SharePoint List NewForm and creates a governed SharePoint master-data projection for review/synchronisation. It must not create a live order, run, dispatch record or other TMS transaction. The site form must require CustomerKey and SiteKey; postcode remains descriptive and non-unique.

#### Operations shortcuts

- Operational Live Documents
- TMS Master Data
- Forms & Records
- Transport procedures
- Incident & Claims register
- Integration status

#### Today / attention area

Use list and document web parts to surface:

- Master-data validation items
- Integration warnings/errors
- Open incidents and claims
- Recently changed operational documents
- Latest company/transport notices

#### Governance strip

A concise statement should be visible:

> Operational orders, loads, runs, allocation, tracking and ETA remain controlled by the SLH TMS. SharePoint provides governed master-data projections, documents, records and business-facing operational information.

## TMS Master Data experience

The existing numbered folder structure remains the controlled document repository:

- 00 Governance & Schema
- 01 Customers
- 02 Sites
- 03 Markets
- 04 Drivers
- 05 Vehicles
- 06 Trailers
- 07 Contacts & Communications
- 08 Timing & Access Rules
- 09 Mappings & Aliases
- 90 Validation & Review
- 99 Snapshots & Archive

The user-facing experience should favour SharePoint Lists and views over browsing these folders for routine reference.

### Core governed lists

- Hub Customers
- Hub Sites
- Hub Site Aliases
- Hub Drivers
- Hub Vehicles
- Hub Trailers
- Hub Integration Log
- Hub Incidents & Claims

### Identity rule

Postcode is **not** a unique identifier.

Canonical site identity is based on `CustomerKey + SiteKey`. Aliases map alternate names and legacy wording to the canonical `SiteKey`.

## Audience-based design

### Transport planners

Primary links: TMS Portal, Order Review, Planning Board, Live Operations, Driver Dispatch, Operational Live Documents.

### Managers

Primary links: operational dashboard, integration warnings, incidents, compliance, master-data validation and reporting.

### Office / administration

Primary links: Forms & Records, Customers, Contacts & Communications, Policies, Staffing.

### Restricted finance users

Accounts content remains separate under Accounts - Restricted. The redesign must not broaden permissions.

## Design principles

1. Do not duplicate the TMS operational database in SharePoint.
2. Do not use postcode as a unique site key.
3. Do not alter Power Automate order intake as part of portal styling.
4. Preserve permissions on restricted libraries/folders.
5. Prefer governed lists and clear views over unmanaged spreadsheets.
6. Keep navigation shallow: users should reach common tasks within two clicks.
7. Use consistent naming and avoid abbreviations unless they are standard SLH operational terms.
8. Use status views for Warning/Error/Open items so exceptions are visible rather than buried.

## Implementation

`Apply-SLH-Hub-Modern-Design.ps1` provisions the theme, navigation and modern home page. It is designed to be repeatable and is separate from the existing list-provisioning script so visual changes do not alter the TMS data contract.
