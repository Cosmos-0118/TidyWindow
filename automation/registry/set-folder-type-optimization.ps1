[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Folder discovery optimization'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'FolderType' -Value 'NotSpecified' -Type 'String'
        Register-RegistryChange -Change $change -Description 'Pinned shell folder type to NotSpecified.'
        Write-RegistryOutput 'Explorer will skip per-folder sniffing for faster browsing.'
    }
    else {
        $change = Remove-RegistryValue -Path $path -Name 'FolderType'
        Register-RegistryChange -Change $change -Description 'Removed FolderType override.'
        Write-RegistryOutput 'Explorer folder type detection restored to default.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
