# SLH Hub SharePoint verification
# Requires: PnP.PowerShell and permission to read the Stuart Lyons Haulage Team Portal.
param([Parameter(Mandatory=$true)][string]$SiteUrl)

$ErrorActionPreference = 'Stop'
Connect-PnPOnline -Url $SiteUrl -Interactive

$expected = @{
 'Hub Customers'=@('CustomerKey','TradingName','Active','AccountOwner','ServiceNotes','TmsCustomerId','LastSyncStatus','LastSyncUtc')
 'Hub Sites'=@('SiteKey','CustomerKey','SiteName','BuildingName','Address1','Address2','Town','County','Postcode','Latitude','Longitude','AccessWindowStart','AccessWindowEnd','GeofenceId','Active','TmsSiteId','SyncStatus')
 'Hub Site Aliases'=@('AliasKey','SiteKey','Alias','AliasType','Active')
 'Hub Drivers'=@('DriverKey','DriverName','EmployeeNumber','LicenceNumber','Active','TmsDriverId','ComplianceStatus','LastSyncUtc')
 'Hub Vehicles'=@('VehicleKey','Registration','VehicleType','Capacity','Active','TmsVehicleId','ComplianceStatus','LastSyncUtc')
 'Hub Trailers'=@('TrailerKey','Registration','TrailerType','Capacity','Active','TmsTrailerId','LastSyncUtc')
 'Hub Integration Log'=@('CorrelationId','EntityType','BusinessKey','Direction','Status','Message','OccurredUtc')
 'Hub Incidents & Claims'=@('ClaimKey','IncidentDate','CustomerKey','VehicleKey','DriverKey','Status','Severity','Description','TmsIncidentId')
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach($entry in $expected.GetEnumerator()) {
    $list = Get-PnPList -Identity $entry.Key -ErrorAction SilentlyContinue
    if(-not $list) { $failures.Add("Missing list: $($entry.Key)"); continue }

    foreach($displayName in $entry.Value) {
        $internal = $displayName -replace '[^A-Za-z0-9]',''
        if(-not (Get-PnPField -List $list -Identity $internal -ErrorAction SilentlyContinue)) {
            $failures.Add("Missing field: $($entry.Key).$displayName")
        }
    }

    $activeView = Get-PnPView -List $entry.Key -Identity ((@{
        'Hub Customers'='Hub Customers - Active'; 'Hub Sites'='Hub Sites - Active'; 'Hub Site Aliases'='Hub Site Aliases - Active';
        'Hub Drivers'='Hub Drivers - Active'; 'Hub Vehicles'='Hub Vehicles - Active'; 'Hub Trailers'='Hub Trailers - Active';
        'Hub Integration Log'='Hub Integration Log - Recent'; 'Hub Incidents & Claims'='Hub Incidents & Claims - Open'
    })[$entry.Key]) -ErrorAction SilentlyContinue
    if(-not $activeView) { $failures.Add("Missing operational view: $($entry.Key)") }
}

# Explicitly check the site-identity rule is represented in the provisioned schema.
$site = Get-PnPList -Identity 'Hub Sites' -ErrorAction SilentlyContinue
if($site) {
    $siteKey = Get-PnPField -List $site -Identity 'SiteKey' -ErrorAction SilentlyContinue
    $postcode = Get-PnPField -List $site -Identity 'Postcode' -ErrorAction SilentlyContinue
    if(-not $siteKey -or -not $postcode) { $failures.Add('Hub Sites must contain both SiteKey and Postcode; postcode is not the identity key.') }
}

if($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "SLH Hub verification failed with $($failures.Count) issue(s)."
}

Write-Host 'SLH Hub verification passed: all expected lists, fields and operational views are present.'
