param([string]$Root = "C:\SLH-TMS-LOCAL")
$run = Join-Path $Root "run"
foreach ($name in @("web","api")) {
    $pidFile = Join-Path $run "$name.pid"
    if (-not (Test-Path $pidFile)) { continue }
    $processId = Get-Content $pidFile | Select-Object -First 1
    if ($processId -match '^\d+$') { Stop-Process -Id ([int]$processId) -Force -ErrorAction SilentlyContinue }
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
}
Write-Host "SLH TMS local processes stopped."
