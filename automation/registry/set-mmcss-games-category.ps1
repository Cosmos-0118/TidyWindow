[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $SchedulingCategory = 'High',
    [string] $SfioPriority = 'High',
    [int] $Priority = 8,
    [int] $GpuPriority = 8,
    [switch] $Revert,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Games MMCSS scheduling'

try {
    Assert-TidyAdmin

    $path = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games'

    if ($Revert.IsPresent) {
        $change1 = Remove-RegistryValue -Path $path -Name 'Scheduling Category'
        Register-RegistryChange -Change $change1 -Description 'Removed Scheduling Category override.'

        $change2 = Remove-RegistryValue -Path $path -Name 'SFIO Priority'
        Register-RegistryChange -Change $change2 -Description 'Removed SFIO Priority override.'

        $change3 = Remove-RegistryValue -Path $path -Name 'Priority'
        Register-RegistryChange -Change $change3 -Description 'Removed Priority override.'

        $change4 = Remove-RegistryValue -Path $path -Name 'GPU Priority'
        Register-RegistryChange -Change $change4 -Description 'Removed GPU Priority override.'

        Write-RegistryOutput 'Games MMCSS scheduling reverted to defaults.'
    }
    else {
        $change1 = Set-RegistryValue -Path $path -Name 'Scheduling Category' -Value $SchedulingCategory -Type 'String'
        Register-RegistryChange -Change $change1 -Description "Set Scheduling Category to $SchedulingCategory."

        $change2 = Set-RegistryValue -Path $path -Name 'SFIO Priority' -Value $SfioPriority -Type 'String'
        Register-RegistryChange -Change $change2 -Description "Set SFIO Priority to $SfioPriority."

        $change3 = Set-RegistryValue -Path $path -Name 'Priority' -Value $Priority -Type 'DWord'
        Register-RegistryChange -Change $change3 -Description "Set Priority to $Priority."

        $change4 = Set-RegistryValue -Path $path -Name 'GPU Priority' -Value $GpuPriority -Type 'DWord'
        Register-RegistryChange -Change $change4 -Description "Set GPU Priority to $GpuPriority."

        Write-RegistryOutput 'Games MMCSS scheduling optimised for low latency.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
