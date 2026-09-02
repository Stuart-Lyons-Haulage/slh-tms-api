# SLH TMS Entra authorisation map

## Role hierarchy

The API treats the application roles as an ordered privilege hierarchy. Microsoft Entra emits the assigned role values in the `roles` claim; Entra does not infer hierarchy, so each ASP.NET Core policy explicitly lists every role at or above its threshold.

| Policy | Minimum role | Accepted roles |
| --- | --- | --- |
| `TmsAccess` | `TMS.Viewer` | Viewer, Planner, Dispatcher, OperationsController, Approver, MasterDataAdmin, SystemAdmin |
| `TmsWrite` | `TMS.Planner` | Planner, Dispatcher, OperationsController, Approver, MasterDataAdmin, SystemAdmin |
| `TmsDispatch` | `TMS.Dispatcher` | Dispatcher, OperationsController, Approver, MasterDataAdmin, SystemAdmin |
| `TmsApprove` | `TMS.Approver` | Approver, MasterDataAdmin, SystemAdmin |
| `TmsMasterData` | `TMS.MasterDataAdmin` | MasterDataAdmin, SystemAdmin |
| `TmsAdmin` | `TMS.SystemAdmin` | SystemAdmin only |

All Entra-protected policies also retain the existing SLH company-domain assertion. A valid SLH identity without an assigned application role fails closed with HTTP 403.

## Controller and endpoint mapping

Unless a stronger method-level policy is listed below, controllers marked `[Authorize]` use the default `TmsAccess` policy.

