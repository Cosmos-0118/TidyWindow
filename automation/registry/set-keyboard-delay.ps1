[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int] $KeyboardDelay = 0,
    [switch] $Revert,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Keyboard repeat delay'

try {
    Assert-TidyAdmin

    $path = 'HKCU:\Control Panel\Keyboard'

    if ($Revert.IsPresent) {
        # Windows default is 1 (range 0-3)
        $change = Set-RegistryValue -Path $path -Name 'KeyboardDelay' -Value '1' -Type 'String'
        Register-RegistryChange -Change $change -Description 'Restored keyboard delay to Windows default (1).'

        Write-RegistryOutput 'Keyboard repeat delay restored to default.'
    }
    else {
        if ($KeyboardDelay -lt 0 -or $KeyboardDelay -gt 3) { throw 'KeyboardDelay must be between 0 and 3.' }

        $change = Set-RegistryValue -Path $path -Name 'KeyboardDelay' -Value "$KeyboardDelay" -Type 'String'
        Register-RegistryChange -Change $change -Description "Set keyboard delay to $KeyboardDelay."

        Write-RegistryOutput "Keyboard repeat delay set to $KeyboardDelay (0 = fastest)."
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
