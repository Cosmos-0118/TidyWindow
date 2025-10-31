[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Automatic', 'Manual', 'Disabled')]
    [string] $Mode,
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'SysMain service policy'

try {
    Assert-TidyAdmin

    $resolvedMode = if (-not [string]::IsNullOrWhiteSpace($Mode)) { $Mode } elseif ($Enable.IsPresent -and -not $Disable.IsPresent) { 'Automatic' } else { 'Disabled' }

    $startType = switch ($resolvedMode) {
        'Automatic' { 2 }
        'Manual' { 3 }
        'Disabled' { 4 }
        default { throw "Unsupported SysMain mode '$resolvedMode'." }
    }

    $state = $resolvedMode

    Write-RegistryOutput ("Configuring SysMain start type: {0}" -f $state)

    $path = 'HKLM:\SYSTEM\CurrentControlSet\Services\SysMain'
    $change = Set-RegistryValue -Path $path -Name 'Start' -Value $startType -Type 'DWord'
    Register-RegistryChange -Change $change -Description 'Updated SysMain service start type.'

    Write-RegistryOutput 'SysMain policy updated.'
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
