param(
    [switch]$Enable,
    [switch]$Disable
)

$path = "HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers"
New-Item -Path $path -Force | Out-Null

if ($Enable) {
    Set-ItemProperty -Path $path -Name "HwSchMode" -Type DWord -Value 2
    return
}

if ($Disable) {
    Set-ItemProperty -Path $path -Name "HwSchMode" -Type DWord -Value 1
    return
}

throw "Specify -Enable or -Disable."