[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Classic context menu'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $clsidPath = 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}'
    $inprocPath = "$clsidPath\InprocServer32"

    $effectiveClsid = Resolve-RegistryUserPath -Path $clsidPath
    $effectiveInproc = Resolve-RegistryUserPath -Path $inprocPath

    if ($apply) {
        if ($PSCmdlet.ShouldProcess($effectiveInproc, 'Create InprocServer32 key with empty default value')) {
            New-Item -Path $effectiveInproc -Force | Out-Null
            Set-ItemProperty -LiteralPath $effectiveInproc -Name '(default)' -Value '' -ErrorAction Stop
        }

        Write-RegistryOutput "Classic context menu enabled at $effectiveClsid."
        Write-RegistryOutput 'Explorer restart required for changes to take effect.'
    }
    else {
        if (Test-Path -LiteralPath $effectiveClsid) {
            if ($PSCmdlet.ShouldProcess($effectiveClsid, 'Remove CLSID key')) {
                Remove-Item -LiteralPath $effectiveClsid -Recurse -Force -ErrorAction Stop
            }
        }

        Write-RegistryOutput 'Classic context menu disabled (Windows 11 default restored).'
        Write-RegistryOutput 'Explorer restart required for changes to take effect.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}