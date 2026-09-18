param(
    [string]$SaPassword
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$apiRepo = Resolve-Path (Join-Path $scriptRoot '..\..')
$api = Join-Path $apiRepo 'v2/Slh.Tms.V2.Api'
$parent = Split-Path -Parent $apiRepo
$web = Join-Path $parent 'slh-tms-web/v2'
$runDir = Join-Path $apiRepo '.v2-local'
$apiOut = Join-Path $runDir 'api.out.log'
$apiErr = Join-Path $runDir 'api.err.log'
$webOut = Join-Path $runDir 'web.out.log'
$webErr = Join-Path $runDir 'web.err.log'
$pidFile = Join-Path $runDir 'pids.json'

if ([string]::IsNullOrWhiteSpace($SaPassword)) {
    $secure = Read-Host 'Enter the local V2 SQL SA password' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $SaPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

if (-not (Test-Path $web)) { throw "Web repo not found at $web. Keep slh-tms-api and slh-tms-web beside each other." }
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$connection = "Server=localhost,14333;Database=SLH_TMS_V2_DEV;User Id=sa;Password=$SaPassword;TrustServerCertificate=True;Encrypt=False;MultipleActiveResultSets=true"

Write-Host 'Building API...' -ForegroundColor Cyan
Push-Location $api
try {
    dotnet restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
}
finally { Pop-Location }

Write-Host 'Building portal...' -ForegroundColor Cyan
Push-Location $web
try {
    if (-not (Test-Path 'node_modules')) {
        npm install
        if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
    }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'npm build failed.' }
}
finally { Pop-Location }

Write-Host 'Launching API and portal in the background...' -ForegroundColor Green

$apiProcess = Start-Process dotnet `
    -WorkingDirectory $api `
    -ArgumentList @('run','--no-build','--urls','http://localhost:5080') `
    -Environment @{ TMS_V2_SQL_CONNECTION = $connection; ASPNETCORE_ENVIRONMENT = 'Development' } `
    -RedirectStandardOutput $apiOut `
    -RedirectStandardError $apiErr `
    -PassThru

$webProcess = Start-Process npm `
    -WorkingDirectory $web `
    -ArgumentList @('run','dev','--','--host','0.0.0.0') `
    -RedirectStandardOutput $webOut `
    -RedirectStandardError $webErr `
    -PassThru

@{
    apiPid = $apiProcess.Id
    webPid = $webProcess.Id
    startedAt = (Get-Date).ToString('o')
} | ConvertTo-Json | Set-Content $pidFile

Write-Host 'Waiting for the API health check...' -ForegroundColor DarkCyan
$healthy = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $response = Invoke-WebRequest 'http://localhost:5080/health/ready' -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) {
            $healthy = $true
            break
        }
    }
    catch {
        Start-Sleep -Seconds 1
    }
}

if (-not $healthy) {
    Write-Host 'API did not become healthy. Recent API error log:' -ForegroundColor Red
    if (Test-Path $apiErr) { Get-Content $apiErr -Tail 40 }
    throw 'Local V2 API failed to start.'
}

Start-Process open -ArgumentList 'http://localhost:5180' | Out-Null

Write-Host ''
Write-Host 'SLH TMS V2 is running in the background.' -ForegroundColor Green
Write-Host 'Your PowerShell prompt remains free to use.' -ForegroundColor Green
Write-Host 'Portal: http://localhost:5180'
Write-Host 'API:    http://localhost:5080'
Write-Host "Logs:   $runDir"
Write-Host 'Stop it later with: ./stop-mac.ps1' -ForegroundColor Yellow
