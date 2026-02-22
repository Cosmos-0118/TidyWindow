[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Disable web search results'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $explorerPath = 'HKCU:\Software\Policies\Microsoft\Windows\Explorer'
    $searchPath = 'HKCU:\Software\Policies\Microsoft\Windows\Windows Search'

    if ($apply) {
        $c1 = Set-RegistryValue -Path $explorerPath -Name 'DisableSearchBoxSuggestions' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $c1 -Description 'Disabled search box suggestions.'

        $c2 = Set-RegistryValue -Path $searchPath -Name 'EnableDynamicContentInWSB' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $c2 -Description 'Disabled dynamic web content in search.'

        Write-RegistryOutput 'Web search results disabled.'
    }
    else {
        $c1 = Remove-RegistryValue -Path $explorerPath -Name 'DisableSearchBoxSuggestions'
        Register-RegistryChange -Change $c1 -Description 'Removed search box suggestions override.'

        $c2 = Remove-RegistryValue -Path $searchPath -Name 'EnableDynamicContentInWSB'
        Register-RegistryChange -Change $c2 -Description 'Removed dynamic content override.'

        Write-RegistryOutput 'Web search results restored to defaults.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}