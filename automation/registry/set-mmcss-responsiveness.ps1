[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [ValidateRange(0,100)]
    [int] $ResponsivenessPercent = 10,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'MMCSS system responsiveness'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'

    if ($apply) {
        $value = [Math]::Max(0, [Math]::Min(100, $ResponsivenessPercent))
        $change = Set-RegistryValue -Path $path -Name 'SystemResponsiveness' -Value $value -Type 'DWord'
        Register-RegistryChange -Change $change -Description ("Set SystemResponsiveness to {0}." -f $value)
        Write-RegistryOutput ("System responsiveness reserved CPU lowered to {0}%" -f $value)
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'SystemResponsiveness' -Value 20 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Restored SystemResponsiveness to default (20).'
        Write-RegistryOutput 'System responsiveness reservation restored to 20%.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
