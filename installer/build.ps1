# Builds TidyWindow-Setup-<version>.exe from a self-contained publish folder.
#   pwsh installer/build.ps1 -Version 5.0.0 -PublishDir artifacts/publish
# Needs Inno Setup 6 (winget install JRSoftware.InnoSetup, or choco install innosetup).
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PublishDir,
    [string]$OutDir = 'artifacts'
)
$ErrorActionPreference = 'Stop'

$candidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source }
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found.' }

$publish = (Resolve-Path $PublishDir).Path
foreach ($path in 'TidyWindow.Next.exe', 'CleanHost\TidyWindow.CleanHost.exe', 'ScoutHost\TidyWindow.SearchHost.exe') {
    if (-not (Test-Path (Join-Path $publish $path))) { throw "The publish folder is missing $path." }
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$out = (Resolve-Path $OutDir).Path

& $iscc /Qp "/DMyAppVersion=$Version" "/DBuildOutput=$publish" "/O$out" (Join-Path $PSScriptRoot 'TidyWindow.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$setup = Join-Path $out "TidyWindow-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Expected $setup was not produced." }
$setup
