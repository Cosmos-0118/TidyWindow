param(
    [Parameter(Mandatory = $true)]
    [string] $Manager,
    [Parameter(Mandatory = $true)]
    [string] $PackageId,
    [string] $DisplayName,
    [switch] $RequiresAdmin,
    [switch] $Elevated,
    [string] $ResultPath,
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($DisplayName)) {
    $DisplayName = $PackageId
}

$normalizedManager = $Manager.Trim()
$managerKey = $normalizedManager.ToLowerInvariant()
$needsElevation = $RequiresAdmin.IsPresent -or $managerKey -in @('winget', 'choco', 'chocolatey')

$script:TidyOutput = [System.Collections.Generic.List[string]]::new()
$script:TidyErrors = [System.Collections.Generic.List[string]]::new()
$script:ResultPayload = $null
$script:UsingResultFile = -not [string]::IsNullOrWhiteSpace($ResultPath)

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

$callerModulePath = $PSCmdlet.MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath)) {
    $callerModulePath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -Path $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

function Add-TidyOutput {
    param([object] $Message)

    $text = Convert-TidyLogMessage -InputObject $Message
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        [void]$script:TidyOutput.Add($text)
    }
}

function Add-TidyError {
    param([object] $Message)

    $text = Convert-TidyLogMessage -InputObject $Message
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        [void]$script:TidyErrors.Add($text)
    }
}

function Save-TidyResult {
    if (-not $script:UsingResultFile) {
        return
    }

    if ($null -eq $script:ResultPayload) {
        return
    }

    $json = $script:ResultPayload | ConvertTo-Json -Depth 6
    Set-Content -LiteralPath $ResultPath -Value $json -Encoding UTF8
}

