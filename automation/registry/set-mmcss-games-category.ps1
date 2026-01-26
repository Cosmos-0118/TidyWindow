param(
    [string]$SchedulingCategory = "High",
    [string]$SfioPriority = "High",
    [int]$Priority = 6,
    [int]$GpuPriority = 8,
    [switch]$Revert
)

$path = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games"
New-Item -Path $path -Force | Out-Null

if ($Revert) {
    Remove-ItemProperty -Path $path -Name "Scheduling Category" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $path -Name "SFIO Priority" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $path -Name "Priority" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $path -Name "GPU Priority" -ErrorAction SilentlyContinue
    return
}

Set-ItemProperty -Path $path -Name "Scheduling Category" -Type String -Value $SchedulingCategory
Set-ItemProperty -Path $path -Name "SFIO Priority" -Type String -Value $SfioPriority
Set-ItemProperty -Path $path -Name "Priority" -Type DWord -Value $Priority
Set-ItemProperty -Path $path -Name "GPU Priority" -Type DWord -Value $GpuPriority
