[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Keyboard polling throughput'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Services\i8042prt\Parameters'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'PollStatusIterations' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Set PollStatusIterations to 1.'
        Write-RegistryOutput 'Keyboard polling iterations reduced for lower latency.'
    }
    else {
        $change = Remove-RegistryValue -Path $path -Name 'PollStatusIterations'
        Register-RegistryChange -Change $change -Description 'Removed PollStatusIterations override.'
        Write-RegistryOutput 'Keyboard polling iterations reverted to default.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
