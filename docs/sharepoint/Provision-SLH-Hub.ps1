# SLH Hub SharePoint provisioning
# Requires: PnP.PowerShell and permission to manage the Stuart Lyons Haulage Team Portal.
param([Parameter(Mandatory=$true)][string]$SiteUrl)
Connect-PnPOnline -Url $SiteUrl -Interactive
$lists = @{
 'Hub Customers'=@(
  @{Name='CustomerKey';Type='Text';Required=$true}; @{Name='TradingName';Type='Text'}; @{Name='Active';Type='Boolean'}; @{Name='AccountOwner';Type='Text'}; @{Name='ServiceNotes';Type='Note'}; @{Name='TmsCustomerId';Type='Number'}; @{Name='LastSyncStatus';Type='Choice';Choices=@('Pending','Synced','Warning','Error')}; @{Name='LastSyncUtc';Type='DateTime'}
 )
 'Hub Sites'=@(
  @{Name='SiteKey';Type='Text';Required=$true}; @{Name='CustomerKey';Type='Text'}; @{Name='SiteName';Type='Text'}; @{Name='BuildingName';Type='Text'}; @{Name='Address1';Type='Text'}; @{Name='Address2';Type='Text'}; @{Name='Town';Type='Text'}; @{Name='County';Type='Text'}; @{Name='Postcode';Type='Text'}; @{Name='Latitude';Type='Number'}; @{Name='Longitude';Type='Number'}; @{Name='AccessWindowStart';Type='DateTime'}; @{Name='AccessWindowEnd';Type='DateTime'}; @{Name='GeofenceId';Type='Text'}; @{Name='Active';Type='Boolean'}; @{Name='TmsSiteId';Type='Number'}; @{Name='SyncStatus';Type='Choice';Choices=@('Pending','Synced','Warning','Error')}
 )
 'Hub Site Aliases'=@(
  @{Name='AliasKey';Type='Text';Required=$true}; @{Name='SiteKey';Type='Text'}; @{Name='Alias';Type='Text'}; @{Name='AliasType';Type='Choice';Choices=@('Customer','Site','Building','Legacy')}; @{Name='Active';Type='Boolean'}
 )
 'Hub Drivers'=@(
  @{Name='DriverKey';Type='Text';Required=$true}; @{Name='DriverName';Type='Text'}; @{Name='EmployeeNumber';Type='Text'}; @{Name='LicenceNumber';Type='Text'}; @{Name='Active';Type='Boolean'}; @{Name='TmsDriverId';Type='Number'}; @{Name='ComplianceStatus';Type='Choice';Choices=@('OK','Warning','Expired','Unknown')}; @{Name='LastSyncUtc';Type='DateTime'}
 )
 'Hub Vehicles'=@(
  @{Name='VehicleKey';Type='Text';Required=$true}; @{Name='Registration';Type='Text'}; @{Name='VehicleType';Type='Text'}; @{Name='Capacity';Type='Number'}; @{Name='Active';Type='Boolean'}; @{Name='TmsVehicleId';Type='Number'}; @{Name='ComplianceStatus';Type='Choice';Choices=@('OK','Warning','Expired','Unknown')}; @{Name='LastSyncUtc';Type='DateTime'}
 )
 'Hub Trailers'=@(
  @{Name='TrailerKey';Type='Text';Required=$true}; @{Name='Registration';Type='Text'}; @{Name='TrailerType';Type='Text'}; @{Name='Capacity';Type='Number'}; @{Name='Active';Type='Boolean'}; @{Name='TmsTrailerId';Type='Number'}; @{Name='LastSyncUtc';Type='DateTime'}
 )
 'Hub Integration Log'=@(
  @{Name='CorrelationId';Type='Text';Required=$true}; @{Name='EntityType';Type='Choice';Choices=@('Customer','Site','Alias','Driver','Vehicle','Trailer','Order','Document')}; @{Name='BusinessKey';Type='Text'}; @{Name='Direction';Type='Choice';Choices=@('SharePointToTms','TmsToSharePoint','Inbound')}; @{Name='Status';Type='Choice';Choices=@('Started','Succeeded','Warning','Failed','Retrying')}; @{Name='Message';Type='Note'}; @{Name='OccurredUtc';Type='DateTime'}
 )
 'Hub Incidents & Claims'=@(
  @{Name='ClaimKey';Type='Text';Required=$true}; @{Name='IncidentDate';Type='DateTime'}; @{Name='CustomerKey';Type='Text'}; @{Name='VehicleKey';Type='Text'}; @{Name='DriverKey';Type='Text'}; @{Name='Status';Type='Choice';Choices=@('Open','Investigation','Submitted','Settled','Closed')}; @{Name='Severity';Type='Choice';Choices=@('Low','Medium','High','Critical')}; @{Name='Description';Type='Note'}; @{Name='TmsIncidentId';Type='Number'}
 )
}
foreach($entry in $lists.GetEnumerator()) {
 $list=Get-PnPList -Identity $entry.Key -ErrorAction SilentlyContinue
 if(-not $list){$list=New-PnPList -Title $entry.Key -Template GenericList -OnQuickLaunch}
 foreach($field in $entry.Value){
  $internal=$field.Name -replace '[^A-Za-z0-9]',''
  if(-not(Get-PnPField -List $list -Identity $internal -ErrorAction SilentlyContinue)){
   $p=@{List=$list;DisplayName=$field.Name;InternalName=$internal;Type=$field.Type;AddToDefaultView=$true}
   if($field.Required){$p.Required=$true}; if($field.Choices){$p.Choices=$field.Choices}; Add-PnPField @p | Out-Null
  }
 }
}
Write-Host 'SLH Hub SharePoint lists provisioned.'
