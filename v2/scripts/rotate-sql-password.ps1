param()

$ErrorActionPreference = 'Stop'

$currentSecure = Read-Host 'Enter CURRENT SQL password' -AsSecureString
$newSecure = Read-Host 'Enter NEW SQL password' -AsSecureString

$currentPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($currentSecure)
$newPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($newSecure)

try {
    $current = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($currentPtr)
    $new = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($newPtr)

    if ([string]::IsNullOrWhiteSpace($current)) { throw 'Current password cannot be empty.' }
    if ($new.Length -lt 12) { throw 'New password must be at least 12 characters.' }

    $escapedNew = $new.Replace("'", "''")
    $sql = "ALTER LOGIN [sa] WITH PASSWORD = N'$escapedNew';"

    docker exec -e "SQLCMDPASSWORD=$current" slh-tms-v2-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q $sql
    if ($LASTEXITCODE -ne 0) { throw 'SQL password change failed.' }

    Write-Host 'SQL password changed successfully.' -ForegroundColor Green
}
finally {
    if ($currentPtr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($currentPtr) }
    if ($newPtr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($newPtr) }
    Remove-Variable current,new -ErrorAction SilentlyContinue
}