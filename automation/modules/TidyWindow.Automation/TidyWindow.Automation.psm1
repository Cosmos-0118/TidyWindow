$script:ModuleRoot = Split-Path -Parent $PSCommandPath
$moduleParts = @(
    'logging.ps1',
    'packages.ps1',
    'registry.ps1',
    'apps.ps1',
    'core.ps1'
)

foreach ($part in $moduleParts) {
    $partPath = Join-Path -Path $script:ModuleRoot -ChildPath $part
    if (-not (Test-Path -LiteralPath $partPath)) {
        throw "Failed to load module part '$partPath'."
    }

    . $partPath
}

$exportedFunctions = @(
    'Convert-TidyLogMessage',
    'Write-TidyLog',
    'Write-TidyInfo',
    'Write-TidyWarning',
    'Write-TidyError',
    'Get-TidyCommandPath',
    'Get-TidyInstalledAppInventory',
    'Get-TidyWingetMsixCandidates',
    'Get-TidyWingetInstalledVersion',
    'Get-TidyChocoInstalledVersion',
    'Get-TidyScoopInstalledVersion',
    'Get-TidyInstalledPackageVersion',
    'Assert-TidyAdmin',
    'Set-TidyMenuShowDelay',
    'Set-TidyWindowAnimation',
    'Set-TidyVisualEffectsProfile',
    'Set-TidyPrefetchingMode',
    'Set-TidyTelemetryLevel',
    'Set-TidyCortanaPolicy',
    'Set-TidyNetworkLatencyProfile',
    'Set-TidySysMainState',
    'Set-TidyLowDiskAlertPolicy',
    'Set-TidyAutoRestartSignOn',
    'Set-TidyAutoEndTasks',
    'Set-TidyHungAppTimeouts',
    'Set-TidyLockWorkstationPolicy',
    'Resolve-TidyPath',
    'ConvertTo-TidyNameKey',
    'Get-TidyProgramDataDirectory',
    'New-TidyFeatureRunDirectory',
    'Write-TidyStructuredEvent',
    'Write-TidyRunLog',
    'Invoke-TidyCommandLine',
    'Get-TidyProcessSnapshot',
    'Get-TidyServiceSnapshot',
    'Find-TidyRelatedProcesses',
    'Stop-TidyProcesses',
    'ConvertTo-TidyRegistryPath',
    'Measure-TidyDirectoryBytes',
    'New-TidyArtifactId'
)

Export-ModuleMember -Function $exportedFunctions
