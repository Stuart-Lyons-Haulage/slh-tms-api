from pathlib import Path


def replace(path: str, old: str, new: str, count: int = 1) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    found = text.count(old)
    if found != count:
        raise SystemExit(f"{path}: expected {count} occurrence(s) of {old!r}, found {found}")
    file.write_text(text.replace(old, new), encoding="utf-8")


program = Path("Program.cs")
text = program.read_text(encoding="utf-8")
old_policy = '''builder.Services.AddAuthorization(options =>
{
    var tmsAccessPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => TmsAccessPolicy.IsCompanyUser(context.User, allowedTmsDomains))
        .Build();
    options.DefaultPolicy = tmsAccessPolicy;
    options.FallbackPolicy = tmsAccessPolicy;
    options.AddPolicy("TmsAccess", tmsAccessPolicy);
    options.AddPolicy("TmsWrite", tmsAccessPolicy);
    options.AddPolicy("TmsApprove", tmsAccessPolicy);
});'''
new_policy = '''builder.Services.AddAuthorization(options =>
{
    AuthorizationPolicy BuildRolePolicy(IReadOnlyCollection<string> roles) => new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => TmsAccessPolicy.IsCompanyUser(context.User, allowedTmsDomains))
        .RequireRole(roles)
        .Build();

    var tmsAccessPolicy = BuildRolePolicy(TmsRoles.Access);
    options.DefaultPolicy = tmsAccessPolicy;
    options.FallbackPolicy = tmsAccessPolicy;
    options.AddPolicy(TmsPolicies.Access, tmsAccessPolicy);
    options.AddPolicy(TmsPolicies.Write, BuildRolePolicy(TmsRoles.Write));
    options.AddPolicy(TmsPolicies.Dispatch, BuildRolePolicy(TmsRoles.Dispatch));
    options.AddPolicy(TmsPolicies.Approve, BuildRolePolicy(TmsRoles.Approve));
    options.AddPolicy(TmsPolicies.MasterData, BuildRolePolicy(TmsRoles.MasterData));
    options.AddPolicy(TmsPolicies.Admin, BuildRolePolicy(TmsRoles.Admin));
});'''
if text.count(old_policy) != 1:
    raise SystemExit("Program.cs flat authorisation block not found exactly once")
text = text.replace(old_policy, new_policy)
old_validation = '''        ValidAudience = audience,
        ValidateLifetime = true'''
new_validation = '''        ValidAudience = audience,
        ValidateLifetime = true,
        NameClaimType = "preferred_username",
        RoleClaimType = "roles"'''
if text.count(old_validation) != 1:
    raise SystemExit("Program.cs token validation block not found exactly once")
text = text.replace(old_validation, new_validation)
# Preserve raw Entra claim names so preferred_username/upn/email and roles stay predictable.
anchor = '''    options.Authority = issuer;
    options.Audience = audience;'''
replacement = '''    options.Authority = issuer;
    options.Audience = audience;
    options.MapInboundClaims = false;'''
if text.count(anchor) != 1:
    raise SystemExit("Program.cs JWT options anchor not found exactly once")
program.write_text(text.replace(anchor, replacement), encoding="utf-8")

replace(
    "Slh.Tms.Api.Tests/TestAuthHandler.cs",
    "var identity = new ClaimsIdentity(claims, SchemeName);",
    'var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, "roles");',
)
replace(
    "Slh.Tms.Api.Tests/CustomWebFactory.cs",
    '''public HttpClient CreateClientWithUser(string userName, string scopes = "")
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User", userName);
        if (!string.IsNullOrEmpty(scopes)) c.DefaultRequestHeaders.Add("X-Test-Scopes", scopes);
        return c;
    }''',
    '''public HttpClient CreateClientWithUser(string userName, string scopes = "", string roles = "TMS.SystemAdmin")
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User", userName);
        if (!string.IsNullOrEmpty(scopes)) c.DefaultRequestHeaders.Add("X-Test-Scopes", scopes);
        if (!string.IsNullOrWhiteSpace(roles)) c.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        return c;
    }''',
)

# Authentication role-claim regression cases.
auth = Path("Slh.Tms.Api.Tests/AuthenticationTests.cs")
text = auth.read_text(encoding="utf-8")
old_roles = '''    [Theory]
    [InlineData("Tms.Access")]
    [InlineData("Tms.Write")]
    [InlineData("Tms.Approve")]
    [InlineData("Tms.Admin")]'''
new_roles = '''    [Theory]
    [InlineData("TMS.Viewer")]
    [InlineData("TMS.Planner")]
    [InlineData("TMS.Dispatcher")]
    [InlineData("TMS.OperationsController")]
    [InlineData("TMS.Approver")]
    [InlineData("TMS.MasterDataAdmin")]
    [InlineData("TMS.SystemAdmin")]'''
if text.count(old_roles) != 1:
    raise SystemExit("AuthenticationTests old role theory not found exactly once")
