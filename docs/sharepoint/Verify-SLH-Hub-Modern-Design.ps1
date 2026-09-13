# Verifies the SLH modern SharePoint Hub experience after tenant deployment.
param(
  [Parameter(Mandatory=$true)][string]$SiteUrl,
  [string]$ExpectedTheme = 'SLH Modern',
  [string]$ExpectedHomePage = 'SitePages/STUART-LYONS-HAULAGE-HUB.aspx'
)

$ErrorActionPreference = 'Stop'
Connect-PnPOnline -Url $SiteUrl -Interactive

$failures = New-Object System.Collections.Generic.List[string]
$checks = New-Object System.Collections.Generic.List[object]

function Add-Check([string]$Name,[bool]$Passed,[string]$Detail) {
  $checks.Add([pscustomobject]@{ Check=$Name; Passed=$Passed; Detail=$Detail }) | Out-Null
  if(-not $Passed){ $failures.Add("$Name: $Detail") | Out-Null }
}

$web = Get-PnPWeb -Includes Url,Title,WelcomePage
Add-Check 'Site reachable' ($null -ne $web) $web.Url

$theme = Get-PnPTenantTheme | Where-Object Name -eq $ExpectedTheme
Add-Check 'SLH theme exists' ($null -ne $theme) $ExpectedTheme

$page = Get-PnPPage -Identity 'STUART-LYONS-HAULAGE-HUB.aspx' -ErrorAction SilentlyContinue
Add-Check 'Modern Hub page exists' ($null -ne $page) 'SLH-Hub-Home.aspx'
if($page){
  Add-Check 'Hub page published' ($page.PageId -ne $null) 'Page is addressable in Site Pages'
}

$expectedNav = @(
  'Home','Transport Operations','TMS Master Data','Forms & Records',
  'Policies & Compliance','Staffing','Accounts - Restricted','Archive'
)
$nav = Get-PnPNavigationNode -Location TopNavigationBar
foreach($title in $expectedNav){
  Add-Check "Navigation: $title" ($null -ne ($nav | Where-Object Title -eq $title)) $title
}

$requiredLists = @(
  'Hub Customers','Hub Sites','Hub Site Aliases','Hub Drivers',
  'Hub Vehicles','Hub Trailers','Hub Integration Log','Hub Incidents & Claims'
)
foreach($listName in $requiredLists){
  $list = Get-PnPList -Identity $listName -ErrorAction SilentlyContinue
  Add-Check "List available: $listName" ($null -ne $list) $listName
}

$siteList = Get-PnPList -Identity 'Hub Sites' -ErrorAction SilentlyContinue
if($siteList){
  $siteKey = Get-PnPField -List $siteList -Identity 'SiteKey' -ErrorAction SilentlyContinue
  $postcode = Get-PnPField -List $siteList -Identity 'Postcode' -ErrorAction SilentlyContinue
  Add-Check 'SiteKey field exists' ($null -ne $siteKey) 'Canonical site identity field'
  Add-Check 'Postcode is not enforced unique' ($null -eq $postcode -or -not $postcode.EnforceUniqueValues) 'Postcode must never be the unique site identity'
}

$checks | Format-Table -AutoSize
if($failures.Count -gt 0){
  Write-Error ("SLH Hub modern design verification failed:`n - " + ($failures -join "`n - "))
  exit 1
}

Write-Host 'SLH Hub modern design verification passed.'
