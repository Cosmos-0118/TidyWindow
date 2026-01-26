param(
    [switch]$Enable,
    [switch]$Disable
)

$path = "HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters"
New-Item -Path $path -Force | Out-Null

if ($Enable) {
    Set-ItemProperty -Path $path -Name "SMB1" -Type DWord -Value 0
    return
}

if ($Disable) {
    Remove-ItemProperty -Path $path -Name "SMB1" -ErrorAction SilentlyContinue
    return
}

throw "Specify -Enable or -Disable."