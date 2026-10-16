# Verifies that every file in the publish directory made it into the MSI's
# File table. Catches packaging bugs the OLE-signature check cannot see
# (e.g. a file missing from the generated .wxs would silently ship a broken
# installer). Runs on Windows only (uses the WindowsInstaller COM server).
#
# Usage: verify_msi.ps1 -Msi <path.msi> -PubDir <publish-dir>
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    [Parameter(Mandatory = $true)][string]$PubDir
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Msi)) { throw "MSI not found: $Msi" }
if (-not (Test-Path $PubDir)) { throw "publish dir not found: $PubDir" }

try {
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember(
    "OpenDatabase", "InvokeMethod", $null, $installer, @($Msi, 0))
$view = $database.GetType().InvokeMember(
    "OpenView", "InvokeMethod", $null, $database, @('SELECT `File` FROM File'))
$view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)

$msiFiles = New-Object 'System.Collections.Generic.HashSet[string]'
while ($true) {
    $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
    if ($null -eq $record) { break }
    $value = $record.GetType().InvokeMember(
        "StringData", "GetProperty", $null, $record, @(1))
    # File table names may be "shortname|longname" — keep the long name.
    $name = if ($value -like "*|*") { ($value -split '\|')[-1] } else { $value }
    [void]$msiFiles.Add($name.ToLowerInvariant())
}
$view.GetType().InvokeMember("Close", "InvokeMethod", $null, $view, $null)

[System.Runtime.InteropServices.Marshal]::ReleaseComObject($view) | Out-Null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($database) | Out-Null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null

$pubFiles = @(Get-ChildItem $PubDir -Recurse -File |
    ForEach-Object { $_.Name.ToLowerInvariant() } | Sort-Object -Unique)

$missing = @($pubFiles | Where-Object { -not $msiFiles.Contains($_) })

if ($missing.Count -gt 0) {
    Write-Host "MSI VERIFY FAILED — $($missing.Count) publish file(s) missing from the MSI File table:"
    $missing | Select-Object -First 25 | ForEach-Object { Write-Host "  missing: $_" }
    exit 1
}

Write-Host "OK: MSI File table contains every publish file ($($pubFiles.Count) unique files, $($msiFiles.Count) File rows)"

} catch {
    Write-Host "MSI VERIFY FAILED — unexpected error: $_"
    exit 1
}
