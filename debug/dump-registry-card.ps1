param(
    [string]$TweakId = 'menu-show-delay'
)

$root = Split-Path -Parent $PSScriptRoot
$buildDirApp = Join-Path $root 'src\TidyWindow.App\bin\Debug\net8.0-windows'
$buildDirCore = Join-Path $root 'src\TidyWindow.Core\bin\Debug\net8.0'

$assemblyApp = Join-Path $buildDirApp 'TidyWindow.App.dll'
$assemblyCore = Join-Path $buildDirCore 'TidyWindow.Core.dll'

if (-not (Test-Path $assemblyApp)) { throw "Build output not found: $assemblyApp" }
if (-not (Test-Path $assemblyCore)) { throw "Build output not found: $assemblyCore" }

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase | Out-Null
Add-Type -Path $assemblyCore | Out-Null
Add-Type -Path $assemblyApp | Out-Null

$baseDir = [System.AppContext]::BaseDirectory
Write-Host "AppContext.BaseDirectory = $baseDir" -ForegroundColor DarkCyan

$invoker = [Activator]::CreateInstance([TidyWindow.Core.Automation.PowerShellInvoker])
$optimizer = [Activator]::CreateInstance([TidyWindow.Core.Maintenance.RegistryOptimizerService], $invoker)
$stateService = [Activator]::CreateInstance([TidyWindow.Core.Maintenance.RegistryStateService], $invoker, $optimizer)
$preferences = [Activator]::CreateInstance([TidyWindow.Core.Maintenance.RegistryPreferenceService])

$tweakDefinition = $optimizer.Tweaks | Where-Object { $_.Id -eq $TweakId }
if (-not $tweakDefinition) {
    Write-Host "Available tweak ids:" -ForegroundColor Yellow
    foreach ($item in $optimizer.Tweaks) {
        Write-Host " - $($item.Id)"
    }
    throw "Tweak '$TweakId' not found."
}

$card = [Activator]::CreateInstance([TidyWindow.App.ViewModels.RegistryTweakCardViewModel], @(
        $tweakDefinition,
        $tweakDefinition.Name,
        $tweakDefinition.Summary,
        $tweakDefinition.RiskLevel,
        $preferences
    ))

$stateTask = $stateService.GetStateAsync($TweakId, $true)
$null = $stateTask.Wait()
$state = $stateTask.Result
$card.UpdateState($state)

[pscustomobject]@{
    Id = $card.Id
    CurrentValue = $card.CurrentValue
    RecommendedValue = $card.RecommendedValue
    CustomValue = $card.CustomValue
    SupportsCustom = $card.SupportsCustomValue
    CurrentDisplayRaw = ($state.Values | Select-Object -First 1).CurrentDisplay
    CurrentValueRaw = ($state.Values | Select-Object -First 1).CurrentValue
}
