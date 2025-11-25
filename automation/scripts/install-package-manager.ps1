param(
    [Parameter(Mandatory = $true)]
    [string] $Manager,
    [switch] $Elevated,
    [string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath) -and (Get-Variable -Name PSCommandPath -Scope Script -ErrorAction SilentlyContinue)) {
    $callerModulePath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -Path $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$script:TidyOutputLines = [System.Collections.Generic.List[string]]::new()
$script:TidyErrorLines = [System.Collections.Generic.List[string]]::new()
$script:OperationSucceeded = $true
$script:UsingResultFile = -not [string]::IsNullOrWhiteSpace($ResultPath)

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

function Write-TidyOutput {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    [void]$script:TidyOutputLines.Add($text)
    Write-Output $text
}

function Write-TidyError {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    [void]$script:TidyErrorLines.Add($text)
    Write-Error -Message $text
}

function Save-TidyResult {
    if (-not $script:UsingResultFile) {
        return
    }

    $payload = [pscustomobject]@{
        Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output  = $script:TidyOutputLines
        Errors  = $script:TidyErrorLines
    }

    $json = $payload | ConvertTo-Json -Depth 5
    Set-Content -Path $ResultPath -Value $json -Encoding UTF8
}

function Invoke-TidyCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock] $Command,
        [string] $Description = 'Running command.',
        [object[]] $Arguments = @(),
        [switch] $RequireSuccess
    )

    Write-TidyLog -Level Information -Message $Description

    $output = & $Command @Arguments 2>&1
    $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }

    foreach ($entry in @($output)) {
        if ($null -eq $entry) {
            continue
        }

        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            Write-TidyError -Message $entry
        }
        else {
            Write-TidyOutput -Message $entry
        }
    }

    if ($RequireSuccess -and $exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }

    return $exitCode
}

function Get-TidyResultProperty {
    param(
        [Parameter(Mandatory = $true)]
        $Result,
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($null -eq $Result) {
        return $null
    }

    if ($Result -is [System.Collections.IDictionary]) {
        if ($Result.Contains($Name)) {
            return $Result[$Name]
        }

        if ($Result.ContainsKey($Name)) {
            return $Result[$Name]
        }

        return $null
    }

    try {
        $value = $Result | Select-Object -ExpandProperty $Name -ErrorAction Stop
        return $value
    }
    catch
    {
        return $null
    }
}

function Test-TidyCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommandName
    )

    $null -ne (Get-Command -Name $CommandName -ErrorAction SilentlyContinue)
}

function Ensure-TidyExecutionPolicy {
    param(
        [string] $DesiredPolicy = 'RemoteSigned'
    )

    Write-TidyLog -Level Information -Message ("Ensuring execution policy {0} for current user." -f $DesiredPolicy)

    $policySatisfied = $false

    try {
        $currentPolicy = Get-ExecutionPolicy -Scope CurrentUser -ErrorAction Stop
        if (-not [string]::IsNullOrWhiteSpace($currentPolicy)) {
            if ([string]::Equals($currentPolicy, $DesiredPolicy, [System.StringComparison]::OrdinalIgnoreCase)) {
                $policySatisfied = $true
                Write-TidyOutput -Message ("Execution policy already set to {0} for current user." -f $currentPolicy)
            }
            elseif ($currentPolicy -in @('Bypass', 'Unrestricted')) {
                $policySatisfied = $true
                Write-TidyOutput -Message ("Execution policy {0} for current user already permits bootstrap scripts." -f $currentPolicy)
            }
        }
    }
    catch {
        Write-TidyLog -Level Warning -Message ("Unable to read current user execution policy. {0}" -f $_.Exception.Message)
    }

    if (-not $policySatisfied) {
        try {
            Set-ExecutionPolicy -ExecutionPolicy $DesiredPolicy -Scope CurrentUser -Force -ErrorAction Stop
            $policySatisfied = $true
            Write-TidyOutput -Message ("Execution policy set to {0} for current user." -f $DesiredPolicy)
        }
        catch {
            Write-TidyLog -Level Warning -Message ("Failed to set current user execution policy. {0}" -f $_.Exception.Message)
        }
    }

    if ($policySatisfied) {
        return
    }

    Write-TidyLog -Level Warning -Message 'Unable to persist execution policy change. Falling back to a process-scoped policy for this run.'

    try {
        Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force -ErrorAction Stop
        Write-TidyOutput -Message 'Process execution policy set to Bypass for bootstrap tasks. You may need to configure your execution policy manually for future sessions.'
    }
    catch {
        $message = "Failed to configure execution policy for bootstrap operations. $($_.Exception.Message)"
        throw $message
    }
}

