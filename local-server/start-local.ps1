param(
    [string]$Root = "C:\SLH-TMS-LOCAL",
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
$apiPath = Join-Path $Root "source\slh-tms-api"
$webPath = Join-Path $Root "source\slh-tms-web"
$logs = Join-Path $Root "logs"
$run = Join-Path $Root "run"

foreach ($path in @($apiPath,$webPath,$logs,$run,(Join-Path $Root "data"),(Join-Path $Root "customer-files"))) {
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
}

$env:ASPNETCORE_ENVIRONMENT = "LocalTest"
$env:LocalTest__Enabled = "true"
$env:LocalTest__EnableExternalIntegrations = "false"
$env:LocalTest__AllowExternalWrites = "false"
$env:LocalStorage__Root = (Join-Path $Root "customer-files")
$env:LocalStorage__DatabaseRoot = (Join-Path $Root "data")
$env:ConnectionStrings__TmsDb = "Server=(localdb)\MSSQLLocalDB;Database=SLH_TMS_LOCAL;Integrated Security=true;TrustServerCertificate=true;MultipleActiveResultSets=true"

$apiArgs = @("run","--no-build","--environment","LocalTest","--urls","http://127.0.0.1:5099")
$apiProcess = Start-Process -FilePath "dotnet" -ArgumentList $apiArgs -WorkingDirectory $apiPath -RedirectStandardOutput (Join-Path $logs "api.out.log") -RedirectStandardError (Join-Path $logs "api.err.log") -PassThru

$webArgs = @("run","dev:local","--","--host","127.0.0.1","--port","5173")
$webProcess = Start-Process -FilePath "npm.cmd" -ArgumentList $webArgs -WorkingDirectory $webPath -RedirectStandardOutput (Join-Path $logs "web.out.log") -RedirectStandardError (Join-Path $logs "web.err.log") -PassThru

$apiProcess.Id | Set-Content (Join-Path $run "api.pid")
$webProcess.Id | Set-Content (Join-Path $run "web.pid")

$healthy = $false
for ($i=0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:5099/api/v1/health" -TimeoutSec 2
        if ($health.status -eq "healthy" -and $health.localTestMode -eq $true) { $healthy = $true; break }
    } catch {}
}

if (-not $healthy) {
    Write-Warning "API did not report healthy local-test mode. Check the API logs under $logs."
    exit 1
}

Write-Host "SLH TMS LOCAL TEST is running."
Write-Host "Portal: http://127.0.0.1:5173"
Write-Host "API:    http://127.0.0.1:5099"
Write-Host "Files:  $(Join-Path $Root 'customer-files')"
Write-Host "Data:   $(Join-Path $Root 'data')"
Write-Host "Logs:   $logs"

if (-not $NoBrowser) { Start-Process "http://127.0.0.1:5173" }
