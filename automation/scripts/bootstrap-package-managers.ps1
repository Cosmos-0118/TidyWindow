param(
    [switch] $IncludeScoop,
    [switch] $IncludeChocolatey
)

Import-Module "$PSScriptRoot/../modules/TidyWindow.Automation.psm1" -Force

Write-TidyLog -Level Information -Message 'Detecting package managers available on this machine.'

function Test-TidyCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommandName
    )

    $null -ne (Get-Command -Name $CommandName -ErrorAction SilentlyContinue)
}

$results = @()

$results += [pscustomobject]@{
    Name   = 'winget'
    Found  = Test-TidyCommand -CommandName 'winget'
    Notes  = 'Windows Package Manager client'
}

if ($IncludeChocolatey) {
    $results += [pscustomobject]@{
        Name   = 'choco'
        Found  = Test-TidyCommand -CommandName 'choco'
        Notes  = 'Chocolatey CLI'
    }
}

if ($IncludeScoop) {
    $results += [pscustomobject]@{
        Name   = 'scoop'
        Found  = Test-TidyCommand -CommandName 'scoop'
        Notes  = 'Scoop package manager'
    }
}

$results
