param(
    [Parameter(Mandatory=$true)][string]$PublishPath,
    [string]$ServiceName = "SLH TMS API",
    [string]$EnvironmentName = "ProductionServer"
)

$exe = Join-Path $PublishPath "Slh.Tms.Api.exe"
if (!(Test-Path $exe)) {
    throw "API executable not found at $exe. Publish the API before installing the service."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
}

New-Service `
    -Name $ServiceName `
    -BinaryPathName "`"$exe`" --environment $EnvironmentName" `
    -DisplayName "SLH TMS API" `
    -Description "Stuart Lyons Haulage TMS API and background integration host." `
    -StartupType Automatic

Start-Service -Name $ServiceName
