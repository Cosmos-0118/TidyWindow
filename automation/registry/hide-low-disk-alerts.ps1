[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Suppress,
    [switch] $DisableAlerts = $true,
    [Alias('RevertToWindowsDefault')]
    [switch] $EnableAlerts,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Low disk space alerts'

try {
    Assert-TidyAdmin

    $suppress = $null

    if ($PSBoundParameters.ContainsKey('Suppress')) {
        $suppress = [bool]$PSBoundParameters['Suppress']
    }
    elseif ($PSBoundParameters.ContainsKey('DisableAlerts')) {
        $suppress = [bool]$PSBoundParameters['DisableAlerts']
    }
    elseif ($PSBoundParameters.ContainsKey('EnableAlerts')) {
        $suppress = -not [bool]$PSBoundParameters['EnableAlerts']
    }
    else {
        $suppress = [bool]$DisableAlerts -and -not [bool]$EnableAlerts
    }
    $value = if ($suppress) { 1 } else { 0 }
    $state = if ($suppress) { 'suppressed' } else { 'enabled' }

    Write-RegistryOutput ("Low disk space alerts will be {0}." -f $state)

    $basePath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'
    $change = Set-RegistryValue -Path $basePath -Name 'NoLowDiskSpaceChecks' -Value $value -Type 'DWord'
    Register-RegistryChange -Change $change -Description 'Updated low disk space alert policy.'

    Write-RegistryOutput 'Low disk space alert policy saved.'
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