| Controller / API area | Read / default requirement | Mutating or privileged requirement |
| --- | --- | --- |
| `AssistantController` | `TmsAccess` for snapshot/advice | `fix-safe-validations` → `TmsMasterData` |
| `AssistantOrderDuplicatesController` | `TmsAccess` | duplicate fix → `TmsApprove` |
| `BulkOrderApprovalController` | `TmsAccess` | bulk approve → `TmsApprove` |
| `CapacityController` | `TmsAccess` | capacity check is non-persistent and remains `TmsAccess` |
| `CustomerCommunicationsController` | `TmsAccess` for pending/ledger | ingest → `TmsWrite`; mark sent → `TmsDispatch`; approve/reject → `TmsApprove` |
| `CustomerEtaEvidenceController` | `TmsAccess` | none |
| `DailyComplianceController` | `TmsAccess` | none |
| `DiagnosticsController` | dedicated readiness health endpoint remains anonymous | tables → `TmsAdmin`; master-data suggestions → `TmsMasterData` |
| `DotTrackingController` | `TmsAccess` | none |
| `DotTrackingHealthController` | anonymous health only | none |
| `DriverDispatchController` | `TmsAccess` workbench | driver/agency roster and start-time changes → `TmsDispatch` |
| `DriverHoursComplianceController` | `TmsAccess` | none |
| `DriverMasterHealthController` | anonymous health only | none |
| `DriverPlanningController` | `TmsAccess` | none; operational assignment feed no longer anonymous |
| `DriverVehiclePreferencesController` | `TmsAccess` | refresh preference evidence → `TmsMasterData` |
| `EtaPrecisionController` | `TmsAccess` | ETA snapshot capture → `TmsDispatch` |
| `FleetioAssetStatusResilientController` | `TmsAccess` | none |
| `FleetioAssetSyncController` | `TmsAccess` status/maintenance | asset sync → `TmsMasterData` |
| `FleetioResilientSyncController` | `TmsAccess` | resilient asset sync → `TmsMasterData` |
| `FuelController` | `TmsAccess` | fuel-price upsert → `TmsMasterData` |
| `GeofenceHistoryReplayController` | `TmsAccess` | rebuild/replay → `TmsAdmin` |
| `GeofenceIntegrityController` | `TmsAccess` | none |
| `GeofenceMasterDataHealthController` | anonymous GET health | sync → `TmsMasterData` |
| `GeofencePayloadHealthController` | anonymous health only | none |
| `GeofenceRecoveryController` | `TmsAccess` | recovery/ensure → `TmsAdmin` |
| `GeofencesController` | `TmsAccess` for list/visits | import seed/provider data and repair links → `TmsMasterData` |
| `IntegrationsController` | `TmsAccess` for integration status | TachoMaster/Sage HR/Fleetio master sync → `TmsMasterData` |
| `LiveVehicleDetailsController` | `TmsAccess` | none |
| `LookupsController` | `TmsAccess` for lookups | vehicle/driver/customer/contact/trailer/site updates → `TmsMasterData` |
| `ManagementController` | `TmsAccess` | none |
| `ManagementResilienceController` | `TmsAccess` | none |
| `MasterDataCleanupController` | `TmsAccess` | archive/restore/delete/bulk-delete → `TmsMasterData` |
| `MasterDataController` | `TmsAccess` | apply and register-link → `TmsMasterData` |
| `MasterDataRebuildController` | `TmsAccess` | reviewed-register rebuild → `TmsAdmin` |
| `MasterDocumentsController` | `TmsAccess` | add/update/archive/restore → `TmsMasterData` |
| `NightOutController` | `TmsAccess` | none |
| `NwfOrderReferenceRepairController` | `TmsAdmin` | repair → `TmsAdmin` |
| `OperationalMasterDataController` | `TmsAccess` for search/detail/audit | all driver, vehicle, trailer, site, customer and geofence mutations → `TmsMasterData` |
| `OperationalOrderMaintenanceController` | `TmsWrite` | order update → `TmsWrite` |
| `OperationalRecoveryController` | `TmsAccess` | cancel order → `TmsWrite`; Tacho driver refresh → `TmsMasterData` |
| `OperationalSnapshotController` | `TmsAccess` | none |
| `OperationsControlController` | `TmsAccess` | integration mapping create/delete → `TmsMasterData`; driver status capture → `TmsDispatch` |
| `OperationsController` | `TmsAccess` | delivery ETA feed no longer anonymous |
| `OperationsIntelligenceController` | `TmsAccess` | lock plan → `TmsApprove` |
| `OperationsLiveCoverageController` | `TmsAccess` | none |
| `OrderIntakeController` | `TmsAccess` | preview/intake → `TmsWrite` |
| `OrderIntakeDuplicateCheckController` | `TmsAccess` | duplicate check request → `TmsWrite` |
| `OrderIntakeLedgerController` | `TmsAccess` | replay → `TmsWrite` |
| `OrderReviewSchemaHealthController` | anonymous health only | none |
| `OrdersBulkMaintenanceController` | `TmsAccess` | cancel all open orders → `TmsApprove` |
| `OrdersMaintenanceController` | `TmsAccess` | update/cancel individual order → `TmsWrite` |
| `OutstandingReferencesController` | `TmsAccess` | draft chase, record sent and resolve → `TmsWrite` |
| `PalletPlanningControlController` | `TmsAccess` | pallet allocation → `TmsWrite` |
| `PlannerAutosaveController` | `TmsAccess` | stop autosave → `TmsWrite` |
| `PlannerEfficiencyController` | `TmsAccess` | none |
| `PlannerImportHealthController` | anonymous health only | none |
| `PlannerPlanImportController` | `TmsAccess` | plan import → `TmsWrite` |
| `PlannerResourceReconciliationController` | `TmsAccess` | resource reconcile → `TmsWrite` |
| `PlannerRunSequenceController` | `TmsAccess` | run resequence → `TmsWrite` |
| `PlannerSiteReconciliationController` | `TmsAccess` | site reconcile → `TmsWrite` |
| `PlannerSourceLineImportController` | `TmsAccess` | source-plan import → `TmsWrite` |
| `PlannerSuggestionsController` | `TmsAccess` | none |
| `PlanningController` | `TmsAccess` for orders, loads, route, dispatch pack and geocode | create/allocate/update runs → `TmsWrite`; send dispatch SMS → `TmsDispatch` |
| `PlanningDayResetController` | `TmsAccess` preview | reset day → `TmsApprove` |
| `PlanningIntelligenceController` | `TmsAccess` | night-out update → `TmsWrite` |
| `PlanningOptimiserController` | `TmsAccess` for proposal reads | generate/apply proposal → `TmsWrite` |
| `PlanningRegionController` | `TmsAccess` | none |
| `PlanningResilienceController` | internal helper; no API action | n/a |
| `RunAllocationResilienceController` | `TmsAccess` for runs/route/dispatch | allocation, operational, status and stop updates → `TmsWrite` |
| `RunDriverMessageController` | `TmsAccess` for readiness | send driver SMS → `TmsDispatch` |
| `RunEvidenceHealthController` | `TmsAccess` | operational run evidence is no longer anonymous |
| `RunGeofenceLinkageController` | `TmsAccess` | none |
| `RunProgressController` | `TmsAccess` | operational progress feed is no longer anonymous |
| `RunReadinessController` | `TmsAccess` | none |
| `RunTimelineResilienceController` | `TmsAccess` | none |
| `RunTimingController` | `TmsAccess` | operational timing feed is no longer anonymous |
| `SiteAliasController` | `TmsAccess` | alias update → `TmsMasterData` |
| `SiteGeofenceSyncController` | `TmsAccess` for site view | sync/link → `TmsMasterData` |
| `SitePlanningProfilesController` | `TmsAccess` | update → `TmsMasterData` |
| `StagingAmendmentController` | `TmsAccess` | amend/confirm delivery site → `TmsApprove` |
| `StagingController` | `TmsAccess` for list/detail/history | stage/batch → `TmsWrite`; archive/clear/approve/reject → `TmsApprove` |
| `SubcontractorResourcesController` | `TmsAccess` | create resources → `TmsWrite` |
| `SystemSyncController` | `TmsAccess` for state | force provider sync → `TmsAdmin` |
| `TachoDriverMasterController` | `TmsAccess` for status/quality/profile | TachoMaster master sync → `TmsMasterData` |
| `TachoMasterDriverHistoryController` | `TmsAccess` | none |
| `TachoMasterHealthController` | anonymous health only | none |
| `TachoMasterIdentityController` | `TmsMasterData` | worker/vehicle identity imports → `TmsMasterData` |
| `TrackingGeofenceHealthController` | anonymous summary health; vehicle detail requires `TmsAccess` | none |
| `TvDisplayController` | display key/pairing-code reads → `TmsAccess`; paired TV feed uses device-key path | rotate key / refresh pairing code → `TmsAdmin` |
| `TvDisplayRunLabelsController` | TV device-key / authenticated-user path | none |
| `TvDwellController` | TV device-key / authenticated-user path | none |
| `TvPlannedRunsController` | TV device-key / authenticated-user path | none |
| `TvRouteProgressController` | TV device-key / authenticated-user path | none |
| `WallboardSourceHealthController` | anonymous health only | none |
| `WarehousePlanningController` | `TmsAccess` | none |
| `WeeklyDriverTimesheetsController` | `TmsAccess` | none |
| `WeeklyDriverTimesheetsResilientController` | `TmsAccess` | none |

