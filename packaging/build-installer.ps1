# Builds the DualNet portable zip + MSI installer on Windows.
# Called by .github/workflows/build-release.yml (and usable locally).
#
# Inputs (env):
#   VERSION      e.g. 4.1.2
#   UPGRADE_CODE stable MSI upgrade code
# Outputs (out/):
#   DualNetDownloader-<VERSION>-setup.msi
#   DualNetDownloader-<VERSION>-win-x64.zip
#   DualNetDownloader-<VERSION>.sha256   (both SHA256s)
param([Parameter(Mandatory=$true)][string]$Version)

$ErrorActionPreference = "Stop"

if (-not $env:UPGRADE_CODE) { $env:UPGRADE_CODE = "{9C7B7F3A-52D4-4E62-A8F1-D6B4C2E77A10}" }

$repoRoot = Split-Path -Parent $PSScriptRoot
$pub      = Join-Path $repoRoot "out\publish"
$out      = Join-Path $repoRoot "out"

Write-Host "== test =="
# CI runners are slow/noisy: skip the 3 timing-sensitive localhost-timing tests there.
# They keep running in the default (full) suite on Linux/local runs.
dotnet test (Join-Path $repoRoot "tests\DualNet.Tests\DualNet.Tests.csproj") -c Release --nologo --filter "Category!=TimingSensitive"
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

Write-Host "== publish win-x64 self-contained =="
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
dotnet publish (Join-Path $repoRoot "src\DualNet.App\DualNet.App.csproj") -c Release -r win-x64 `
  --self-contained true -p:EnableWindowsTargeting=true -p:Version=$Version -o $pub --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "== bundle ijwhost.dll (required for C++/CLI mixed-mode CefSharp) =="
# Find ijwhost.dll in: 1) user nuget cache  2) machine-wide dotnet packs  3) NuGet download
$ijw = $null
$ijw = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.netcore.app.host.win-x64" -Recurse -Filter ijwhost.dll -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $ijw) {
  $ijw = Get-ChildItem "C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64" -Recurse -Filter ijwhost.dll -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $ijw) {
  Write-Host "nuget cache + packs missing it; downloading package..."
  $nupkg = Join-Path $env:TEMP "hostpack.nupkg"
  Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.host.win-x64/8.0.32/microsoft.netcore.app.host.win-x64.8.0.32.nupkg" -OutFile $nupkg
  $hpDir = Join-Path $env:TEMP "hostpack"
  if (Test-Path $hpDir) { Remove-Item -Recurse -Force $hpDir }
  New-Item -ItemType Directory -Path $hpDir | Out-Null
  Copy-Item $nupkg (Join-Path $hpDir "h.zip") -Force
  Expand-Archive -Path (Join-Path $hpDir "h.zip") -DestinationPath $hpDir -Force
  $ijw = Get-ChildItem $hpDir -Recurse -Filter ijwhost.dll | Select-Object -First 1
}
if (-not $ijw) { throw "ijwhost.dll not found anywhere" }
$ijwDest = Join-Path $pub "ijwhost.dll"
Copy-Item $ijw.FullName $ijwDest -Force
# Hard verify: the copy MUST land in the publish dir (v4.1.2 taught us this file
# is life-or-death for CefSharp startup — never trust a silent copy).
if (-not (Test-Path $ijwDest) -or (Get-Item $ijwDest).Length -le 0) { throw "ijwhost.dll copy failed -> $ijwDest" }
Write-Host "bundled: $($ijw.FullName) -> $ijwDest ($((Get-Item $ijwDest).Length) bytes)"

Write-Host "== bundle VC++ runtime DLLs =="
$appx = Join-Path $env:TEMP "Microsoft.VCLibs.x64.14.00.Desktop.appx"
$zip  = Join-Path $env:TEMP "Microsoft.VCLibs.x64.14.00.Desktop.zip"
Invoke-WebRequest -Uri "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx" -OutFile $appx
Copy-Item $appx $zip -Force
$want = @("msvcp140.dll","msvcp140_1.dll","msvcp140_2.dll","msvcp140_atomic_wait.dll",
          "msvcp140_codecvt_ids.dll","vcruntime140.dll","vcruntime140_1.dll")
Expand-Archive -Path $zip -DestinationPath (Join-Path $env:TEMP "vclibs") -Force
foreach ($d in $want) {
  $src = Get-ChildItem (Join-Path $env:TEMP "vclibs") -Recurse -Filter $d | Select-Object -First 1
  if (-not $src) { throw "VC runtime dll missing in appx: $d" }
  $dst = Join-Path $pub $d
  Copy-Item $src.FullName $dst -Force
  if (-not (Test-Path $dst) -or (Get-Item $dst).Length -le 0) { throw "VC runtime copy failed: $dst" }
}
Write-Host "bundled $($want.Count)/$($want.Count) VC runtime DLLs (verified present in publish dir)"

Write-Host "== strip PDBs =="
Get-ChildItem $pub -Filter *.pdb | Remove-Item -Force

$pubFiles = @(Get-ChildItem $pub -Recurse -File)
Write-Host "publish dir ready: $($pubFiles.Count) files, $([math]::Round(($pubFiles | Measure-Object Length -Sum).Sum / 1MB, 1)) MB"
# Belt-and-braces: the 8 manually bundled files MUST all be present right now.
foreach ($must in (@("ijwhost.dll") + $want)) {
  if (-not (Test-Path (Join-Path $pub $must))) { throw "$must missing from publish dir before packaging" }
}

Write-Host "== portable zip (deterministic python builder — Compress-Archive silently dropped ijwhost.dll on CI in 4.1.2) =="
$zipOut = Join-Path $out "DualNetDownloader-$Version-win-x64.zip"
if (Test-Path $zipOut) { Remove-Item -Force $zipOut }
python (Join-Path $repoRoot "packaging\make_zip.py") --pub $pub --out $zipOut
if ($LASTEXITCODE -ne 0) { throw "zip build failed" }

Write-Host "== WiX v6 =="
dotnet tool install --global wix --version 6.0.2 2>$null; dotnet tool update --global wix --version 6.0.2 | Out-Null
$env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
$wixVer = (wix --version)
Write-Host "wix: $wixVer"

Write-Host "== fetch UI extension (version-matched wixext) =="
$extDir = Join-Path $env:TEMP "wixuiext"
if (Test-Path $extDir) { Remove-Item -Recurse -Force $extDir }
New-Item -ItemType Directory -Path $extDir | Out-Null
$nupkg = Join-Path $extDir "ui.wixext.nupkg"
Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/wixtoolset.ui.wixext/6.0.2/wixtoolset.ui.wixext.6.0.2.nupkg" -OutFile $nupkg
Copy-Item $nupkg (Join-Path $extDir "ui.zip") -Force
Expand-Archive -Path (Join-Path $extDir "ui.zip") -DestinationPath $extDir -Force
$extDll = Get-ChildItem $extDir -Recurse -Filter "WixToolset.UI.wixext.dll" | Select-Object -First 1
if (-not $extDll) { throw "WixToolset.UI.wixext.dll not found in nupkg" }
Write-Host "extension: $($extDll.FullName)"

Write-Host "== generate wxs + build MSI =="
$rtf = Join-Path $out "LICENSE.rtf"
python (Join-Path $repoRoot "packaging\make-license-rtf.py") `
  --license (Join-Path $repoRoot "LICENSE") --out $rtf
