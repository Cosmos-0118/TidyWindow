[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Taskbar clock seconds'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'ShowSecondsInSystemClock' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Enabled seconds on system clock.'
        Write-RegistryOutput 'Explorer clock will show seconds.'

        # Double-check the value in case the shell blocks the first attempt.
        $observed = Get-ItemProperty -LiteralPath (Resolve-RegistryUserPath -Path $path) -Name 'ShowSecondsInSystemClock' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ShowSecondsInSystemClock -ErrorAction SilentlyContinue
        if ($observed -ne 1) {
            Write-RegistryOutput 'Retrying clock-seconds flag via reg.exe fallback.'
            & reg.exe add 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced' /v ShowSecondsInSystemClock /t REG_DWORD /d 1 /f | Out-Null
            $observed = Get-ItemProperty -LiteralPath (Resolve-RegistryUserPath -Path $path) -Name 'ShowSecondsInSystemClock' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ShowSecondsInSystemClock -ErrorAction SilentlyContinue
            if ($observed -ne 1) {
                throw "Unable to persist ShowSecondsInSystemClock (observed '$observed')."
            }
        }

        # Restart Explorer to apply the UI change
        Write-RegistryOutput 'Restarting explorer to apply clock seconds display.'
        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'ShowSecondsInSystemClock' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled seconds on system clock.'
        Write-RegistryOutput 'Explorer clock seconds hidden.'

        # Restart Explorer to apply the UI change
        Write-RegistryOutput 'Restarting explorer to apply clock seconds display.'
        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
