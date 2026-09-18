param()

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptRoot
$composeFile = Join-Path $repoRoot 'docker-compose.mac.yml'

Write-Host 'This resets ONLY the local SLH TMS V2 Docker SQL database.' -ForegroundColor Yellow
Write-Host 'It removes the current V2 dev container and its Docker volume, then recreates a clean database.' -ForegroundColor Yellow

$confirm = Read-Host 'Type RESET to continue'
if ($confirm -ne 'RESET') {
    Write-Host 'Cancelled.' -ForegroundColor Yellow
    exit 0
}

$secure = Read-Host 'Choose a NEW local SQL SA password' -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $SaPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }

if ($SaPassword.Length -lt 12) {
    throw 'Use a password of at least 12 characters with upper/lowercase, a number and a symbol.'
}

$env:SLH_TMS_V2_SA_PASSWORD = $SaPassword

Write-Host 'Removing current V2 SQL container and local dev volume...' -ForegroundColor Cyan
docker compose -f $composeFile down -v --remove-orphans
if ($LASTEXITCODE -ne 0) { throw 'Failed to remove the current V2 SQL container/volume.' }

Write-Host 'Creating a fresh V2 SQL container...' -ForegroundColor Cyan
docker compose -f $composeFile up -d
if ($LASTEXITCODE -ne 0) { throw 'Failed to create the fresh V2 SQL container.' }

Write-Host 'Waiting for SQL Server engine...' -ForegroundColor DarkCyan
$ready = $false
for ($i = 0; $i -lt 90; $i++) {
    $logs = docker logs slh-tms-v2-sql 2>&1
    if ($logs -match 'SQL Server is now ready for client connections') { $ready = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $ready) {
    docker logs --tail 100 slh-tms-v2-sql
    throw 'Fresh SQL Server container did not become ready.'
}

Write-Host 'Verifying the new password...' -ForegroundColor DarkCyan
docker exec -e "SQLCMDPASSWORD=$SaPassword" slh-tms-v2-sql /bin/bash -lc "if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q 'SELECT 1'; else /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -b -Q 'SELECT 1'; fi" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fresh SQL container started, but the new SA password could not be verified.' }

Write-Host ''
Write-Host 'Fresh local V2 SQL is ready.' -ForegroundColor Green
Write-Host 'Now run: ./setup-mac.ps1 -SaPassword <your password>' -ForegroundColor Green
Write-Host 'Then run: ./start-mac.ps1 -SaPassword <your password>' -ForegroundColor Green