[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Block driver updates via Windows Update'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'

    # Check if managed by Group Policy before modifying
    if (Test-TidyGroupPolicyManaged -RegistryPath $path) {
        Write-RegistryOutput 'WARNING: WindowsUpdate policy is managed by Group Policy. Changes may be overwritten on next policy refresh.'
    }

    # Backup before modifying
    Backup-TidyRegistryKey -Path $path

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'ExcludeWUDriversInQualityUpdate' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Excluded driver updates from quality updates.'

        Write-RegistryOutput 'Driver updates via Windows Update blocked.'
    }
    else {
        $change = Remove-RegistryValue -Path $path -Name 'ExcludeWUDriversInQualityUpdate'
        Register-RegistryChange -Change $change -Description 'Removed driver update exclusion policy.'

        Write-RegistryOutput 'Driver updates via Windows Update restored to defaults.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}