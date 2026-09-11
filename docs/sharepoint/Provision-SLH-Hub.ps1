# SLH Hub SharePoint provisioning
# Requires: PnP.PowerShell and permission to manage the Stuart Lyons Haulage Team Portal.
# This script is intentionally idempotent: re-running it creates only missing lists/fields/views.
param([Parameter(Mandatory=$true)][string]$SiteUrl)

$ErrorActionPreference = 'Stop'
Connect-PnPOnline -Url $SiteUrl -Interactive

$lists = @{
 'Hub Customers'=@(
  @{Name='CustomerKey';Type='Text';Required=$true}; @{Name='TradingName';Type='Text'}; @{Name='Active';Type='Boolean'}; @{Name='AccountOwner';Type='Text'}; @{Name='ServiceNotes';Type='Note'}; @{Name='DefaultSiteCode';Type='Text'}; @{Name='TmsCustomerId';Type='Number'}; @{Name='LastSyncStatus';Type='Choice';Choices=@('Pending','Synced','Warning','Error')}; @{Name='LastSyncUtc';Type='DateTime'}
 )
 'Hub Sites'=@(
  @{Name='SiteKey';Type='Text';Required=$true}; @{Name='CustomerKey';Type='Text'}; @{Name='SiteName';Type='Text'}; @{Name='BuildingName';Type='Text'}; @{Name='Address1';Type='Text'}; @{Name='Address2';Type='Text'}; @{Name='Town';Type='Text'}; @{Name='County';Type='Text'}; @{Name='Postcode';Type='Text'}; @{Name='Latitude';Type='Number'}; @{Name='Longitude';Type='Number'}; @{Name='AccessWindowStart';Type='DateTime'}; @{Name='AccessWindowEnd';Type='DateTime'}; @{Name='GeofenceId';Type='Text'}; @{Name='Active';Type='Boolean'}; @{Name='TmsSiteId';Type='Number'}; @{Name='SyncStatus';Type='Choice';Choices=@('Pending','Synced','Warning','Error')}
 )
 'Hub Site Aliases'=@(
  @{Name='AliasKey';Type='Text';Required=$true}; @{Name='SiteKey';Type='Text'}; @{Name='Alias';Type='Text'}; @{Name='AliasType';Type='Choice';Choices=@('Customer','Site','Building','Legacy')}; @{Name='Active';Type='Boolean'}
 )
 'Hub Drivers'=@(
  @{Name='DriverKey';Type='Text';Required=$true}; @{Name='DriverName';Type='Text'}; @{Name='EmployeeNumber';Type='Text'}; @{Name='TachoName';Type='Text'}; @{Name='MobileNumber';Type='Text'}; @{Name='DriverType';Type='Text'}; @{Name='DriverGroup';Type='Text'}; @{Name='Skills';Type='Text'}; @{Name='AgencyName';Type='Text'}; @{Name='Coding';Type='Text'}; @{Name='Notes';Type='Note'}; @{Name='LicenceNumber';Type='Text'}; @{Name='LicenceExpiry';Type='DateTime'}; @{Name='CPCExpiry';Type='DateTime'}; @{Name='DigitalTachoCardExpiry';Type='DateTime'}; @{Name='MedicalExpiry';Type='DateTime'}; @{Name='TachoCardNumber';Type='Text'}; @{Name='TachoMasterDriverId';Type='Text'}; @{Name='Active';Type='Boolean'}; @{Name='TmsDriverId';Type='Number'}; @{Name='ComplianceStatus';Type='Choice';Choices=@('OK','Warning','Expired','Unknown')}; @{Name='LastSyncUtc';Type='DateTime'}; @{Name='LastTachoSyncUtc';Type='DateTime'}
 )
 'Hub Vehicles'=@(
  @{Name='VehicleKey';Type='Text';Required=$true}; @{Name='Registration';Type='Text'}; @{Name='FleetNumber';Type='Text'}; @{Name='VehicleType';Type='Text'}; @{Name='Abbreviation';Type='Text'}; @{Name='Transmission';Type='Text'}; @{Name='DvsCompliant';Type='Boolean'}; @{Name='FuelProvider';Type='Text'}; @{Name='CabMobile';Type='Text'}; @{Name='FuelPin';Type='Text'}; @{Name='ShellCard';Type='Text'}; @{Name='BpRedCard';Type='Text'}; @{Name='BpPlainCard';Type='Text'}; @{Name='FuelPinSecretName';Type='Text'}; @{Name='FuelCardLastFour';Type='Text'}; @{Name='Notes';Type='Note'}; @{Name='FleetioId';Type='Text'}; @{Name='FleetioName';Type='Text'}; @{Name='FleetioStatus';Type='Text'}; @{Name='MOTExpiry';Type='DateTime'}; @{Name='TachoCalibrationExpiry';Type='DateTime'}; @{Name='VehicleTestExpiry';Type='DateTime'}; @{Name='SamsaraAssetId';Type='Text'}; @{Name='Capacity';Type='Number'}; @{Name='Active';Type='Boolean'}; @{Name='TmsVehicleId';Type='Number'}; @{Name='ComplianceStatus';Type='Choice';Choices=@('OK','Warning','Expired','Unknown')}; @{Name='LastSyncUtc';Type='DateTime'}
 )=@(
  @{Name='VehicleKey';Type='Text';Required=$true}; @{Name='Registration';Type='Text'}; @{Name='VehicleType';Type='Text'}; @{Name='Capacity';Type='Number'}; @{Name='Active';Type='Boolean'}; @{Name='TmsVehicleId';Type='Number'}; @{Name='ComplianceStatus';Type='Choice';Choices=@('OK','Warning','Expired','Unknown')}; @{Name='LastSyncUtc';Type='DateTime'}
 )
 'Hub Trailers'=@(
  @{Name='TrailerKey';Type='Text';Required=$true}; @{Name='Registration';Type='Text'}; @{Name='TrailerType';Type='Text'}; @{Name='StandardCapacity';Type='Number'}; @{Name='EuroCapacity';Type='Number'}; @{Name='MOTExpiry';Type='DateTime'}; @{Name='TestExpiry';Type='DateTime'}; @{Name='Active';Type='Boolean'}; @{Name='TmsTrailerId';Type='Number'}; @{Name='LastSyncUtc';Type='DateTime'}
 )
 'Fuel Cards'=@(
  @{Name='VehicleKey';Type='Text';Required=$true}; @{Name='Registration';Type='Text'}; @{Name='FuelProvider';Type='Text'}; @{Name='FuelPinSecretName';Type='Text'}; @{Name='FuelCardLastFour';Type='Text'}; @{Name='ShellCard';Type='Text'}; @{Name='BpRedCard';Type='Text'}; @{Name='BpPlainCard';Type='Text'}; @{Name='Active';Type='Boolean'}; @{Name='LastSyncUtc';Type='DateTime'}
 )
 'Fuel Pricing'=@(
  @{Name='WeekCommencing';Type='DateTime';Required=$true}; @{Name='Provider';Type='Text';Required=$true}; @{Name='PricePencePerLitre';Type='Number';Required=$true}; @{Name='IsPricingMaximum';Type='Boolean'}; @{Name='Source';Type='Text'}; @{Name='Notes';Type='Note'}; @{Name='Active';Type='Boolean'}; @{Name='LastSyncUtc';Type='DateTime'}
 )
 'TMS Markets'=@(
  @{Name='Market';Type='Text';Required=$true}; @{Name='Name';Type='Text';Required=$true}; @{Name='StandOrLocation';Type='Text'}; @{Name='Salesman';Type='Text'}; @{Name='Sender';Type='Text'}; @{Name='Active';Type='Boolean'}; @{Name='LastSyncUtc';Type='DateTime'}
 )
 'Hub Integration Log'=@(
  @{Name='CorrelationId';Type='Text';Required=$true}; @{Name='EntityType';Type='Choice';Choices=@('Customer','Site','Alias','Driver','Vehicle','Trailer','Order','Document')}; @{Name='BusinessKey';Type='Text'}; @{Name='Direction';Type='Choice';Choices=@('SharePointToTms','TmsToSharePoint','Inbound')}; @{Name='Status';Type='Choice';Choices=@('Started','Succeeded','Warning','Failed','Retrying')}; @{Name='Message';Type='Note'}; @{Name='OccurredUtc';Type='DateTime'}
 )
 'Hub Incidents & Claims'=@(
  @{Name='ClaimKey';Type='Text';Required=$true}; @{Name='IncidentDate';Type='DateTime'}; @{Name='CustomerKey';Type='Text'}; @{Name='VehicleKey';Type='Text'}; @{Name='DriverKey';Type='Text'}; @{Name='Status';Type='Choice';Choices=@('Open','Investigation','Submitted','Settled','Closed')}; @{Name='Severity';Type='Choice';Choices=@('Low','Medium','High','Critical')}; @{Name='Description';Type='Note'}; @{Name='TmsIncidentId';Type='Number'}
 )
}

