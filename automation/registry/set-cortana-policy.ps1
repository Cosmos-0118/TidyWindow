[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Enable', 'Disable')]
    [string] $Mode,
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Cortana policy toggle'

try {
    Assert-TidyAdmin

    $resolvedMode = if (-not [string]::IsNullOrWhiteSpace($Mode)) { $Mode } elseif ($Enable.IsPresent -and -not $Disable.IsPresent) { 'Enable' } else { 'Disable' }

    $isEnabled = $resolvedMode -eq 'Enable'
    $value = if ($isEnabled) { 0 } else { 1 }
    $stateText = if ($isEnabled) { 'enabled' } else { 'disabled' }

    Write-RegistryOutput ("Cortana background components will be {0}." -f $stateText)

    $path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search'
    $change = Set-RegistryValue -Path $path -Name 'AllowCortana' -Value $value -Type 'DWord'
    Register-RegistryChange -Change $change -Description 'Updated AllowCortana policy.'

    Write-RegistryOutput 'Cortana policy updated.'
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
