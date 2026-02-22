[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $ShowExtensions,
    [switch] $ShowHidden,
    [switch] $ShowProtected,
    [switch] $RevertToWindowsDefaults,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'File visibility settings'

try {
    Assert-TidyAdmin

    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'

    if ($RevertToWindowsDefaults.IsPresent) {
        $c1 = Set-RegistryValue -Path $path -Name 'HideFileExt' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $c1 -Description 'Hide known file extensions (default).'

        $c2 = Set-RegistryValue -Path $path -Name 'Hidden' -Value 2 -Type 'DWord'
        Register-RegistryChange -Change $c2 -Description 'Do not show hidden files (default).'

        $c3 = Set-RegistryValue -Path $path -Name 'ShowSuperHidden' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $c3 -Description 'Hide protected OS files (default).'

        Write-RegistryOutput 'File visibility reverted to Windows defaults.'
    }
    else {
        $hideExt = if ($ShowExtensions.IsPresent) { 0 } else { 1 }
        $c1 = Set-RegistryValue -Path $path -Name 'HideFileExt' -Value $hideExt -Type 'DWord'
        Register-RegistryChange -Change $c1 -Description "HideFileExt set to $hideExt."

        $hidden = if ($ShowHidden.IsPresent) { 1 } else { 2 }
        $c2 = Set-RegistryValue -Path $path -Name 'Hidden' -Value $hidden -Type 'DWord'
        Register-RegistryChange -Change $c2 -Description "Hidden set to $hidden."

        $superHidden = if ($ShowProtected.IsPresent) { 1 } else { 0 }
        $c3 = Set-RegistryValue -Path $path -Name 'ShowSuperHidden' -Value $superHidden -Type 'DWord'
        Register-RegistryChange -Change $c3 -Description "ShowSuperHidden set to $superHidden."

        Write-RegistryOutput 'File visibility settings applied.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
