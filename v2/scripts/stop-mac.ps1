param()

$ErrorActionPreference = 'Continue'
$scriptRoot = $PSScriptRoot
$apiRepo = Resolve-Path (Join-Path $scriptRoot '..\..')
$runDir = Join-Path $apiRepo '.v2-local'
$pidFile = Join-Path $runDir 'pids.json'

if (Test-Path $pidFile) {
    try {
        $pids = Get-Content -Raw $pidFile | ConvertFrom-Json
        foreach ($processId in @($pids.apiPid, $pids.webPid)) {
            if ($processId) {
                Stop-Process -Id ([int]$processId) -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
        Write-Host 'Could not read saved PIDs; falling back to port cleanup.' -ForegroundColor Yellow
    }
}

foreach ($port in 5080, 5180) {
    $ids = @(lsof -ti "tcp:$port" 2>$null)
    foreach ($processId in $ids) {
        if ($processId) { Stop-Process -Id ([int]$processId) -Force -ErrorAction SilentlyContinue }
    }
}

Remove-Item $pidFile -ErrorAction SilentlyContinue
Write-Host 'SLH TMS V2 local API and portal stopped.' -ForegroundColor Green
