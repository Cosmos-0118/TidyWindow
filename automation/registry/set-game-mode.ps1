param(
    [switch]$Enable,
    [switch]$Disable
)

$path = "HKLM:\SOFTWARE\Microsoft\GameBar"
New-Item -Path $path -Force | Out-Null

if ($Enable) {
    Set-ItemProperty -Path $path -Name "AllowAutoGameMode" -Type DWord -Value 1
    return
}

if ($Disable) {
    Set-ItemProperty -Path $path -Name "AllowAutoGameMode" -Type DWord -Value 0
    return
}

throw "Specify -Enable or -Disable."