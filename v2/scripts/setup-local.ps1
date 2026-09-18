param(
    [string]$Instance = '(localdb)\MSSQLLocalDB'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sqlFile = Join-Path $PSScriptRoot 'local-schema.sql'

Write-Host 'SLH TMS V2 local database setup' -ForegroundColor Cyan

if (-not (Get-Command sqllocaldb -ErrorAction SilentlyContinue)) {
    Write-Host 'SQL Server LocalDB is not installed.' -ForegroundColor Yellow
    Write-Host 'Install SQL Server Express LocalDB, then run this script again.' -ForegroundColor Yellow
    exit 2
}

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    Write-Host 'sqlcmd is not installed.' -ForegroundColor Yellow
    Write-Host 'Install Microsoft SQL command-line utilities, then run this script again.' -ForegroundColor Yellow
    exit 3
}

Write-Host 'Starting LocalDB...' -ForegroundColor DarkCyan
sqllocaldb start MSSQLLocalDB | Out-Null

Write-Host 'Creating/updating SLH_TMS_V2_DEV...' -ForegroundColor DarkCyan
sqlcmd -S $Instance -E -b -i $sqlFile
if ($LASTEXITCODE -ne 0) { throw 'Database setup failed.' }

Write-Host 'Local V2 database is ready.' -ForegroundColor Green
