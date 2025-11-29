[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Verbose startup/shutdown messages'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'VerboseStatus' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Enabled verbose status messages.'
        Write-RegistryOutput 'Verbose boot and shutdown messages enabled.'
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'VerboseStatus' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled verbose status messages.'
        Write-RegistryOutput 'Verbose boot messaging disabled.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
