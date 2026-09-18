param([string]$Root = "C:\SLH-TMS-LOCAL")
$ErrorActionPreference = "Stop"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = Join-Path $Root "backups"
$stage = Join-Path $backupRoot "stage-$timestamp"
$zip = Join-Path $backupRoot "SLH-TMS-LOCAL-$timestamp.zip"

& (Join-Path $Root "source\slh-tms-api\local-server\stop-local.ps1") -Root $Root
sqllocaldb stop MSSQLLocalDB | Out-Null

New-Item -ItemType Directory -Force -Path $stage | Out-Null
foreach ($folder in @("data","customer-files","logs")) {
    $source = Join-Path $Root $folder
    if (Test-Path $source) { Copy-Item $source -Destination (Join-Path $stage $folder) -Recurse -Force }
}
if (Test-Path (Join-Path $Root "local-package.json")) { Copy-Item (Join-Path $Root "local-package.json") $stage -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force
sqllocaldb start MSSQLLocalDB | Out-Null
Write-Host "Backup created: $zip"
Write-Host "The TMS remains stopped after backup. Start it again with start-local.ps1."