$descriptions = @{
 'Hub Customers'='Governed customer master-data projection. TMS SQL remains operational authority.'
 'Hub Sites'='Governed site master-data projection. SiteKey is the identity; postcode is not unique.'
 'Hub Site Aliases'='Inbound and legacy site-name aliases mapped to canonical SiteKey values.'
 'Hub Drivers'='People-facing driver reference and compliance projection. Operational availability remains in TMS.'
 'Hub Vehicles'='People-facing vehicle reference and compliance projection. Operational allocation remains in TMS.'
 'Hub Trailers'='People-facing trailer reference. Operational allocation remains in TMS.'
 'Hub Integration Log'='Human-readable integration audit projection. Authoritative operational audit remains in TMS SQL.'
 'Hub Incidents & Claims'='People-facing incidents and claims register linked back to TMS where applicable.'
}

$views = @{
 'Hub Customers'=@{Title='Hub Customers - Active';Fields=@('CustomerKey','TradingName','Active','AccountOwner','DefaultSiteCode','LastSyncStatus','LastSyncUtc');Query="<Where><Eq><FieldRef Name='Active'/><Value Type='Boolean'>1</Value></Eq></Where>"}
 'Hub Sites'=@{Title='Hub Sites - Active';Fields=@('SiteKey','CustomerKey','SiteName','BuildingName','Town','Postcode','AccessWindowStart','AccessWindowEnd','GeofenceId','Active','SyncStatus');Query="<Where><Eq><FieldRef Name='Active'/><Value Type='Boolean'>1</Value></Eq></Where>"}
 'Hub Site Aliases'=@{Title='Hub Site Aliases - Active';Fields=@('AliasKey','SiteKey','Alias','AliasType','Active');Query="<Where><Eq><FieldRef Name='Active'/><Value Type='Boolean'>1</Value></Eq></Where>"}
 'Hub Drivers'=@{Title='Hub Drivers - Active';Fields=@('DriverKey','DriverName','EmployeeNumber','LicenceNumber','LicenceExpiry','CPCExpiry','DigitalTachoCardExpiry','MedicalExpiry','Active','ComplianceStatus','LastSyncUtc');Query="<Where><Eq><FieldRef Name='Active'/><Value Type='Boolean'>1</Value></Eq></Where>"}
 'Hub Vehicles'=@{Title='Hub Vehicles - Active';Fields=@('VehicleKey','Registration','VehicleType','Capacity','MOTExpiry','TachoCalibrationExpiry','Active','ComplianceStatus','LastSyncUtc');Query="<Where><Eq><FieldRef Name='Active'/><Value Type='Boolean'>1</Value></Eq></Where>"}
 'Hub Trailers'=@{Title='Hub Trailers - Active';Fields=@('TrailerKey','Registration','TrailerType','StandardCapacity','EuroCapacity','MOTExpiry','TestExpiry','Active','LastSyncUtc');Query="<Where><Eq><FieldRef Name='Active'/><Value Type='Boolean'>1</Value></Eq></Where>"}
 'Hub Integration Log'=@{Title='Hub Integration Log - Recent';Fields=@('CorrelationId','EntityType','BusinessKey','Direction','Status','Message','OccurredUtc');Query='';RowLimit=100}
 'Fuel Cards'=@{Title='Fuel Cards - Active';Fields=@('VehicleKey','Registration','FuelProvider','FuelCardLastFour','Active','LastSyncUtc');Query="<Where><Eq><FieldRef Name='Active'/><Value Type='Boolean'>1</Value></Eq></Where>"}
 'Fuel Pricing'=@{Title='Fuel Pricing - Active';Fields=@('WeekCommencing','Provider','PricePencePerLitre','IsPricingMaximum','Source','Active','LastSyncUtc');Query="<Where><Eq><FieldRef Name='Active'/><Value Type='Boolean'>1</Value></Eq></Where>"}
 'TMS Markets'=@{Title='TMS Markets - Active';Fields=@('Market','Name','StandOrLocation','Salesman','Sender','Active','LastSyncUtc');Query="<Where><Eq><FieldRef Name='Active'/><Value Type='Boolean'>1</Value></Eq></Where>"}
 'Hub Incidents & Claims'=@{Title='Hub Incidents & Claims - Open';Fields=@('ClaimKey','IncidentDate','CustomerKey','VehicleKey','DriverKey','Status','Severity','Description','TmsIncidentId');Query="<Where><Neq><FieldRef Name='Status'/><Value Type='Choice'>Closed</Value></Neq></Where>"}
}