auth.write_text(text.replace(old_roles, new_roles), encoding="utf-8")

# Read-only endpoints accidentally elevated by the previous flat policy.
replace("Controllers/DotTrackingController.cs", '[Authorize(Policy = "TmsWrite")]', '[Authorize(Policy = "TmsAccess")]')
replace("Controllers/LiveVehicleDetailsController.cs", '[Authorize(Policy = "TmsWrite")]', '[Authorize(Policy = "TmsAccess")]')
replace("Controllers/CustomerCommunicationsController.cs", '[HttpGet("pending"), Authorize(Policy = "TmsWrite")]', '[HttpGet("pending"), Authorize(Policy = "TmsAccess")]')

# Dispatch mutations.
replace("Controllers/DriverDispatchController.cs", 'Authorize(Policy = "TmsWrite")', 'Authorize(Policy = "TmsDispatch")', 3)
replace("Controllers/PlanningController.cs", '[HttpPost("loads/{id:guid}/dispatch/sms"), Authorize(Policy = "TmsWrite")]', '[HttpPost("loads/{id:guid}/dispatch/sms"), Authorize(Policy = "TmsDispatch")]')
replace("Controllers/RunDriverMessageController.cs", '[HttpPost("{id:guid}/driver-message/sms"), Authorize(Policy = "TmsWrite")]', '[HttpPost("{id:guid}/driver-message/sms"), Authorize(Policy = "TmsDispatch")]')
replace("Controllers/OperationsControlController.cs", '[HttpPost("loads/{loadId}/driver-status"), Authorize(Policy = "TmsWrite")]', '[HttpPost("loads/{loadId}/driver-status"), Authorize(Policy = "TmsDispatch")]')
replace("Controllers/EtaPrecisionController.cs", '[HttpPost("eta-snapshots/capture"), Authorize(Policy = "TmsWrite")]', '[HttpPost("eta-snapshots/capture"), Authorize(Policy = "TmsDispatch")]')
replace("Controllers/CustomerCommunicationsController.cs", '[HttpPost("{communicationKey}/sent"), Authorize(Policy = "TmsWrite")]', '[HttpPost("{communicationKey}/sent"), Authorize(Policy = "TmsDispatch")]')

# Master-data administration.
replace("Controllers/AssistantController.cs", '[HttpPost("fix-safe-validations"), Authorize(Policy = "TmsApprove")]', '[HttpPost("fix-safe-validations"), Authorize(Policy = "TmsMasterData")]')
replace("Controllers/DriverVehiclePreferencesController.cs", '[HttpPost("refresh"), Authorize(Policy = "TmsWrite")]', '[HttpPost("refresh"), Authorize(Policy = "TmsMasterData")]')
replace("Controllers/FleetioAssetSyncController.cs", '[Authorize(Policy = "TmsWrite")]', '[Authorize(Policy = "TmsMasterData")]')
replace("Controllers/FleetioResilientSyncController.cs", '[Authorize(Policy = "TmsWrite")]', '[Authorize(Policy = "TmsMasterData")]')
replace("Controllers/FuelController.cs", '[Authorize(Policy = "TmsWrite")]', '[Authorize(Policy = "TmsMasterData")]')
replace("Controllers/GeofencesController.cs", 'Authorize(Policy = "TmsWrite")', 'Authorize(Policy = "TmsMasterData")', 3)
replace("Controllers/IntegrationsController.cs", 'Authorize(Policy = "TmsWrite")', 'Authorize(Policy = "TmsMasterData")', 3)
replace("Controllers/LookupsController.cs", 'Authorize(Policy = "TmsWrite")', 'Authorize(Policy = "TmsMasterData")', 6)
replace("Controllers/MasterDataCleanupController.cs", 'Authorize(Policy = "TmsApprove")', 'Authorize(Policy = "TmsMasterData")', 4)
replace("Controllers/MasterDataController.cs", 'Authorize(Policy = "TmsApprove")', 'Authorize(Policy = "TmsMasterData")', 2)
replace("Controllers/OperationalMasterDataController.cs", 'Authorize(Policy = "TmsApprove")', 'Authorize(Policy = "TmsMasterData")', 19)
replace("Controllers/SiteAliasController.cs", 'Authorize(Policy = "TmsApprove")', 'Authorize(Policy = "TmsMasterData")')
replace("Controllers/SiteGeofenceSyncController.cs", 'Authorize(Policy = "TmsApprove")', 'Authorize(Policy = "TmsMasterData")', 2)
replace("Controllers/SitePlanningProfilesController.cs", 'Authorize(Policy = "TmsApprove")', 'Authorize(Policy = "TmsMasterData")')
replace("Controllers/TachoDriverMasterController.cs", 'Authorize(Policy = "TmsApprove")', 'Authorize(Policy = "TmsMasterData")')
replace("Controllers/TachoMasterIdentityController.cs", '[Authorize(Policy = "TmsWrite")]', '[Authorize(Policy = "TmsMasterData")]')
replace("Controllers/OperationsControlController.cs", '[HttpPost("mappings"), Authorize(Policy = "TmsWrite")]', '[HttpPost("mappings"), Authorize(Policy = "TmsMasterData")]')
replace("Controllers/OperationsControlController.cs", '[HttpDelete("mappings/{id}"), Authorize(Policy = "TmsWrite")]', '[HttpDelete("mappings/{id}"), Authorize(Policy = "TmsMasterData")]')

