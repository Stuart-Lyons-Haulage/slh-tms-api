param(
    [string]$Root = "C:\SLH-TMS-LOCAL"
)

$ErrorActionPreference = "Stop"
$source = Join-Path $Root "source"
$apiPath = Join-Path $source "slh-tms-api"
$webPath = Join-Path $source "slh-tms-web"

Write-Host "Preparing SLH TMS local test workspace at $Root"

foreach ($cmd in @("git","dotnet","node","npm","sqllocaldb")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        throw "Required command '$cmd' was not found. Install it before continuing."
    }
}

foreach ($dir in @($Root,$source,(Join-Path $Root "data"),(Join-Path $Root "customer-files"),(Join-Path $Root "backups"),(Join-Path $Root "logs"),(Join-Path $Root "run"))) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

if (-not (Test-Path $apiPath)) {
    git clone --branch local-server-edition https://github.com/Stuart-Lyons-Haulage/slh-tms-api.git $apiPath
} else {
    git -C $apiPath fetch origin local-server-edition
    git -C $apiPath checkout local-server-edition
    git -C $apiPath pull --ff-only origin local-server-edition
}

if (-not (Test-Path $webPath)) {
    git clone --branch local-server-edition https://github.com/Stuart-Lyons-Haulage/slh-tms-web.git $webPath
} else {
    git -C $webPath fetch origin local-server-edition
    git -C $webPath checkout local-server-edition
    git -C $webPath pull --ff-only origin local-server-edition
}

Push-Location $apiPath
dotnet restore
dotnet build -c Debug --no-restore
Pop-Location

Push-Location $webPath
npm ci
npm run build:local
Pop-Location

@{
    root = $Root
    preparedAt = (Get-Date).ToString("o")
    apiBranch = "local-server-edition"
    webBranch = "local-server-edition"
} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Root "local-package.json")

Write-Host ""
Write-Host "Local package prepared successfully."
Write-Host "Run start-local.ps1 from the API local-server folder."