function Test-TidyAdmin {
    return [bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-TidyPowerShellExecutable {
    if ($PSVersionTable.PSEdition -eq 'Core') {
        $pwsh = Get-Command -Name 'pwsh' -ErrorAction SilentlyContinue
        if ($pwsh) { return $pwsh.Source }
    }

    $legacy = Get-Command -Name 'powershell.exe' -ErrorAction SilentlyContinue
    if ($legacy) { return $legacy.Source }

    throw 'Unable to locate a PowerShell executable to request elevation.'
}

function ConvertTo-TidyArgument {
    param([Parameter(Mandatory = $true)][string] $Value)

    $escaped = $Value -replace '"', '""'
    return "`"$escaped`""
}

function Request-TidyElevation {
    param(
        [Parameter(Mandatory = $true)][string] $ScriptPath,
        [Parameter(Mandatory = $true)][string] $Manager,
        [Parameter(Mandatory = $true)][string] $PackageId,
        [Parameter(Mandatory = $true)][string] $DisplayName
    )

    $resultTemp = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "tidywindow-remove-" + ([System.Guid]::NewGuid().ToString('N')) + '.json')
    $shellPath = Get-TidyPowerShellExecutable

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (ConvertTo-TidyArgument -Value $ScriptPath),
        '-Manager', (ConvertTo-TidyArgument -Value $Manager),
        '-PackageId', (ConvertTo-TidyArgument -Value $PackageId),
        '-DisplayName', (ConvertTo-TidyArgument -Value $DisplayName),
        '-RequiresAdmin',
        '-Elevated',
        '-ResultPath', (ConvertTo-TidyArgument -Value $resultTemp)
    )

    try {
        Start-Process -FilePath $shellPath -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait | Out-Null
    }
    catch {
        throw 'Administrator approval was denied or the request was cancelled.'
    }

    if (-not (Test-Path -LiteralPath $resultTemp)) {
        throw 'Administrator approval was denied before the removal could start.'
    }

    try {
        $json = Get-Content -LiteralPath $resultTemp -Raw -ErrorAction Stop
        return ConvertFrom-Json -InputObject $json -ErrorAction Stop
    }
    finally {
        Remove-Item -LiteralPath $resultTemp -ErrorAction SilentlyContinue
    }
}

if ($needsElevation -and -not $Elevated.IsPresent -and -not (Test-TidyAdmin)) {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        $scriptPath = $MyInvocation.MyCommand.Path
    }

    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        throw 'Unable to determine script path for elevation.'
    }

    $result = Request-TidyElevation -ScriptPath $scriptPath -Manager $normalizedManager -PackageId $PackageId -DisplayName $DisplayName
    $result | ConvertTo-Json -Depth 6
    return
}

function Resolve-ManagerExecutable {
    param([string] $Key)

    switch ($Key) {
        'winget' {
            $cmd = Get-Command -Name 'winget' -ErrorAction SilentlyContinue
            if (-not $cmd) { throw 'winget CLI was not found on this machine.' }
            if ($cmd.Source) { return $cmd.Source }
            return 'winget'
        }
        'choco' {
            $cmd = Get-Command -Name 'choco' -ErrorAction SilentlyContinue
            if (-not $cmd) { throw 'Chocolatey CLI was not found on this machine.' }
            if ($cmd.Source) { return $cmd.Source }
            return 'choco'
        }
        'chocolatey' {
            $cmd = Get-Command -Name 'choco' -ErrorAction SilentlyContinue
            if (-not $cmd) { throw 'Chocolatey CLI was not found on this machine.' }
            if ($cmd.Source) { return $cmd.Source }
            return 'choco'
        }
        'scoop' {
            $cmd = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue
            if (-not $cmd) { throw 'Scoop CLI was not found on this machine.' }
            if ($cmd.Source) { return $cmd.Source }
            return 'scoop'
        }
        default { throw "Unsupported package manager '$Key'." }
    }
}


function Invoke-Removal {
    param([string] $Key, [string] $PackageId)

    $exe = Resolve-ManagerExecutable -Key $Key
    $arguments = switch ($Key) {
    'winget' { @('uninstall', '--id', $PackageId, '-e', '--accept-source-agreements', '--disable-interactivity') }
        'choco' { @('uninstall', $PackageId, '-y', '--no-progress') }
        'chocolatey' { @('uninstall', $PackageId, '-y', '--no-progress') }
        'scoop' { @('uninstall', $PackageId) }
        default { throw "Unsupported package manager '$Key' for removal." }
    }

    $output = & $exe @arguments 2>&1
    $exitCode = $LASTEXITCODE

    $logs = [System.Collections.Generic.List[string]]::new()
    $errors = [System.Collections.Generic.List[string]]::new()

    foreach ($entry in @($output)) {
        if ($null -eq $entry) { continue }
        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            $message = [string]$entry
            if (-not [string]::IsNullOrWhiteSpace($message)) { [void]$errors.Add($message) }
        }
        else {
            $message = [string]$entry
            if (-not [string]::IsNullOrWhiteSpace($message)) { [void]$logs.Add($message) }
        }
    }

    $summary = if ($exitCode -eq 0) { 'Removal command completed.' } else { "Removal command exited with code $exitCode." }

    return [pscustomobject]@{
        Attempted = $true
        ExitCode = $exitCode
        Output = $logs.ToArray()
        Errors = $errors.ToArray()
        Summary = $summary
    }
}

$installedBefore = Get-TidyInstalledPackageVersion -Manager $managerKey -PackageId $PackageId
$statusBefore = if ([string]::IsNullOrWhiteSpace($installedBefore)) { 'NotInstalled' } else { 'Installed' }
$attempted = $false
$exitCode = 0
$operationSucceeded = $false
$summary = $null

try {
    if ($statusBefore -eq 'NotInstalled') {
        $summary = "Package '$DisplayName' is not currently installed."
        $operationSucceeded = $true
    }
    elseif ($DryRun.IsPresent) {
        $summary = "Dry run: package '$DisplayName' is installed."
        $operationSucceeded = $true
    }
    else {
        $attempt = Invoke-Removal -Key $managerKey -PackageId $PackageId
        $attempted = $attempt.Attempted
        $exitCode = $attempt.ExitCode
        foreach ($line in $attempt.Output) { Add-TidyOutput -Message $line }
        foreach ($line in $attempt.Errors) { Add-TidyError -Message $line }
        if (-not [string]::IsNullOrWhiteSpace($attempt.Summary)) { $summary = $attempt.Summary }
        if ($exitCode -ne 0) { $operationSucceeded = $false } else { $operationSucceeded = $true }
    }
}
catch {
    $operationSucceeded = $false
    $message = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($message)) { $message = $_.ToString() }
    Add-TidyError -Message $message
    if (-not $summary) { $summary = $message }
}

$installedAfter = Get-TidyInstalledPackageVersion -Manager $managerKey -PackageId $PackageId
$statusAfter = if ([string]::IsNullOrWhiteSpace($installedAfter)) { 'NotInstalled' } else { 'Installed' }

if ($statusAfter -eq 'NotInstalled' -or $DryRun.IsPresent) {
    $operationSucceeded = $true
}

if ([string]::IsNullOrWhiteSpace($summary)) {
    $summary = if ($operationSucceeded) { "Package '$DisplayName' removed." } else { "Package '$DisplayName' removal failed." }
}

$script:ResultPayload = [pscustomobject]@{
    operation = 'remove'
    manager = $normalizedManager
    packageId = $PackageId
    displayName = $DisplayName
    requiresAdmin = $needsElevation
    statusBefore = $statusBefore
    statusAfter = $statusAfter
    installedVersion = if ([string]::IsNullOrWhiteSpace($installedAfter)) { $null } else { $installedAfter }
    succeeded = [bool]$operationSucceeded
    attempted = [bool]$attempted
    exitCode = [int]$exitCode
    summary = $summary
    output = $script:TidyOutput
    errors = $script:TidyErrors
}

try {
    Save-TidyResult
}
finally {
    $script:ResultPayload | ConvertTo-Json -Depth 6
}
