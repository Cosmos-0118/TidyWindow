param(
    [string]$TweakId = 'menu-show-delay'
)

$root = Split-Path -Parent $PSScriptRoot
$buildDirApp = Join-Path $root 'src\TidyWindow.App\bin\Debug\net8.0-windows'
$buildDirCore = Join-Path $root 'src\TidyWindow.Core\bin\Debug\net8.0'

$assemblyAppCandidates = Get-ChildItem -Path $buildDirApp -Include 'TidyWindow.App.dll', 'TidyWindow.dll' -File -Recurse -ErrorAction SilentlyContinue `
    | Where-Object { $_.Name -notlike 'TidyWindow.Core.dll' }

$assemblyApp = $assemblyAppCandidates `
    | Sort-Object -Property LastWriteTimeUtc -Descending `
    | Select-Object -First 1

$assemblyCore = Join-Path $buildDirCore 'TidyWindow.Core.dll'

if (-not $assemblyApp) {
    throw "Build output not found. Expected to locate TidyWindow.App.dll or TidyWindow.dll beneath $buildDirApp."
}
Write-Host "Using App assembly: $($assemblyApp.FullName)" -ForegroundColor DarkCyan
if (-not (Test-Path $assemblyCore)) { throw "Build output not found: $assemblyCore" }

# Ensure AppContext resolves assets relative to the repository root so the
# configuration and automation scripts can be discovered when loading the
# view-model types outside the packaged app.
[AppDomain]::CurrentDomain.SetData('APP_CONTEXT_BASE_DIRECTORY', $root)

# Load dependencies and target assemblies explicitly. Using LoadFrom ensures the
# assemblies are loaded even if PowerShell's type resolver doesn't find them by
# name. Avoid relying on [Type] resolution for generative/source-generated types.
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase | Out-Null
[System.Reflection.Assembly]::LoadFrom($assemblyCore) | Out-Null
[System.Reflection.Assembly]::LoadFrom($assemblyApp.FullName) | Out-Null

$baseDir = [System.AppContext]::BaseDirectory
Write-Host "AppContext.BaseDirectory = $baseDir" -ForegroundColor DarkCyan

# Resolve types from their respective assemblies to avoid "type not found" when
# the default resolver misses them.
$coreAsm = [System.Reflection.Assembly]::LoadFrom($assemblyCore)
$appAsm = [System.Reflection.Assembly]::LoadFrom($assemblyApp.FullName)

$tInvoker = $coreAsm.GetType('TidyWindow.Core.Automation.PowerShellInvoker', $true)
$tOptimizer = $coreAsm.GetType('TidyWindow.Core.Maintenance.RegistryOptimizerService', $true)
$tStateSvc = $coreAsm.GetType('TidyWindow.Core.Maintenance.RegistryStateService', $true)
$tPrefs = $coreAsm.GetType('TidyWindow.Core.Maintenance.RegistryPreferenceService', $true)
$tCard = $appAsm.GetType('TidyWindow.App.ViewModels.RegistryTweakCardViewModel', $false)
if (-not $tCard) {
    # Fallback: scan all loaded assemblies just in case the assembly identity/name differs
    $tCard = [AppDomain]::CurrentDomain.GetAssemblies() `
        | ForEach-Object { $_.GetType('TidyWindow.App.ViewModels.RegistryTweakCardViewModel', $false) } `
        | Where-Object { $_ -ne $null } `
        | Select-Object -First 1

    if (-not $tCard) {
        Write-Host "Unable to resolve type 'TidyWindow.App.ViewModels.RegistryTweakCardViewModel'. Dumping candidates:" -ForegroundColor Yellow
        Write-Host "Loaded assemblies:" -ForegroundColor DarkGray
        [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { Write-Host " * $($_.FullName)" -ForegroundColor DarkGray }
        [AppDomain]::CurrentDomain.GetAssemblies() |
            ForEach-Object {
                try {
                    $_.GetTypes() | Where-Object { $_.FullName -like '*RegistryTweak*' } | ForEach-Object { Write-Host " - $($_.FullName)" }
                } catch {
                    # ignore
                }
            }
    }
}
else {
    $constructors = $tCard.GetConstructors()
    if ($constructors.Length -gt 0) {
        Write-Host 'RegistryTweakCardViewModel constructors:' -ForegroundColor DarkGray
        foreach ($ctor in $constructors) {
            Write-Host " - $ctor" -ForegroundColor DarkGray
        }
    }
}

$invoker = [Activator]::CreateInstance($tInvoker)
$optimizer = [Activator]::CreateInstance($tOptimizer, $invoker)
$stateService = [Activator]::CreateInstance($tStateSvc, $invoker, $optimizer)
$preferences = [Activator]::CreateInstance($tPrefs)

$tweakDefinition = $optimizer.Tweaks | Where-Object { $_.Id -eq $TweakId }
if (-not $tweakDefinition) {
    Write-Host "Available tweak ids:" -ForegroundColor Yellow
    foreach ($item in $optimizer.Tweaks) {
        Write-Host " - $($item.Id)"
    }
    throw "Tweak '$TweakId' not found."
}

Write-Host "Definition type: $($tweakDefinition.GetType().AssemblyQualifiedName)" -ForegroundColor DarkGray
Write-Host "Preferences type: $($preferences.GetType().AssemblyQualifiedName)" -ForegroundColor DarkGray

$ctor = $tCard.GetConstructors() | Where-Object { $_.GetParameters().Length -eq 5 } | Select-Object -First 1
if (-not $ctor) {
    throw "Constructor with expected signature not found on $($tCard.FullName)."
}

$definitionArg = $tweakDefinition.PSObject.BaseObject
$preferencesArg = $preferences.PSObject.BaseObject

$card = $ctor.Invoke([object[]]@(
        $definitionArg,
        $tweakDefinition.Name,
        $tweakDefinition.Summary,
        $tweakDefinition.RiskLevel,
        $preferencesArg
    ))

if (-not $card) {
    throw "Failed to create RegistryTweakCardViewModel from assembly '$($assemblyApp.FullName)'."
}

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
