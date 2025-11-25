function Assert-TidyAdmin {
    if (-not ([bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) {
        throw 'Administrator privileges are required for this operation.'
    }
}

function Invoke-TidyRegistryScript {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $ScriptName,
        [hashtable] $Parameters
    )

    $registryRoot = Join-Path -Path $PSScriptRoot -ChildPath '..\registry'
    $scriptPath = Join-Path -Path $registryRoot -ChildPath $ScriptName
    $scriptPath = [System.IO.Path]::GetFullPath($scriptPath)

    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "Registry automation script not found at path '$scriptPath'."
    }

    if (-not $Parameters) {
        $Parameters = @{}
    }

    & $scriptPath @Parameters
}

function Add-TidyShouldProcessParameters {
    param(
        [hashtable] $Parameters,
        [System.Collections.IDictionary] $BoundParameters
    )

    if (-not $Parameters) {
        $Parameters = @{}
    }

    if ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'ContainsKey') -and $BoundParameters.ContainsKey('WhatIf')) {
        $Parameters['WhatIf'] = $BoundParameters['WhatIf']
    }
    elseif ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'Contains') -and $BoundParameters.Contains('WhatIf')) {
        $Parameters['WhatIf'] = $BoundParameters['WhatIf']
    }

    if ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'ContainsKey') -and $BoundParameters.ContainsKey('Confirm')) {
        $Parameters['Confirm'] = $BoundParameters['Confirm']
    }
    elseif ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'Contains') -and $BoundParameters.Contains('Confirm')) {
        $Parameters['Confirm'] = $BoundParameters['Confirm']
    }

    return $Parameters
}

function Set-TidyMenuShowDelay {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateRange(0, 2000)]
        [int] $DelayMilliseconds = 120,
        [switch] $RevertToDefault,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('DelayMilliseconds')) {
        $parameters['DelayMilliseconds'] = $DelayMilliseconds
    }
    if ($RevertToDefault.IsPresent) {
        $parameters['RevertToDefault'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-menu-show-delay.ps1' -Parameters $parameters
}

function Set-TidyWindowAnimation {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('Disable', 'Enable')]
        [string] $AnimationState = 'Disable',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('AnimationState')) {
        $parameters['AnimationState'] = $AnimationState
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-window-animation.ps1' -Parameters $parameters
}

function Set-TidyVisualEffectsProfile {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('Balanced', 'Performance', 'Appearance', 'Default')]
        [string] $Profile = 'Balanced',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('Profile')) {
        $parameters['Profile'] = $Profile
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-visual-effects.ps1' -Parameters $parameters
}

function Set-TidyPrefetchingMode {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('SsdRecommended', 'Default', 'Disabled')]
        [string] $Mode = 'SsdRecommended',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('Mode')) {
        $parameters['Mode'] = $Mode
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'manage-prefetching.ps1' -Parameters $parameters
}

function Set-TidyTelemetryLevel {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('Security', 'Basic', 'Enhanced', 'Full')]
        [string] $Level = 'Security',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('Level')) {
        $parameters['Level'] = $Level
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-telemetry-level.ps1' -Parameters $parameters
}

function Set-TidyCortanaPolicy {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-cortana-policy.ps1' -Parameters $parameters
}

function Set-TidyNetworkLatencyProfile {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Revert,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Revert.IsPresent) {
        $parameters['Revert'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'tune-network-latency.ps1' -Parameters $parameters
}

function Set-TidySysMainState {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'stop-sysmain.ps1' -Parameters $parameters
}

function Set-TidyLowDiskAlertPolicy {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $DisableAlerts,
        [switch] $EnableAlerts,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($DisableAlerts.IsPresent) {
        $parameters['DisableAlerts'] = $true
    }
    if ($EnableAlerts.IsPresent) {
        $parameters['EnableAlerts'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'hide-low-disk-alerts.ps1' -Parameters $parameters
}

function Set-TidyAutoRestartSignOn {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'configure-auto-restart-sign-on.ps1' -Parameters $parameters
}

function Set-TidyAutoEndTasks {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'auto-end-tasks.ps1' -Parameters $parameters
}

function Set-TidyHungAppTimeouts {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateRange(1000, 20000)]
        [int] $HungAppTimeout = 5000,
        [ValidateRange(1000, 20000)]
        [int] $WaitToKillAppTimeout = 5000,
        [switch] $Revert,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('HungAppTimeout')) {
        $parameters['HungAppTimeout'] = $HungAppTimeout
    }
    if ($PSBoundParameters.ContainsKey('WaitToKillAppTimeout')) {
        $parameters['WaitToKillAppTimeout'] = $WaitToKillAppTimeout
    }
    if ($Revert.IsPresent) {
        $parameters['Revert'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'adjust-hung-app-timeouts.ps1' -Parameters $parameters
}

function Set-TidyLockWorkstationPolicy {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'toggle-lock-workstation.ps1' -Parameters $parameters
}

