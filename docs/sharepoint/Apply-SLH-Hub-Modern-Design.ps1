# Modern SLH SharePoint Hub design
# Requires PnP.PowerShell and delegated permission to manage the Stuart Lyons Haulage SharePoint site.
# This script is intentionally separate from Provision-SLH-Hub.ps1 so styling/navigation changes do not alter TMS data contracts.
param(
  [Parameter(Mandatory=$true)][string]$SiteUrl,
  [string]$TmsPortalUrl = 'https://slh-tms-portal-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io/',
  [switch]$SetAsHomePage
)

$ErrorActionPreference = 'Stop'
Connect-PnPOnline -Url $SiteUrl -Interactive

$themeName = 'SLH Modern'
$theme = @{
  'themePrimary' = '#0B3A63'
  'themeLighterAlt' = '#F2F7FB'
  'themeLighter' = '#D5E4F1'
  'themeLight' = '#B5CEE3'
  'themeTertiary' = '#6E9CBD'
  'themeSecondary' = '#2E648D'
  'themeDarkAlt' = '#0A3459'
  'themeDark' = '#082C4B'
  'themeDarker' = '#061F36'
  'neutralLighterAlt' = '#FAFAFA'
  'neutralLighter' = '#F4F4F4'
  'neutralLight' = '#EAEAEA'
  'neutralQuaternaryAlt' = '#DADADA'
  'neutralQuaternary' = '#D0D0D0'
  'neutralTertiaryAlt' = '#C8C8C8'
  'neutralTertiary' = '#A6A6A6'
  'neutralSecondary' = '#666666'
  'neutralPrimaryAlt' = '#3C3C3C'
  'neutralPrimary' = '#222222'
  'neutralDark' = '#111111'
  'black' = '#000000'
  'white' = '#FFFFFF'
  'primaryBackground' = '#FFFFFF'
  'primaryText' = '#222222'
  'bodyBackground' = '#FFFFFF'
  'bodyText' = '#222222'
  'disabledBackground' = '#F4F4F4'
  'disabledText' = '#A6A6A6'
  'accent' = '#6AA84F'
}

$themeJson = $theme | ConvertTo-Json -Compress
$existingTheme = Get-PnPTenantTheme | Where-Object Name -eq $themeName
if($existingTheme){
  Set-PnPTenantTheme -Identity $themeName -Palette $themeJson -IsInverted $false
}else{
  Add-PnPTenantTheme -Identity $themeName -Palette $themeJson -IsInverted $false
}
Set-PnPWebTheme -Theme $themeName

try {
  Set-PnPWebHeader -Layout Compact -MenuStyle MegaMenu -HeaderBackgroundTheme 0 | Out-Null
} catch { Write-Warning "Header styling skipped: $($_.Exception.Message)" }
try {
  Set-PnPWebFooter -Enabled:$true -Layout Simple -BackgroundTheme 0 | Out-Null
} catch { Write-Warning "Footer styling skipped: $($_.Exception.Message)" }

function Ensure-NavNode {
  param([string]$Title,[string]$Url,[string]$Location='TopNavigationBar')
  $nodes = Get-PnPNavigationNode -Location $Location
  if(-not ($nodes | Where-Object { $_.Title -eq $Title })) {
    Add-PnPNavigationNode -Title $Title -Url $Url -Location $Location | Out-Null
  }
}

$web = Get-PnPWeb -Includes Url
$root = $web.Url.TrimEnd('/')
Ensure-NavNode 'Home' "$root/SitePages/SLH-Hub-Home.aspx"
Ensure-NavNode 'Transport Operations' "$root/Planning"
Ensure-NavNode 'TMS Master Data' "$root/Shared%20Documents/SLH%20Hub/TMS%20Master%20Data"
Ensure-NavNode 'Forms & Records' "$root/Shared%20Documents/SLH%20Hub/Forms%20%26%20Records"
Ensure-NavNode 'Policies & Compliance' "$root/Company%20Policies"
Ensure-NavNode 'Staffing' "$root/Staffing"
Ensure-NavNode 'Accounts - Restricted' "$root/Shared%20Documents/SLH%20Hub/Accounts%20-%20Restricted"
Ensure-NavNode 'Archive' "$root/Shared%20Documents/SLH%20Hub/Archive"

$pageName = 'SLH-Hub-Home.aspx'
$page = Get-PnPPage -Identity $pageName -ErrorAction SilentlyContinue
if(-not $page){
  $page = Add-PnPPage -Name $pageName -LayoutType Home
}else{
  $page.Controls.Clear()
  $page.Sections.Clear()
}

Add-PnPPageSection -Page $page -SectionTemplate OneColumn -Order 1 | Out-Null
Add-PnPPageTextPart -Page $page -Section 1 -Column 1 -Text @"
<div style='padding:10px 0 4px 0'>
  <h1 style='margin-bottom:4px'>Stuart Lyons Haulage Hub</h1>
  <p style='font-size:18px;margin-top:0'>Transport operations, governed master data, records and business information.</p>
