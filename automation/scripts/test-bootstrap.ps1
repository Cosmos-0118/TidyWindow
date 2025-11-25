param(
    [string] $BootstrapScriptPath
)

$callerModulePath = $PSCmdlet.MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath)) {
    $callerModulePath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

if ([string]::IsNullOrWhiteSpace($BootstrapScriptPath)) {
    $BootstrapScriptPath = Join-Path -Path $scriptDirectory -ChildPath 'bootstrap-package-managers.ps1'
}

$BootstrapScriptPath = [System.IO.Path]::GetFullPath($BootstrapScriptPath)
if (-not (Test-Path -Path $BootstrapScriptPath)) {
    throw "Bootstrap script not found at path '$BootstrapScriptPath'."
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -Path $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

function Invoke-TidyBootstrapScenario {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [string] $ScriptPath,
        [bool] $IncludeChocolatey,
        [bool] $IncludeScoop
    )

    $arguments = @{}
    if ($IncludeChocolatey) {
        $arguments['IncludeChocolatey'] = $true
    }
    if ($IncludeScoop) {
        $arguments['IncludeScoop'] = $true
    }

    Write-TidyLog -Level Information -Message "Running scenario '$Name' (Chocolatey=$IncludeChocolatey, Scoop=$IncludeScoop)."

    $rawOutput = & $ScriptPath @arguments 2>&1

    $errors = $rawOutput | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] }
    if ($errors.Count -gt 0) {
        $messages = $errors | ForEach-Object { $_.ToString() }
        throw "Errors emitted by bootstrap script in scenario '$Name':`n$($messages -join [Environment]::NewLine)"
    }

    $textLines = $rawOutput | ForEach-Object { $_.ToString() }
    $jsonPayload = $textLines | Where-Object { $_ -match '^[\uFEFF\s]*[\[{]' } | Select-Object -First 1

    if (-not $jsonPayload) {
        throw "Scenario '$Name' did not return a JSON payload."
    }

    try {
        $parsed = $jsonPayload | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Scenario '$Name' returned invalid JSON: $($_.Exception.Message)"
    }

    if ($null -eq $parsed) {
        return @()
    }

    if ($parsed -is [System.Collections.IEnumerable]) {
        return , @($parsed)
    }

    return , $parsed
}

function Assert-TidyScenarioExpectations {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [array] $Results,
        [Parameter(Mandatory = $true)]
        [string[]] $ExpectedPresent,
        [string[]] $ExpectedAbsent = @()
    )

    foreach ($expected in $ExpectedPresent) {
        $match = $Results | Where-Object { $_.Name -eq $expected }
        if (-not $match) {
            throw "Scenario '$Name' did not include expected entry '$expected'."
        }
    }

    foreach ($missing in $ExpectedAbsent) {
        $match = $Results | Where-Object { $_.Name -eq $missing }
        if ($match) {
            throw "Scenario '$Name' unexpectedly included entry '$missing'."
        }
    }

    foreach ($item in $Results) {
        if (-not ($item.PSObject.Properties.Name -contains 'Name')) {
            throw "Scenario '$Name' returned an item missing the 'Name' property."
        }
        if (-not ($item.PSObject.Properties.Name -contains 'Found')) {
            throw "Scenario '$Name' returned an item missing the 'Found' property."
        }
        if (-not ($item.PSObject.Properties.Name -contains 'Notes')) {
            throw "Scenario '$Name' returned an item missing the 'Notes' property."
        }
    }
}

$scenarios = @(
    [pscustomobject]@{
        Name              = 'AllPackageManagers'
        IncludeChocolatey = $true
        IncludeScoop      = $true
        ExpectedPresent   = @('winget', 'choco', 'scoop')
        ExpectedAbsent    = @()
    },
    [pscustomobject]@{
        Name              = 'WingetOnly'
        IncludeChocolatey = $false
        IncludeScoop      = $false
        ExpectedPresent   = @('winget')
        ExpectedAbsent    = @('choco', 'scoop')
    },
    [pscustomobject]@{
        Name              = 'ChocolateyOnly'
        IncludeChocolatey = $true
        IncludeScoop      = $false
        ExpectedPresent   = @('winget', 'choco')
        ExpectedAbsent    = @('scoop')
    },
    [pscustomobject]@{
        Name              = 'ScoopOnly'
        IncludeChocolatey = $false
        IncludeScoop      = $true
        ExpectedPresent   = @('winget', 'scoop')
        ExpectedAbsent    = @('choco')
    }
)

$scenarioResults = @()
$allPassed = $true

foreach ($scenario in $scenarios) {
    try {
        $results = Invoke-TidyBootstrapScenario -Name $scenario.Name -ScriptPath $BootstrapScriptPath -IncludeChocolatey $scenario.IncludeChocolatey -IncludeScoop $scenario.IncludeScoop
        Assert-TidyScenarioExpectations -Name $scenario.Name -Results $results -ExpectedPresent $scenario.ExpectedPresent -ExpectedAbsent $scenario.ExpectedAbsent
        Write-TidyLog -Level Information -Message "Scenario '$($scenario.Name)' passed with $($results.Count) result(s)."
        $scenarioResults += [pscustomobject]@{
            Scenario    = $scenario.Name
            Status      = 'Passed'
            ResultCount = $results.Count
        }
    }
    catch {
        Write-TidyLog -Level Error -Message "Scenario '$($scenario.Name)' failed: $($_.Exception.Message)"
        $scenarioResults += [pscustomobject]@{
            Scenario    = $scenario.Name
            Status      = 'Failed'
            ResultCount = 0
            Error       = $_.Exception.Message
        }
        $allPassed = $false
    }
}

$scenarioResults | Format-Table -AutoSize | Out-String | Write-Host

if (-not $allPassed) {
    throw 'One or more bootstrap scenarios failed. See log for details.'
}

Write-TidyLog -Level Information -Message 'All bootstrap validation scenarios completed successfully.'

return $scenarioResults

