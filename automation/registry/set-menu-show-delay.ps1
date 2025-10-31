[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateRange(0, 2000)]
    [int] $DelayMilliseconds = 120,
    [switch] $RevertToDefault,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Menu show delay tuning'

try {
    Assert-TidyAdmin

    $targetValue = if ($RevertToDefault.IsPresent) { 400 } else { $DelayMilliseconds }
    Write-RegistryOutput ("Target delay: {0} ms" -f $targetValue)

    $change = Set-RegistryValue -Path 'HKCU:\Control Panel\Desktop' -Name 'MenuShowDelay' -Value ([string]$targetValue) -Type 'String'
    Register-RegistryChange -Change $change -Description 'Updated HKCU menu show delay.'

    Write-RegistryOutput 'Menu show delay applied successfully.'
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
