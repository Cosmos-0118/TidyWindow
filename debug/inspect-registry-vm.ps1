param(
    [string]$TweakId = $null,
    [int]$DelaySeconds = 3
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
[AppDomain]::CurrentDomain.SetData('APP_CONTEXT_BASE_DIRECTORY', $root)

$coreAssemblyPath = Join-Path $root 'src/TidyWindow.Core/bin/Debug/net8.0/TidyWindow.Core.dll'
$appAssemblyRoot = Join-Path $root 'src/TidyWindow.App/bin/Debug/net8.0-windows'

$assemblyCandidates = Get-ChildItem -Path $appAssemblyRoot -Filter 'TidyWindow.dll' -File -Recurse -ErrorAction SilentlyContinue |
    Sort-Object -Property LastWriteTimeUtc -Descending

if (-not $assemblyCandidates) {
    throw "Unable to locate TidyWindow.dll beneath $appAssemblyRoot. Build the app project first."
}

$appAssemblyPath = $assemblyCandidates[0].FullName
if (-not (Test-Path $coreAssemblyPath)) {
    throw "Core assembly not found at $coreAssemblyPath"
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase | Out-Null
$appAssembly = [System.Reflection.Assembly]::LoadFrom($appAssemblyPath)
$coreAssembly = [System.Reflection.Assembly]::LoadFrom($coreAssemblyPath)

$appAssemblyDirectory = Split-Path -Parent $appAssemblyPath
$dependencyFiles = @(
    'Microsoft.Extensions.DependencyInjection.dll',
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll'
)

foreach ($file in $dependencyFiles) {
    $dependencyPath = Join-Path $appAssemblyDirectory $file
    if (Test-Path $dependencyPath) {
        try {
            [System.Reflection.Assembly]::LoadFrom($dependencyPath) | Out-Null
        }
        catch {
            # Already loaded
        }
    }
}

function Resolve-Type {
    param(
        [Parameter(Mandatory = $true)][string]$Name
    )

    $type = $appAssembly.GetType($Name, $false)
    if (-not $type) {
        $type = $coreAssembly.GetType($Name, $false)
    }

    if (-not $type) {
        throw "Type '$Name' not found."
    }

    return $type
}

$activityType = Resolve-Type 'TidyWindow.App.Services.ActivityLogService'
$activityLog = [Activator]::CreateInstance($activityType, 500)

$serviceCollectionType = [Type]::GetType('Microsoft.Extensions.DependencyInjection.ServiceCollection, Microsoft.Extensions.DependencyInjection', $true)
$services = [Activator]::CreateInstance($serviceCollectionType)
$buildProviderMethod = [Type]::GetType('Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions, Microsoft.Extensions.DependencyInjection', $true).GetMethod('BuildServiceProvider', [Type[]]@($serviceCollectionType, [Type]::GetType('System.Boolean')))
$serviceProvider = $buildProviderMethod.Invoke($null, @($services, $false))

$navigationServiceType = Resolve-Type 'TidyWindow.App.Services.NavigationService'
$navigationService = [Activator]::CreateInstance($navigationServiceType, @($serviceProvider))

$mainViewModelType = Resolve-Type 'TidyWindow.App.ViewModels.MainViewModel'
$mainViewModel = [Activator]::CreateInstance($mainViewModelType, @($navigationService, $activityLog))

$invokerType = Resolve-Type 'TidyWindow.Core.Automation.PowerShellInvoker'
$invoker = [Activator]::CreateInstance($invokerType)

$optimizerServiceType = Resolve-Type 'TidyWindow.Core.Maintenance.RegistryOptimizerService'
$optimizerService = [Activator]::CreateInstance($optimizerServiceType, @($invoker))

$preferenceServiceType = Resolve-Type 'TidyWindow.Core.Maintenance.RegistryPreferenceService'
$preferenceService = [Activator]::CreateInstance($preferenceServiceType)

$stateServiceType = Resolve-Type 'TidyWindow.Core.Maintenance.RegistryStateService'
$stateService = [Activator]::CreateInstance($stateServiceType, @($invoker, $optimizerService))

$viewModelType = Resolve-Type 'TidyWindow.App.ViewModels.RegistryOptimizerViewModel'
$viewModel = [Activator]::CreateInstance($viewModelType, @($activityLog, $mainViewModel, $optimizerService, $stateService, $preferenceService))

if ($DelaySeconds -gt 0) {
    Start-Sleep -Seconds $DelaySeconds
}

$tweaksProperty = $viewModelType.GetProperty('Tweaks')
$tweaks = $tweaksProperty.GetValue($viewModel)

if (-not $tweaks) {
    Write-Host 'No tweaks found.'
    return
}

foreach ($tweak in $tweaks) {
    $idProperty = $tweak.GetType().GetProperty('Id')
    $id = $idProperty.GetValue($tweak)

    if ($TweakId -and $id -ne $TweakId) {
        continue
    }

    $current = $tweak.GetType().GetProperty('CurrentValue').GetValue($tweak)
    $recommended = $tweak.GetType().GetProperty('RecommendedValue').GetValue($tweak)
    $custom = $tweak.GetType().GetProperty('CustomValue').GetValue($tweak)
    $supportsCustom = $tweak.GetType().GetProperty('SupportsCustomValue').GetValue($tweak)

    [pscustomobject]@{
        Id = $id
        CurrentValue = $current
        RecommendedValue = $recommended
        CustomValue = $custom
        SupportsCustomValue = $supportsCustom
    }
}
