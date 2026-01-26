param(
    [switch]$Enable,
    [switch]$Disable
)

$userPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo"
$policyPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo"

if ($Enable) {
    New-Item -Path $userPath -Force | Out-Null
    New-Item -Path $policyPath -Force | Out-Null
    Set-ItemProperty -Path $userPath -Name "Enabled" -Type DWord -Value 0
    Set-ItemProperty -Path $policyPath -Name "DisabledByGroupPolicy" -Type DWord -Value 1
    return
}

if ($Disable) {
    Set-ItemProperty -Path $userPath -Name "Enabled" -Type DWord -Value 1
    Remove-ItemProperty -Path $policyPath -Name "DisabledByGroupPolicy" -ErrorAction SilentlyContinue
    return
}

throw "Specify -Enable or -Disable."