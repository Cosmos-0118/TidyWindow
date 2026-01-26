param(
    [switch]$Enable,
    [switch]$Disable
)

$machinePath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System"
$userPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\CDP"

if ($Enable) {
    New-Item -Path $machinePath -Force | Out-Null
    New-Item -Path $userPath -Force | Out-Null
    Set-ItemProperty -Path $machinePath -Name "EnableCdp" -Type DWord -Value 0
    Set-ItemProperty -Path $userPath -Name "CdpSessionUserAuthzPolicy" -Type DWord -Value 0
    return
}

if ($Disable) {
    Remove-ItemProperty -Path $machinePath -Name "EnableCdp" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $userPath -Name "CdpSessionUserAuthzPolicy" -ErrorAction SilentlyContinue
    return
}

throw "Specify -Enable or -Disable."