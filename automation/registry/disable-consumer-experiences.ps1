param(
    [switch]$Enable,
    [switch]$Disable
)

$path = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent"
New-Item -Path $path -Force | Out-Null

if ($Enable) {
    Set-ItemProperty -Path $path -Name "DisableWindowsConsumerFeatures" -Type DWord -Value 1
    Set-ItemProperty -Path $path -Name "DisableWindowsSpotlightFeatures" -Type DWord -Value 1
    return
}

if ($Disable) {
    Remove-ItemProperty -Path $path -Name "DisableWindowsConsumerFeatures" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $path -Name "DisableWindowsSpotlightFeatures" -ErrorAction SilentlyContinue
    return
}

throw "Specify -Enable or -Disable."