foreach($entry in $lists.GetEnumerator()) {
 $list=Get-PnPList -Identity $entry.Key -ErrorAction SilentlyContinue
 if(-not $list){$list=New-PnPList -Title $entry.Key -Template GenericList -OnQuickLaunch}
 if($descriptions.ContainsKey($entry.Key)){
  Set-PnPList -Identity $list -Description $descriptions[$entry.Key] -EnableVersioning $true -MajorVersions 20 | Out-Null
 }
 foreach($field in $entry.Value){
  $internal=$field.Name -replace '[^A-Za-z0-9]',''
  if(-not(Get-PnPField -List $list -Identity $internal -ErrorAction SilentlyContinue)){
   $p=@{List=$list;DisplayName=$field.Name;InternalName=$internal;Type=$field.Type;AddToDefaultView=$true}
   if($field.Required){$p.Required=$true}; if($field.Choices){$p.Choices=$field.Choices}; Add-PnPField @p | Out-Null
  }
 }
}

foreach($entry in $views.GetEnumerator()) {
 $list=$entry.Key
 $view=$entry.Value
 $existing=Get-PnPView -List $list -Identity $view.Title -ErrorAction SilentlyContinue
 if(-not $existing){
  $rowLimit=100
  if($view.ContainsKey('RowLimit')){$rowLimit=[uint32]$view.RowLimit}
  $params=@{List=$list;Title=$view.Title;Fields=$view.Fields;SetAsDefault=$true;Paged=$true;RowLimit=$rowLimit}
  if($view.Query){$params.Query=$view.Query}
  Add-PnPView @params | Out-Null
 } else {
  Set-PnPView -List $list -Identity $view.Title -Fields $view.Fields | Out-Null
 }
}

Write-Host 'SLH Hub SharePoint lists, fields, versioning and operational views provisioned.'
