param(
    [switch]$Enable,
    [switch]$Disable,
    [int]$MouseSpeed = 0,
    [int]$MouseThreshold1 = 0,
    [int]$MouseThreshold2 = 0
)

$path = "HKCU:\Control Panel\Mouse"

if ($Enable) {
    Set-ItemProperty -Path $path -Name "MouseSpeed" -Type String -Value "$MouseSpeed"
    Set-ItemProperty -Path $path -Name "MouseThreshold1" -Type String -Value "$MouseThreshold1"
    Set-ItemProperty -Path $path -Name "MouseThreshold2" -Type String -Value "$MouseThreshold2"
    return
}

if ($Disable) {
    Set-ItemProperty -Path $path -Name "MouseSpeed" -Type String -Value "1"
    Set-ItemProperty -Path $path -Name "MouseThreshold1" -Type String -Value "6"
    Set-ItemProperty -Path $path -Name "MouseThreshold2" -Type String -Value "10"
    return
}

throw "Specify -Enable or -Disable."