## Anonymous/device exceptions

`AllowAnonymous` is retained only where the route is deliberately outside normal interactive Entra RBAC: narrow health/readiness endpoints and paired TV/device feeds. TV feeds must continue to validate the paired display key or accepted legacy wallboard key internally. Operational planning, driver assignment, ETA, run-progress and run-timing feeds are no longer anonymous and require `TmsAccess`.

## Controller attribute examples

```csharp
[ApiController, Route("api/v1/driver-dispatch"), Authorize]
public sealed class DriverDispatchController : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get(...) { ... } // TmsAccess via default policy

    [HttpPut("{loadId:guid}/start-time"), Authorize(Policy = TmsPolicies.Dispatch)]
    public Task<IActionResult> SetStartTime(...) { ... }
}
```

```csharp
[HttpPost("{id:guid}/approve"), Authorize(Policy = TmsPolicies.Approve)]
public Task<IActionResult> Approve(...) { ... }

[HttpPut("drivers/{id:guid}"), Authorize(Policy = TmsPolicies.MasterData)]
public Task<IActionResult> UpdateDriver(...) { ... }

[HttpPost("force/{provider}"), Authorize(Policy = TmsPolicies.Admin)]
public Task<IActionResult> Force(...) { ... }
```

The existing string policy attributes can be migrated to the `TmsPolicies` constants incrementally; the policy values are identical and the runtime behaviour is already role-based.
