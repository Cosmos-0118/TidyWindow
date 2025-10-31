[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Security', 'Basic', 'Enhanced', 'Full')]
    [string] $Level = 'Security',
    [switch] $RevertToWindowsDefault,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Telemetry collection level'

try {
    Assert-TidyAdmin

    $path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection'

    if ($RevertToWindowsDefault.IsPresent) {
        Write-RegistryOutput 'Reverting telemetry policy to Windows defaults.'

        $change = Remove-RegistryValue -Path $path -Name 'AllowTelemetry'
        Register-RegistryChange -Change $change -Description 'Removed AllowTelemetry policy setting.'

        Write-RegistryOutput 'Telemetry policy reverted.'
    }
    else {
        $value = switch ($Level) {
            'Security' { 0 }
            'Basic' { 1 }
            'Enhanced' { 2 }
            'Full' { 3 }
        }

        Write-RegistryOutput ("Applying telemetry level: {0}" -f $Level)

        $change = Set-RegistryValue -Path $path -Name 'AllowTelemetry' -Value $value -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Updated AllowTelemetry policy setting.'

        Write-RegistryOutput 'Telemetry policy applied.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
