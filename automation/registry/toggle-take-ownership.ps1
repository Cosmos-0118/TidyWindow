[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Take ownership context menu'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent

    $targets = @(
        'Registry::HKEY_CLASSES_ROOT\*\shell\TidyTakeOwnership',
        'Registry::HKEY_CLASSES_ROOT\Directory\shell\TidyTakeOwnership',
        'Registry::HKEY_CLASSES_ROOT\Drive\shell\TidyTakeOwnership'
    )

    if ($apply) {
        foreach ($target in $targets) {
            if (-not (Test-Path -LiteralPath $target)) {
                New-Item -Path $target -ItemType Key -Force | Out-Null
            }

            Set-ItemProperty -LiteralPath $target -Name '(default)' -Value 'Take ownership' -ErrorAction Stop
            Set-ItemProperty -LiteralPath $target -Name 'HasLUAShield' -Value '' -ErrorAction Stop
            Set-ItemProperty -LiteralPath $target -Name 'NoWorkingDirectory' -Value '' -ErrorAction Stop

            $commandPath = Join-Path -Path $target -ChildPath 'command'
            if (-not (Test-Path -LiteralPath $commandPath)) {
                New-Item -Path $commandPath -ItemType Key -Force | Out-Null
            }

            $command = 'cmd.exe /c takeown /f "%1" /r /d y && icacls "%1" /grant administrators:F /t'
            Set-ItemProperty -LiteralPath $commandPath -Name '(default)' -Value $command -ErrorAction Stop

            Write-RegistryOutput ("Context menu registered at {0}" -f $target)
        }
    }
    else {
        foreach ($target in $targets) {
            if (Test-Path -LiteralPath $target) {
                if ($PSCmdlet.ShouldProcess($target, 'Remove take ownership menu')) {
                    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
                    Write-RegistryOutput ("Removed context menu entry at {0}" -f $target)
                }
            }
        }
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
