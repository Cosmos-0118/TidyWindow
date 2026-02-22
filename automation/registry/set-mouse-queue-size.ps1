[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int] $MouseDataQueueSize = 32,
    [switch] $Revert,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Mouse data queue size'

try {
    Assert-TidyAdmin

    $path = 'HKLM:\SYSTEM\CurrentControlSet\Services\mouclass\Parameters'

    if ($Revert.IsPresent) {
        $change = Remove-RegistryValue -Path $path -Name 'MouseDataQueueSize'
        Register-RegistryChange -Change $change -Description 'Removed MouseDataQueueSize override.'

        Write-RegistryOutput 'Mouse data queue size restored to default.'
    }
    else {
        if ($MouseDataQueueSize -lt 1) { throw 'MouseDataQueueSize must be positive.' }

        $change = Set-RegistryValue -Path $path -Name 'MouseDataQueueSize' -Value $MouseDataQueueSize -Type 'DWord'
        Register-RegistryChange -Change $change -Description "Set MouseDataQueueSize to $MouseDataQueueSize."

        Write-RegistryOutput "Mouse data queue size set to $MouseDataQueueSize (lower = less input latency)."
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
