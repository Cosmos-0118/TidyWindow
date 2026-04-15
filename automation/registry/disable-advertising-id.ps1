[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Disable advertising ID'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $userPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo'
    $policyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo'

    # Check if managed by Group Policy before modifying
    if (Test-TidyGroupPolicyManaged -RegistryPath $policyPath) {
        Write-RegistryOutput 'WARNING: AdvertisingInfo policy is managed by Group Policy. Changes may be overwritten on next policy refresh.'
    }

    # Backup before modifying
    Backup-TidyRegistryKey -Path $policyPath

    if ($apply) {
        $c1 = Set-RegistryValue -Path $userPath -Name 'Enabled' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $c1 -Description 'Disabled advertising ID for the current user.'

        $c2 = Set-RegistryValue -Path $policyPath -Name 'DisabledByGroupPolicy' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $c2 -Description 'Disabled advertising ID via group policy.'

        Write-RegistryOutput 'Advertising ID disabled.'
    }
    else {
        $c1 = Set-RegistryValue -Path $userPath -Name 'Enabled' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $c1 -Description 'Re-enabled advertising ID for the current user.'

        $c2 = Remove-RegistryValue -Path $policyPath -Name 'DisabledByGroupPolicy'
        Register-RegistryChange -Change $c2 -Description 'Removed advertising ID group policy override.'

        Write-RegistryOutput 'Advertising ID restored to defaults.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}