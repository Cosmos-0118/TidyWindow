param(
    [switch]$ShowExtensions,
    [switch]$ShowHidden,
    [switch]$ShowProtected,
    [switch]$RevertToWindowsDefaults
)

$path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"
New-Item -Path $path -Force | Out-Null

if ($RevertToWindowsDefaults) {
    Set-ItemProperty -Path $path -Name "HideFileExt" -Type DWord -Value 1
    Set-ItemProperty -Path $path -Name "Hidden" -Type DWord -Value 2
    Set-ItemProperty -Path $path -Name "ShowSuperHidden" -Type DWord -Value 0
    return
}

Set-ItemProperty -Path $path -Name "HideFileExt" -Type DWord -Value ([int](-not $ShowExtensions))
Set-ItemProperty -Path $path -Name "Hidden" -Type DWord -Value (if ($ShowHidden) { 1 } else { 2 })
Set-ItemProperty -Path $path -Name "ShowSuperHidden" -Type DWord -Value (if ($ShowProtected) { 1 } else { 0 })
