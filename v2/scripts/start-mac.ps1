param(
    [string]$SaPassword = $env:SLH_TMS_V2_SA_PASSWORD
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$apiRepo = Resolve-Path (Join-Path $scriptRoot '..\..')
$api = Join-Path $apiRepo 'v2/Slh.Tms.V2.Api'
$parent = Split-Path -Parent $apiRepo
$web = Join-Path $parent 'slh-tms-web/v2'

if ([string]::IsNullOrWhiteSpace($SaPassword)) {
    $secure = Read-Host 'Enter the local V2 SQL SA password' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $SaPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

if (-not (Test-Path $web)) { throw "Web repo not found at $web. Keep slh-tms-api and slh-tms-web beside each other." }

$connection = "Server=localhost,14333;Database=SLH_TMS_V2_DEV;User Id=sa;Password=$SaPassword;TrustServerCertificate=True;Encrypt=False;MultipleActiveResultSets=true"
$escapedConnection = $connection.Replace("'", "''")
$apiCommand = "`$env:TMS_V2_SQL_CONNECTION='$escapedConnection'; Set-Location '$api'; dotnet run --launch-profile 'SLH TMS V2 Local'"
$webCommand = "Set-Location '$web'; npm run dev -- --host 0.0.0.0"

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

Write-Host 'Launching API and portal...' -ForegroundColor Green
Start-Process pwsh -ArgumentList '-NoExit','-Command',$apiCommand
Start-Sleep -Seconds 3
Start-Process pwsh -ArgumentList '-NoExit','-Command',$webCommand
Start-Sleep -Seconds 3
Start-Process 'http://localhost:5180'

Write-Host 'Portal: http://localhost:5180' -ForegroundColor Green
Write-Host 'API:    http://localhost:5080' -ForegroundColor Green