function Invoke-ScoopBootstrap {
    if (Test-TidyCommand -CommandName 'scoop') {
        Invoke-TidyCommand -Command { scoop update } -Description 'Updating Scoop installation.' -RequireSuccess
        return 'Scoop update completed.'
    }

    Write-TidyLog -Level Information -Message 'Installing Scoop for the current user.'
    Ensure-TidyExecutionPolicy -DesiredPolicy 'RemoteSigned'

    $installScript = Invoke-RestMethod -Uri 'https://get.scoop.sh' -UseBasicParsing
    if ([string]::IsNullOrWhiteSpace($installScript)) {
        throw 'Failed to download Scoop bootstrap script.'
    }

    Write-TidyOutput -Message 'Downloaded Scoop bootstrap script.'
    if (Test-TidyAdmin) {
        $tempPath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('tidywindow-scoop-install-' + ([System.Guid]::NewGuid().ToString('N')) + '.ps1')
        try {
            Set-Content -Path $tempPath -Value $installScript -Encoding UTF8
            Invoke-TidyCommand -Command { param($path) & $path -RunAsAdmin } -Arguments @($tempPath) -Description 'Running Scoop bootstrap script with -RunAsAdmin.' -RequireSuccess
        }
        finally {
            Remove-Item -Path $tempPath -ErrorAction SilentlyContinue
        }
    }
    else {
        Invoke-TidyCommand -Command { param($content) Invoke-Expression $content } -Arguments @($installScript) -Description 'Running Scoop bootstrap script.' -RequireSuccess
    }
    return 'Scoop installation completed.'
}

function Test-TidyAdmin {
    return [bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-TidyPowerShellExecutable {
    if ($PSVersionTable.PSEdition -eq 'Core') {
        $pwsh = Get-Command -Name 'pwsh' -ErrorAction SilentlyContinue
        if ($pwsh) {
            return $pwsh.Source
        }
    }

    $legacy = Get-Command -Name 'powershell.exe' -ErrorAction SilentlyContinue
    if ($legacy) {
        return $legacy.Source
    }

    throw 'Unable to locate a PowerShell executable to request elevation.'
}

function Request-ChocolateyElevation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ManagerName
    )

    $tempPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "tidywindow-choco-" + ([System.Guid]::NewGuid().ToString('N')) + '.json')

    $shellPath = Get-TidyPowerShellExecutable
    $escapedScript = $callerModulePath -replace "'", "''"
    $escapedManager = $ManagerName -replace "'", "''"
    $escapedResult = $tempPath -replace "'", "''"
    $command = "& '$escapedScript' -Manager '$escapedManager' -Elevated -ResultPath '$escapedResult'"

    Write-TidyLog -Level Information -Message 'Requesting administrator approval for Chocolatey install or repair.'
    Write-TidyOutput -Message 'Requesting administrator approval. Windows may prompt for permission.'

    try {
        Start-Process -FilePath $shellPath -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $command) -Verb RunAs -WindowStyle Hidden -Wait | Out-Null
    }
    catch {
        throw 'Administrator approval was denied or cancelled.'
    }

    if (-not (Test-Path -Path $tempPath)) {
        throw 'Administrator approval was denied before the operation could start.'
    }

    try {
        $json = Get-Content -Path $tempPath -Raw -ErrorAction Stop
        $result = ConvertFrom-Json -InputObject $json -ErrorAction Stop
        if ($result -is [System.Collections.IEnumerable] -and -not ($result -is [string])) {
            $resultArray = @($result)
            if ($resultArray.Count -eq 1) {
                $result = $resultArray[0]
            }
        }
        elseif ($result -isnot [System.Collections.IDictionary] -and $result -isnot [System.Management.Automation.PSObject]) {
            $wrappedOutput = if ($null -ne $result) { @($result) } else { @() }
            $result = [pscustomobject]@{
                Success = $true
                Output  = $wrappedOutput
                Errors  = @()
            }
        }
    }
    finally {
        Remove-Item -Path $tempPath -ErrorAction SilentlyContinue
    }

    return $result
}

