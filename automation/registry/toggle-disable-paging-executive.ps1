param(
    [switch]$Enable,
    [switch]$Disable
)

$path = "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
New-Item -Path $path -Force | Out-Null

if ($Enable) {
    Set-ItemProperty -Path $path -Name "DisablePagingExecutive" -Type DWord -Value 1
    return
}

if ($Disable) {
    Remove-ItemProperty -Path $path -Name "DisablePagingExecutive" -ErrorAction SilentlyContinue
    return
}

throw "Specify -Enable or -Disable."