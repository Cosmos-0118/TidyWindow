[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Workstation', 'Default')]
    [string] $Profile = 'Workstation',
    [Alias('RevertToWindowsDefault')]
    [switch] $Revert,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Network latency tuning'

try {
    Assert-TidyAdmin

    $basePath = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces'
    $interfaces = Get-ChildItem -LiteralPath $basePath -ErrorAction Stop

    foreach ($interface in $interfaces) {
        $interfaceName = $interface.PSChildName
        $interfacePath = Join-Path -Path $basePath -ChildPath $interfaceName
        if ($Revert.IsPresent) {
            $change = Remove-RegistryValue -Path $interfacePath -Name 'TcpAckFrequency'
            Register-RegistryChange -Change $change -Description "Reverted TcpAckFrequency for $interfaceName."

            $change2 = Remove-RegistryValue -Path $interfacePath -Name 'TCPNoDelay'
            Register-RegistryChange -Change $change2 -Description "Reverted TCPNoDelay for $interfaceName."
        }
        else {
            $ackValue = $Profile -eq 'Default' ? 0 : 1
            $nodelayValue = $Profile -eq 'Default' ? 0 : 1

            $change3 = Set-RegistryValue -Path $interfacePath -Name 'TcpAckFrequency' -Value $ackValue -Type 'DWord'
            Register-RegistryChange -Change $change3 -Description "Configured TcpAckFrequency for $interfaceName."

            $change4 = Set-RegistryValue -Path $interfacePath -Name 'TCPNoDelay' -Value $nodelayValue -Type 'DWord'
            Register-RegistryChange -Change $change4 -Description "Configured TCPNoDelay for $interfaceName."
        }
    }

    if (-not $interfaces -or $interfaces.Count -eq 0) {
        Write-RegistryOutput 'No TCP interfaces found; no registry changes applied.'
    }
    else {
        if ($Revert.IsPresent) {
            Write-RegistryOutput 'Network latency tweaks reverted for all interfaces.'
        }
        else {
            Write-RegistryOutput ("Network latency profile '{0}' applied to all interfaces." -f $Profile)
        }
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