python (Join-Path $repoRoot "packaging\gen_wxs.py") `
  --pub $pub --out (Join-Path $out "DualNet.wxs") --version $Version `
  --ico (Join-Path $repoRoot "src\DualNet.App\Assets\app.ico") `
  --license-rtf $rtf --upgrade-code $env:UPGRADE_CODE

$msi = Join-Path $out "DualNetDownloader-$Version-setup.msi"
wix build -arch x64 -ext $extDll.FullName (Join-Path $out "DualNet.wxs") -o $msi
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $msi)) { throw "wix build failed" }

Write-Host "== verify MSI contents (File table vs publish dir) =="
& (Join-Path $repoRoot "packaging\verify_msi.ps1") -Msi $msi -PubDir $pub
if ($LASTEXITCODE -ne 0) { throw "MSI content verification failed" }

Write-Host "== sha256 =="
$shaFile = Join-Path $out "DualNetDownloader-$Version.sha256"
$hashZip = (Get-FileHash $zipOut -Algorithm SHA256).Hash.ToLower()
$hashMsi = (Get-FileHash $msi   -Algorithm SHA256).Hash.ToLower()
"$hashZip  DualNetDownloader-$Version-win-x64.zip`n$hashMsi  DualNetDownloader-$Version-setup.msi" | Set-Content $shaFile

Write-Host "== release-gate: verify MSI + zip contents =="
python (Join-Path $repoRoot "packaging\verify_outputs.py") --zip $zipOut --msi $msi

Write-Host "DONE:"
Get-Item $msi, $zipOut, $shaFile | Select-Object Name, Length
