param(
    [int]$MouseDataQueueSize = 32,
    [switch]$Revert
)

$path = "HKLM:\SYSTEM\CurrentControlSet\Services\mouclass\Parameters"

if ($Revert) {
    Remove-ItemProperty -Path $path -Name "MouseDataQueueSize" -ErrorAction SilentlyContinue
    return
}

if ($MouseDataQueueSize -lt 1) { throw "MouseDataQueueSize must be positive." }
New-Item -Path $path -Force | Out-Null
Set-ItemProperty -Path $path -Name "MouseDataQueueSize" -Type DWord -Value $MouseDataQueueSize
