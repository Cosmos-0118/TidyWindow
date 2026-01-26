param(
    [switch]$Enable,
    [switch]$Disable
)

$path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"
New-Item -Path $path -Force | Out-Null

if ($Enable) {
    Set-ItemProperty -Path $path -Name "SystemPaneSuggestionsEnabled" -Type DWord -Value 0
    Set-ItemProperty -Path $path -Name "SubscribedContent-338388Enabled" -Type DWord -Value 0
    return
}

if ($Disable) {
    Set-ItemProperty -Path $path -Name "SystemPaneSuggestionsEnabled" -Type DWord -Value 1
    Set-ItemProperty -Path $path -Name "SubscribedContent-338388Enabled" -Type DWord -Value 1
    return
}

throw "Specify -Enable or -Disable."