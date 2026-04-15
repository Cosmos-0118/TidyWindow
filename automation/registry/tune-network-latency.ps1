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

    # Backup the entire interfaces key before modifying
    Backup-TidyRegistryKey -Path $basePath

    $interfaces = Get-ChildItem -LiteralPath $basePath -ErrorAction Stop

    # Filter to actual network interfaces (must have an IP address configured)
    $validInterfaces = @()
    foreach ($iface in $interfaces) {
        $ifacePath = Join-Path -Path $basePath -ChildPath $iface.PSChildName
        $hasIp = $false
        try {
            $addr = Get-ItemPropertyValue -LiteralPath $ifacePath -Name 'IPAddress' -ErrorAction SilentlyContinue
            $dhcp = Get-ItemPropertyValue -LiteralPath $ifacePath -Name 'EnableDHCP' -ErrorAction SilentlyContinue
            $hasIp = ($null -ne $addr -and $addr -ne '') -or ($dhcp -eq 1)
        } catch { }
        if ($hasIp) {
            $validInterfaces += $iface
        }
    }

    foreach ($interface in $validInterfaces) {
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

    if (-not $validInterfaces -or $validInterfaces.Count -eq 0) {
        Write-RegistryOutput 'No TCP interfaces with IP configuration found; no registry changes applied.'
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
