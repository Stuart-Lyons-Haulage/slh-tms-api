# SLH Hub SharePoint verification
# Requires: PnP.PowerShell and permission to read the Stuart Lyons Haulage Team Portal.
param([Parameter(Mandatory=$true)][string]$SiteUrl)

$ErrorActionPreference = 'Stop'
Connect-PnPOnline -Url $SiteUrl -Interactive

$expected = @{
 'Hub Customers'=@('CustomerKey','TradingName','Active','AccountOwner','ServiceNotes','DefaultSiteCode','TmsCustomerId','LastSyncStatus','LastSyncUtc')
 'Hub Customer Contacts'=@('ContactKey','CustomerKey','ContactName','Email','MobileNumber','ReceivesEtaUpdates','Active')
 'Hub Sites'=@('SiteKey','CustomerKey','SiteName','BuildingName','Address1','Address2','Town','County','Postcode','Latitude','Longitude','AccessWindowStart','AccessWindowEnd','GeofenceId','Active','TmsSiteId','SyncStatus')
 'Hub Site Aliases'=@('AliasKey','SiteKey','Alias','AliasType','Active')
 'Hub Drivers'=@('DriverKey','DriverName','Employee Number','Member Code','TachoName','MobileNumber','DriverType','DriverGroup','Skills','AgencyName','Coding','Notes','Email','Site','Type','Agency','Started','Licence Pass Date','Licence Check Due','Licence Photo Exp.','DQC Expiry','LicenceNumber','Driving Licence Exp.','CPC Expiry','Driver Card Exp.','MedicalExpiry','Driver Card No.','TachoMasterDriverId','Card Last Read','Active','TmsDriverId','ComplianceStatus','LastSyncUtc','LastTachoSyncUtc')
 'Hub Vehicles'=@('VehicleKey','Registration','VIN','Site','OwnerType','FleetNumber','VehicleType','Abbreviation','Transmission','DvsCompliant','FuelProvider','CabMobile','FuelPin','ShellCard','BpRedCard','BpPlainCard','FuelPinSecretName','FuelCardLastFour','Notes','FleetioId','FleetioName','FleetioStatus','MOTExpiry','TachoCalibrationExpiry','VehicleTestExpiry','SamsaraAssetId','Capacity','Active','TmsVehicleId','ComplianceStatus','LastSyncUtc')
 'Hub Trailers'=@('TrailerKey','Registration','TrailerType','StandardCapacity','EuroCapacity','MOTExpiry','TestExpiry','Active','TmsTrailerId','LastSyncUtc')
 'Fuel Cards'=@('VehicleKey','Registration','FuelProvider','FuelPinSecretName','FuelCardLastFour','ShellCard','BpRedCard','BpPlainCard','Active','LastSyncUtc')
 'Fuel Pricing'=@('WeekCommencing','Provider','PricePencePerLitre','IsPricingMaximum','Source','Notes','Active','LastSyncUtc')
 'TMS Markets'=@('MarketKey','Market Name','Seller','Stall/Stand','Salesman','Sender','Active','LastSyncUtc')
 'Order Email Routes'=@('RouteKey','CustomerKey','SiteKey','MarketKey','SenderEmail','SenderDomain','SubjectContains','ParserType','RequiresReview','Active')
 'Hub Integration Log'=@('CorrelationId','EntityType','BusinessKey','Direction','Status','Message','OccurredUtc')
 'Hub Incidents & Claims'=@('ClaimKey','IncidentDate','CustomerKey','VehicleKey','DriverKey','Status','Severity','Description','TmsIncidentId')
}

$operationalViews = @{
 'Hub Customers'='Hub Customers - Active'
 'Hub Customer Contacts'='Hub Customer Contacts - Active'
 'Hub Sites'='Hub Sites - Active'
 'Hub Site Aliases'='Hub Site Aliases - Active'
 'Hub Drivers'='Hub Drivers - Active'
 'Hub Vehicles'='Hub Vehicles - Active'
 'Hub Trailers'='Hub Trailers - Active'
 'Fuel Cards'='Fuel Cards - Active'
 'Fuel Pricing'='Fuel Pricing - Active'
 'TMS Markets'='TMS Markets - Active'
 'Order Email Routes'='Order Email Routes - Active'
 'Hub Integration Log'='Hub Integration Log - Recent'
 'Hub Incidents & Claims'='Hub Incidents & Claims - Open'
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach($entry in $expected.GetEnumerator()) {
    $list = Get-PnPList -Identity $entry.Key -ErrorAction SilentlyContinue
    if(-not $list) { $failures.Add("Missing list: $($entry.Key)"); continue }

    foreach($displayName in $entry.Value) {
        $internal = $displayName -replace '[^A-Za-z0-9]',''
        $field = Get-PnPField -List $list -ErrorAction SilentlyContinue | Where-Object { $_.Title -eq $displayName -or $_.InternalName -eq $internal -or ($displayName -eq 'Market Name' -and $_.InternalName -eq 'Market') } | Select-Object -First 1
        if(-not $field) {
            $failures.Add("Missing field: $($entry.Key).$displayName")
        }
    }

    if($operationalViews.ContainsKey($entry.Key)) {
        $activeView = Get-PnPView -List $entry.Key -Identity $operationalViews[$entry.Key] -ErrorAction SilentlyContinue
        if(-not $activeView) { $failures.Add("Missing operational view: $($entry.Key)") }
    }
}

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