</div>
"@ | Out-Null

$quickLinksProps = @{
  layoutId = 'Button'
  hideWebPartWhenEmpty = $false
  isMigrated = $true
  items = @(
    @{ title='Open TMS Portal'; sourceItem=@{ url=$TmsPortalUrl }; thumbnailType=1 },
    @{ title='Order Review'; sourceItem=@{ url="$TmsPortalUrl/orders/review" }; thumbnailType=1 },
    @{ title='Planning Board'; sourceItem=@{ url="$TmsPortalUrl/planning" }; thumbnailType=1 },
    @{ title='Live Operations'; sourceItem=@{ url="$TmsPortalUrl/live" }; thumbnailType=1 },
    @{ title='Driver Dispatch'; sourceItem=@{ url="$TmsPortalUrl/dispatch" }; thumbnailType=1 }
  )
} | ConvertTo-Json -Depth 8 -Compress
try {
  Add-PnPPageWebPart -Page $page -DefaultWebPartType QuickLinks -Section 1 -Column 1 -WebPartProperties $quickLinksProps | Out-Null
} catch { Write-Warning "Primary Quick Links web part could not be created automatically: $($_.Exception.Message)" }

Add-PnPPageSection -Page $page -SectionTemplate TwoColumn -Order 2 | Out-Null
Add-PnPPageTextPart -Page $page -Section 2 -Column 1 -Text @"
<h2>Operations</h2>
<p>Use the TMS for live orders, planning, dispatch, tracking and ETA. SharePoint supports documents, governed reference data and business-facing records.</p>
<ul>
<li><a href='$root/Shared%20Documents/SLH%20Hub/Operational%20Live%20Documents'>Operational Live Documents</a></li>
<li><a href='$root/Shared%20Documents/SLH%20Hub/TMS%20Master%20Data'>TMS Master Data</a></li>
<li><a href='$root/Shared%20Documents/SLH%20Hub/Forms%20%26%20Records'>Forms & Records</a></li>
</ul>
"@ | Out-Null

Add-PnPPageTextPart -Page $page -Section 2 -Column 2 -Text @"
<h2>Needs attention</h2>
<p>Use the governed list views for master-data warnings, integration errors and open incidents. Exceptions should be visible and actionable rather than buried in spreadsheets.</p>
<ul>
<li><a href='$root/Lists/Hub%20Integration%20Log'>Integration status</a></li>
<li><a href='$root/Lists/Hub%20Incidents%20%26%20Claims'>Incidents & Claims</a></li>
<li><a href='$root/Shared%20Documents/SLH%20Hub/TMS%20Master%20Data/90%20Validation%20%26%20Review'>Master-data validation</a></li>
</ul>
"@ | Out-Null

Add-PnPPageSection -Page $page -SectionTemplate ThreeColumn -Order 3 | Out-Null
Add-PnPPageTextPart -Page $page -Section 3 -Column 1 -Text "<h3>Customers & Sites</h3><p><a href='$root/Lists/Hub%20Customers'>Customers</a><br/><a href='$root/Lists/Hub%20Sites'>Sites</a><br/><a href='$root/Lists/Hub%20Site%20Aliases'>Aliases</a></p>" | Out-Null
Add-PnPPageTextPart -Page $page -Section 3 -Column 2 -Text "<h3>Fleet & People</h3><p><a href='$root/Lists/Hub%20Drivers'>Drivers</a><br/><a href='$root/Lists/Hub%20Vehicles'>Vehicles</a><br/><a href='$root/Lists/Hub%20Trailers'>Trailers</a></p>" | Out-Null
Add-PnPPageTextPart -Page $page -Section 3 -Column 3 -Text "<h3>Business Records</h3><p><a href='$root/Shared%20Documents/SLH%20Hub/Forms%20%26%20Records'>Forms & Records</a><br/><a href='$root/Company%20Policies'>Policies & Compliance</a><br/><a href='$root/Staffing'>Staffing</a></p>" | Out-Null

Add-PnPPageSection -Page $page -SectionTemplate OneColumn -Order 4 | Out-Null
Add-PnPPageTextPart -Page $page -Section 4 -Column 1 -Text @"
<div style='border-left:4px solid #6AA84F;padding:8px 16px;background:#F7F9FA'>
<strong>SLH data governance</strong><br/>
Operational orders, loads, runs, allocations, tracking and ETA remain controlled by the SLH TMS. SharePoint provides governed master-data projections, documents, records and business-facing operational information. Site identity uses CustomerKey + SiteKey; postcode is never treated as unique.
</div>
"@ | Out-Null

Set-PnPPage -Identity $pageName -Title 'Stuart Lyons Haulage Hub' -Publish
if($SetAsHomePage){ Set-PnPHomePage -RootFolderRelativeUrl "SitePages/$pageName" }

Write-Host "SLH Modern theme and modern Hub page provisioned: $root/SitePages/$pageName"
if(-not $SetAsHomePage){ Write-Host 'Home page was created/published but not set as the site home page. Re-run with -SetAsHomePage to switch it live.' }