function Invoke-ChocolateyBootstrap {
    if ((-not (Test-TidyAdmin)) -and (-not $Elevated.IsPresent)) {
        $elevationResult = Request-ChocolateyElevation -ManagerName $Manager

        if ($null -eq $elevationResult) {
            throw 'Failed to capture the elevated Chocolatey result.'
        }

        $outputValue = Get-TidyResultProperty -Result $elevationResult -Name 'Output'
        $errorValue = Get-TidyResultProperty -Result $elevationResult -Name 'Errors'
        $successValue = Get-TidyResultProperty -Result $elevationResult -Name 'Success'

        $outputLines = @()
        foreach ($line in @($outputValue)) {
            if ($null -ne $line -and -not [string]::IsNullOrWhiteSpace([string]$line)) {
                $outputLines += [string]$line
            }
        }

        $errorLines = @()
        foreach ($line in @($errorValue)) {
            if ($null -ne $line -and -not [string]::IsNullOrWhiteSpace([string]$line)) {
                $errorLines += [string]$line
            }
        }

        foreach ($line in $outputLines) {
            Write-TidyOutput -Message $line
        }

        foreach ($line in $errorLines) {
            Write-TidyError -Message $line
        }

        $elevatedSucceeded = $true
        if ($successValue -is [bool]) {
            $elevatedSucceeded = $successValue
        }
        elseif ($null -ne $successValue) {
            $elevatedSucceeded = [System.Convert]::ToBoolean($successValue)
        }

        if (-not $elevatedSucceeded) {
            throw 'Chocolatey install or repair failed when running with administrator privileges.'
        }

        if ($outputLines.Count -gt 0) {
            return $outputLines[$outputLines.Count - 1]
        }

        return 'Chocolatey operation completed with administrator privileges.'
    }

    if (Test-TidyCommand -CommandName 'choco') {
        Invoke-TidyCommand -Command { choco upgrade chocolatey -y --no-progress } -Description 'Upgrading Chocolatey to repair installation.' -RequireSuccess
        return 'Chocolatey upgrade completed.'
    }

    Write-TidyLog -Level Information -Message 'Installing Chocolatey. This process can take a few minutes.'
    Invoke-TidyCommand -Command { Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force } -Description 'Ensuring execution policy for Chocolatey install.'
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
    $installContent = (New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1')
    if ([string]::IsNullOrWhiteSpace($installContent)) {
        throw 'Failed to download Chocolatey bootstrap script.'
    }

    Write-TidyOutput -Message 'Downloaded Chocolatey bootstrap script.'
    Invoke-TidyCommand -Command { param($script) Invoke-Expression $script } -Arguments @($installContent) -Description 'Running Chocolatey bootstrap script.' -RequireSuccess
    return 'Chocolatey installation completed.'
}

$normalized = ($Manager ?? '').Trim().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($normalized)) {
    throw 'Manager name must be provided.'
}

try {
    Write-TidyLog -Level Information -Message ("Install or repair requested for manager '{0}'. Elevated flag: {1}" -f $Manager, $Elevated.IsPresent)
    switch ($normalized) {
        'scoop' {
            $message = Invoke-ScoopBootstrap
            Write-TidyOutput -Message $message
        }
        'scoop package manager' {
            $message = Invoke-ScoopBootstrap
            Write-TidyOutput -Message $message
        }
        'choco' {
            $message = Invoke-ChocolateyBootstrap
            Write-TidyOutput -Message $message
        }
        'chocolatey' {
            $message = Invoke-ChocolateyBootstrap
            Write-TidyOutput -Message $message
        }
        'chocolatey cli' {
            $message = Invoke-ChocolateyBootstrap
            Write-TidyOutput -Message $message
        }
        'winget' {
            Write-TidyLog -Level Warning -Message 'winget is distributed by Microsoft and cannot be installed via automation. Visit the Store to reinstall if required.'
            Write-TidyOutput -Message 'winget is managed by Windows. Use the Microsoft Store to repair or reinstall the App Installer package.'
        }
        default {
            throw "Package manager '$Manager' is not supported by the installer."
        }
    }

    $commandLookupName = switch ($normalized) {
        'chocolatey' { 'choco' }
        'chocolatey cli' { 'choco' }
        'scoop package manager' { 'scoop' }
        default { $normalized }
    }

    $commandPath = Get-TidyCommandPath -CommandName $commandLookupName
    if ([string]::IsNullOrWhiteSpace($commandPath)) {
        $commandPath = Get-TidyCommandPath -CommandName $Manager
    }

    if (-not [string]::IsNullOrWhiteSpace($commandPath)) {
        Write-TidyOutput -Message ("Detected '{0}' at {1}." -f $Manager, $commandPath)
    }
}
catch {
    $script:OperationSucceeded = $false
    $message = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($message)) {
        $message = $_ | Out-String
    }

    Write-TidyLog -Level Error -Message $message
    Write-TidyError -Message $message
    if (-not $script:UsingResultFile) {
        throw
    }
}
finally {
    Save-TidyResult
    Write-TidyLog -Level Information -Message ("Install or repair flow finished for manager '{0}'." -f $Manager)
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

