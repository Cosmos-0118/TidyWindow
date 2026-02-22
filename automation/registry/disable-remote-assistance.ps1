[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Disable Remote Assistance'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\Remote Assistance'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'fAllowToGetHelp' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled Remote Assistance.'

        Write-RegistryOutput 'Remote Assistance disabled.'
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'fAllowToGetHelp' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Re-enabled Remote Assistance.'

        Write-RegistryOutput 'Remote Assistance restored to default (enabled).'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}