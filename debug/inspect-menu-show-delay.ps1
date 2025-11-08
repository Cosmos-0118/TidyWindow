param()

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$detectorPath = Join-Path $repoRoot 'automation\registry\get-registry-state.ps1'

if (-not (Test-Path -Path $detectorPath)) {
    Write-Error "Unable to locate registry detection script at '$detectorPath'."
    exit 1
}

Write-Host "Reading HKCU MenuShowDelay value via registry detection script..." -ForegroundColor Cyan

& $detectorPath -RegistryPath "HKCU:\Control Panel\Desktop" -ValueName "MenuShowDelay" -ValueType "String" -SupportsCustomValue:$true -RecommendedValue "60"
