[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('SsdRecommended', 'Default', 'Disabled')]
    [string] $Mode = 'SsdRecommended',
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Prefetching policy'

try {
    Assert-TidyAdmin

    $prefetchValue = switch ($Mode) {
        'Default' { 3 }
        'Disabled' { 0 }
        default { 1 }
    }

    $superfetchValue = switch ($Mode) {
        'Default' { 3 }
        'Disabled' { 0 }
        default { 0 }
    }

    Write-RegistryOutput ("Applying prefetch mode: {0}" -f $Mode)

    $basePath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters'
    $change1 = Set-RegistryValue -Path $basePath -Name 'EnablePrefetcher' -Value $prefetchValue -Type 'DWord'
    Register-RegistryChange -Change $change1 -Description 'Updated EnablePrefetcher setting.'

    $change2 = Set-RegistryValue -Path $basePath -Name 'EnableSuperfetch' -Value $superfetchValue -Type 'DWord'
    Register-RegistryChange -Change $change2 -Description 'Updated EnableSuperfetch setting.'

    Write-RegistryOutput 'Prefetch configuration updated.'
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