# Master documents: read access for viewers, writes for master-data admins.
replace("Controllers/MasterDocumentsController.cs", '[ApiController, Route("api/v1/master-documents"), Authorize(Policy = "TmsWrite")]', '[ApiController, Route("api/v1/master-documents"), Authorize(Policy = "TmsAccess")]')
replace("Controllers/MasterDocumentsController.cs", '[HttpPost("{entityType}/{entityId:guid}")]', '[HttpPost("{entityType}/{entityId:guid}"), Authorize(Policy = "TmsMasterData")]')
replace("Controllers/MasterDocumentsController.cs", '[HttpPut("{documentId:guid}")]', '[HttpPut("{documentId:guid}"), Authorize(Policy = "TmsMasterData")]')
replace("Controllers/MasterDocumentsController.cs", '[HttpPost("{documentId:guid}/archive")]', '[HttpPost("{documentId:guid}/archive"), Authorize(Policy = "TmsMasterData")]')
replace("Controllers/MasterDocumentsController.cs", '[HttpPost("{documentId:guid}/restore")]', '[HttpPost("{documentId:guid}/restore"), Authorize(Policy = "TmsMasterData")]')

# Operational recovery split by privilege.
replace("Controllers/OperationalRecoveryController.cs", '[Authorize(Policy = "TmsWrite")]', '[Authorize(Policy = "TmsAccess")]')
replace("Controllers/OperationalRecoveryController.cs", '[HttpDelete("orders/{id:guid}")]', '[HttpDelete("orders/{id:guid}"), Authorize(Policy = "TmsWrite")]')
replace("Controllers/OperationalRecoveryController.cs", '[HttpPost("tachomaster/refresh-drivers")]', '[HttpPost("tachomaster/refresh-drivers"), Authorize(Policy = "TmsMasterData")]')

# System administration / destructive repair.
replace("Controllers/GeofenceHistoryReplayController.cs", 'Authorize(Policy = "TmsWrite")', 'Authorize(Policy = "TmsAdmin")', 2)
replace("Controllers/GeofenceRecoveryController.cs", 'Authorize(Policy = "TmsWrite")', 'Authorize(Policy = "TmsAdmin")')
replace("Controllers/MasterDataRebuildController.cs", 'Authorize(Policy = "TmsApprove")', 'Authorize(Policy = "TmsAdmin")')
replace("Controllers/NwfOrderReferenceRepairController.cs", '[Authorize(Policy = "TmsWrite")]', '[Authorize(Policy = "TmsAdmin")]')
replace("Controllers/SystemSyncController.cs", '[HttpPost("force/{provider}"), Authorize(Policy = "TmsWrite")]', '[HttpPost("force/{provider}"), Authorize(Policy = "TmsAdmin")]')
replace("Controllers/TvDisplayController.cs", '[HttpPost("key/rotate"), Authorize(Policy = "TmsWrite")]', '[HttpPost("key/rotate"), Authorize(Policy = "TmsAdmin")]')
replace("Controllers/TvDisplayController.cs", '[HttpPost("pairing-code/refresh"), Authorize(Policy = "TmsWrite")]', '[HttpPost("pairing-code/refresh"), Authorize(Policy = "TmsAdmin")]')
replace("Controllers/DiagnosticsController.cs", '[HttpGet("tables")]', '[HttpGet("tables"), Authorize(Policy = "TmsAdmin")]')
replace("Controllers/DiagnosticsController.cs", '[HttpGet("master-data-suggestions")]', '[HttpGet("master-data-suggestions"), Authorize(Policy = "TmsMasterData")]')

# Close a mutation that was anonymously exposed beside its health GET.
replace("Controllers/GeofenceMasterDataHealthController.cs", '[HttpPost("sync")]\n    [AllowAnonymous]', '[HttpPost("sync"), Authorize(Policy = "TmsMasterData")]')

# Approval boundaries.
replace("Controllers/OperationsIntelligenceController.cs", '[HttpPost("plan-lock/{date}"), Authorize(Policy = "TmsWrite")]', '[HttpPost("plan-lock/{date}"), Authorize(Policy = "TmsApprove")]')
replace("Controllers/OrdersBulkMaintenanceController.cs", '[HttpDelete("open"), Authorize(Policy = "TmsWrite")]', '[HttpDelete("open"), Authorize(Policy = "TmsApprove")]')

print("RBAC source transformation completed successfully.")
