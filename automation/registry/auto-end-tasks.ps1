[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Auto end tasks'

try {
    Assert-TidyAdmin

    $setValue = $Enable.IsPresent -and -not $Disable.IsPresent
    $value = if ($setValue) { 1 } else { 0 }
    $state = if ($setValue) { 'enabled' } else { 'disabled' }

    Write-RegistryOutput ("AutoEndTasks will be {0}." -f $state)

    $path = 'HKCU:\Control Panel\Desktop'
    $change = Set-RegistryValue -Path $path -Name 'AutoEndTasks' -Value ([string]$value) -Type 'String'
    Register-RegistryChange -Change $change -Description 'Updated AutoEndTasks setting.'

    Write-RegistryOutput 'Auto end tasks policy saved.'
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
