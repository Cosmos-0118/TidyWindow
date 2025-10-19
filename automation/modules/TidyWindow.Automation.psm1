function Write-TidyLog {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Information', 'Warning', 'Error')]
        [string] $Level,
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    $timestamp = (Get-Date).ToString('u')
    Write-Host "[$timestamp][$Level] $Message"
}

function Assert-TidyAdmin {
    if (-not ([bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) {
        throw 'Administrator privileges are required for this operation.'
    }
}

Export-ModuleMember -Function Write-TidyLog, Assert-TidyAdmin
