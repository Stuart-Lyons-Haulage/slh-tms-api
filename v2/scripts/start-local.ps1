param(
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$api = Join-Path $root 'Slh.Tms.V2.Api'
$webRepo = Resolve-Path (Join-Path $root '..\..\..\slh-tms-web\v2') -ErrorAction SilentlyContinue

Write-Host 'SLH TMS V2 local start' -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 8 SDK is not installed or not on PATH.'
}
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'Node.js is not installed or not on PATH.'
}

if (-not $webRepo) {
    throw 'Could not find slh-tms-web beside slh-tms-api. Clone both repos into the same parent folder.'
}

Push-Location $api
try {
    Write-Host 'Restoring API...' -ForegroundColor DarkCyan
    dotnet restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    Write-Host 'Building API...' -ForegroundColor DarkCyan
    dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
}
finally {
    Pop-Location
}

Push-Location $webRepo
try {
    if (-not (Test-Path 'node_modules')) {
        Write-Host 'Installing web packages...' -ForegroundColor DarkCyan
        npm install
        if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
    }

    Write-Host 'Building portal...' -ForegroundColor DarkCyan
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'npm build failed.' }
}
finally {
    Pop-Location
}

Write-Host 'Starting API on http://localhost:5080' -ForegroundColor Green
Start-Process powershell -ArgumentList '-NoExit','-Command', "Set-Location '$api'; dotnet run --launch-profile 'SLH TMS V2 Local'"

Start-Sleep -Seconds 3

Write-Host 'Starting portal on http://localhost:5180' -ForegroundColor Green
Start-Process powershell -ArgumentList '-NoExit','-Command', "Set-Location '$webRepo'; npm run dev -- --host 0.0.0.0"

if (-not $NoBrowser) {
    Start-Sleep -Seconds 3
    Start-Process 'http://localhost:5180'
}

Write-Host ''
Write-Host 'Two PowerShell windows have been opened: API and Portal.' -ForegroundColor Cyan
Write-Host 'Keep them open while testing. Close them to stop V2.' -ForegroundColor Cyan
