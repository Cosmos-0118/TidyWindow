[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Taskbar last active click'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'LastActiveClick' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Enabled LastActiveClick taskbar behavior.'
        Write-RegistryOutput 'Taskbar icons now focus the last active window.'
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'LastActiveClick' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled LastActiveClick override.'
        Write-RegistryOutput 'Restored default taskbar grouping behavior.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
