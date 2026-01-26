param(
    [switch]$Enable,
    [switch]$Disable
)

$path = "HKLM:\SYSTEM\CurrentControlSet\Control\Remote Assistance"
New-Item -Path $path -Force | Out-Null

if ($Enable) {
    Set-ItemProperty -Path $path -Name "fAllowToGetHelp" -Type DWord -Value 0
    return
}

if ($Disable) {
    Set-ItemProperty -Path $path -Name "fAllowToGetHelp" -Type DWord -Value 1
    return
}

throw "Specify -Enable or -Disable."