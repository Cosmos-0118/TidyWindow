param(
    [switch]$Enable,
    [switch]$Disable
)

$path = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"
New-Item -Path $path -Force | Out-Null

if ($Enable) {
    Set-ItemProperty -Path $path -Name "ExcludeWUDriversInQualityUpdate" -Type DWord -Value 1
    return
}

if ($Disable) {
    Remove-ItemProperty -Path $path -Name "ExcludeWUDriversInQualityUpdate" -ErrorAction SilentlyContinue
    return
}

throw "Specify -Enable or -Disable."