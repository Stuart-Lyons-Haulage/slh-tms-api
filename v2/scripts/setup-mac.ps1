param(
    [string]$SaPassword = $env:SLH_TMS_V2_SA_PASSWORD
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptRoot
$composeFile = Join-Path $repoRoot 'docker-compose.mac.yml'
$sqlFile = Join-Path $scriptRoot 'local-schema.sql'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker is not installed.' }

if ([string]::IsNullOrWhiteSpace($SaPassword)) {
    $secure = Read-Host 'Enter a strong temporary SA password for local V2 SQL' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $SaPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

if ($SaPassword.Length -lt 12) { throw 'Use a password of at least 12 characters with upper/lowercase, a number and a symbol.' }
$env:SLH_TMS_V2_SA_PASSWORD = $SaPassword

Write-Host 'Starting SQL Server 2022 container for SLH TMS V2...' -ForegroundColor Cyan
docker compose -f $composeFile up -d
if ($LASTEXITCODE -ne 0) { throw 'Docker SQL start failed.' }

Write-Host 'Waiting for SQL Server...' -ForegroundColor DarkCyan
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    docker exec slh-tms-v2-sql /bin/bash -lc "if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '$SaPassword' -C -Q 'SELECT 1'; elif [ -x /opt/mssql-tools/bin/sqlcmd ]; then /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P '$SaPassword' -Q 'SELECT 1'; else exit 127; fi" 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 2
}

if (-not $ready) {
    docker logs --tail 100 slh-tms-v2-sql
    throw 'SQL Server did not become ready.'
}

Write-Host 'Applying V2 schema...' -ForegroundColor DarkCyan
$temp = [System.IO.Path]::GetTempFileName()
Copy-Item $sqlFile $temp -Force
try {
    docker cp $temp slh-tms-v2-sql:/tmp/slh-v2-schema.sql | Out-Null
    docker exec slh-tms-v2-sql /bin/bash -lc "if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '$SaPassword' -C -b -i /tmp/slh-v2-schema.sql; else /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P '$SaPassword' -b -i /tmp/slh-v2-schema.sql; fi"
    if ($LASTEXITCODE -ne 0) { throw 'Schema bootstrap failed.' }
}
finally { Remove-Item $temp -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host 'SLH TMS V2 SQL is ready on localhost:14333' -ForegroundColor Green
Write-Host 'Database: SLH_TMS_V2_DEV' -ForegroundColor Green
Write-Host 'Keep this password private. It is not written to GitHub.' -ForegroundColor Yellow