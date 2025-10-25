param(
    [Parameter(Mandatory = $true)]
    [string] $PackageId,
    [Parameter(Mandatory = $true)]
    [string] $DisplayName,
    [Parameter(Mandatory = $true)]
    [string] $Manager,
    [Parameter()]
    [string] $Command,
    [switch] $RequiresAdmin,
    [switch] $Elevated,
    [string] $ResultPath,
    [string] $PayloadPath,
    [string[]] $Buckets
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [string]::IsNullOrWhiteSpace($PayloadPath) -and (Test-Path -Path $PayloadPath)) {
    try {
        $payloadJson = Get-Content -Path $PayloadPath -Raw -ErrorAction Stop
        $payload = ConvertFrom-Json -InputObject $payloadJson -ErrorAction Stop

        if ($payload.PackageId) { $PackageId = [string]$payload.PackageId }
        if ($payload.DisplayName) { $DisplayName = [string]$payload.DisplayName }
        if ($payload.Manager) { $Manager = [string]$payload.Manager }
        if ($payload.Command) { $Command = [string]$payload.Command }
        if ($payload.RequiresAdmin) { $RequiresAdmin = [bool]$payload.RequiresAdmin }
    if ($payload.Buckets) { $Buckets = @($payload.Buckets | ForEach-Object { [string]$_ }) }
    }
    catch {
        Write-Host "[WARN] Failed to parse payload file. Using provided parameters. $_"
    }
    finally {
        Remove-Item -Path $PayloadPath -ErrorAction SilentlyContinue
    }
}

$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath) -and (Get-Variable -Name PSCommandPath -Scope Script -ErrorAction SilentlyContinue)) {
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
    Write-Output "[ERROR] $text"
}

function Resolve-TidyKnownInstallerOutcome {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Manager,
        [Parameter()]
        [int] $ExitCode = 0,
        [Parameter()]
        [string] $DisplayName = '',
        [string[]] $OutputLines,
        [string[]] $ErrorLines
    )

    $result = [pscustomobject]@{
        Handled = $false
        Success = $false
        Message = $null
    }

    if ([string]::IsNullOrWhiteSpace($Manager)) {
        return $result
    }

    $normalizedManager = $Manager.Trim().ToLowerInvariant()
    $combinedLines = @()

    if ($OutputLines) {
        $combinedLines += $OutputLines
    }

    if ($ErrorLines) {
        $combinedLines += $ErrorLines
    }

    if ($normalizedManager -eq 'winget') {
        $knownSuccessCodes = @(-1978335189, -1978334963)
        $successPatterns = @(
            'No available upgrade found',
            'No applicable update',
            'No updates available',
            'already installed',
            'Found an existing package already installed'
        )

        $matchedPattern = $false

        foreach ($line in $combinedLines) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            foreach ($pattern in $successPatterns) {
                if ($line -match $pattern) {
                    $matchedPattern = $true
                    break
                }
            }

            if ($matchedPattern) {
                break
            }
        }

        if ($knownSuccessCodes -contains $ExitCode -or $matchedPattern) {
            $result.Handled = $true
            $result.Success = $true
            $subject = [string]::IsNullOrWhiteSpace($DisplayName) ? 'Package' : $DisplayName
            $result.Message = "$subject is already installed or up to date."
            return $result
        }
    }

    return $result
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

function Get-TidyScoopBucketNames {
    try {
        $result = & scoop bucket list 2>&1
        $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

        foreach ($line in @($result)) {
            $candidate = [string]$line
            if ([string]::IsNullOrWhiteSpace($candidate)) {
                continue
            }

            $trimmed = $candidate.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) {
                continue
            }

            if ($trimmed.EndsWith(':')) {
                continue
            }

            $bucketName = $trimmed.Split([char[]]@(' ', '	'))[0]
            if (-not [string]::IsNullOrWhiteSpace($bucketName)) {
                [void]$names.Add($bucketName)
            }
        }

        return $names
    }
    catch {
        return $null
    }
}

function Test-TidyScoopBucketExists {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Bucket,
        [System.Collections.Generic.HashSet[string]] $ExistingSet
    )

    if ([string]::IsNullOrWhiteSpace($Bucket)) {
        return $false
    }

    $normalized = $Bucket.Trim()
    if ($ExistingSet -and $ExistingSet.Contains($normalized)) {
        return $true
    }

    $probeRoots = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:SCOOP)) {
        $probeRoots.Add((Join-Path -Path $env:SCOOP -ChildPath 'buckets'))
    }

    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $defaultRoot = Join-Path -Path $env:USERPROFILE -ChildPath 'scoop\buckets'
        if (-not [string]::IsNullOrWhiteSpace($defaultRoot)) {
            $probeRoots.Add($defaultRoot)
        }
    }

    $visited = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($root in $probeRoots) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        if (-not $visited.Add($root)) {
            continue
        }

        try {
            $candidate = Join-Path -Path $root -ChildPath $normalized
        }
        catch {
            continue
        }

        if (Test-Path -LiteralPath $candidate) {
            if ($ExistingSet) {
                [void]$ExistingSet.Add($normalized)
            }

            return $true
        }
    }

    return $false
}

function Ensure-TidyScoopBuckets {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Buckets
    )

    if ($null -eq $Buckets -or $Buckets.Length -eq 0) {
        return
    }

    $uniqueBuckets = [System.Collections.Generic.List[string]]::new()
    $bucketSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($bucket in @($Buckets)) {
        if ([string]::IsNullOrWhiteSpace($bucket)) {
            continue
        }

        $trimmed = $bucket.Trim()
        if ($bucketSet.Add($trimmed)) {
            [void]$uniqueBuckets.Add($trimmed)
        }
    }

    if ($uniqueBuckets.Count -eq 0) {
        return
    }

    $existingSet = Get-TidyScoopBucketNames
    if ($null -eq $existingSet) {
        $existingSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    }

    foreach ($bucket in $uniqueBuckets) {
        if (Test-TidyScoopBucketExists -Bucket $bucket -ExistingSet $existingSet) {
            Write-TidyOutput -Message "Scoop bucket '$bucket' already present. Skipping add."
            continue
        }

        Write-TidyLog -Level Information -Message "Adding Scoop bucket '$bucket'."
        $addOutput = & scoop bucket add $bucket 2>&1
        $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { [int]$LASTEXITCODE } else { 0 }

        $capturedLines = @()
        foreach ($entry in @($addOutput)) {
            if ($null -eq $entry) {
                continue
            }

            $messageText = Convert-TidyLogMessage -InputObject $entry
            if ([string]::IsNullOrWhiteSpace($messageText)) {
                continue
            }

            $capturedLines += $messageText
            Write-TidyOutput -Message $messageText
        }

        $detectedExisting = $false
        if ($capturedLines.Count -gt 0) {
            foreach ($line in $capturedLines) {
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }

                if ($line -match '(?i)\bbucket\b.*\balready exists\b') {
                    $detectedExisting = $true
                    break
                }
            }
        }

        if ($exitCode -ne 0 -and -not $detectedExisting) {
            $joinedOutput = $capturedLines -join [Environment]::NewLine
            if (-not [string]::IsNullOrWhiteSpace($joinedOutput) -and $joinedOutput.IndexOf('already exists', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $detectedExisting = $true
            }
        }

        if ($detectedExisting) {
            Write-TidyOutput -Message "Scoop bucket '$bucket' already present. Skipping add."
            $exitCode = 0
        }

        if ($exitCode -ne 0) {
            $refreshedBuckets = Get-TidyScoopBucketNames
            if ($refreshedBuckets -and $refreshedBuckets.Contains($bucket)) {
                Write-TidyOutput -Message "Scoop bucket '$bucket' detected after add attempt. Treating as success."
                $exitCode = 0
                $existingSet = $refreshedBuckets
            }
        }

        if ($exitCode -ne 0 -and (Test-TidyScoopBucketExists -Bucket $bucket -ExistingSet $existingSet)) {
            Write-TidyOutput -Message "Scoop bucket '$bucket' found on disk. Skipping add."
            $exitCode = 0
        }

        if ($exitCode -ne 0) {
            $joinedOutput = $capturedLines -join [Environment]::NewLine
            if (-not [string]::IsNullOrWhiteSpace($joinedOutput)) {
                throw "Failed to add Scoop bucket '$bucket'. Output:`n$joinedOutput"
            }

            throw "Failed to add Scoop bucket '$bucket'."
        }

        [void]$existingSet.Add($bucket)
    }
}

function Request-TidyElevation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ScriptPath,
        [Parameter(Mandatory = $true)]
        [string] $PackageId,
        [Parameter(Mandatory = $true)]
        [string] $DisplayName,
        [Parameter(Mandatory = $true)]
        [string] $Manager,
        [Parameter(Mandatory = $true)]
        [string] $Command,
        [string[]] $Buckets
    )

    if ($null -eq $Buckets) {
        $Buckets = @()
    }

    $resultTemp = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "tidywindow-install-" + ([System.Guid]::NewGuid().ToString('N')) + '.json')
    $payloadTemp = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "tidywindow-install-" + ([System.Guid]::NewGuid().ToString('N')) + '.payload.json')

    $payload = [pscustomobject]@{
        PackageId     = $PackageId
        DisplayName   = $DisplayName
        Manager       = $Manager
        Command       = $Command
        RequiresAdmin = $true
        Buckets       = $Buckets
    }

    $payload | ConvertTo-Json -Depth 5 | Set-Content -Path $payloadTemp -Encoding UTF8

    $shellPath = Get-TidyPowerShellExecutable

    function ConvertTo-TidyArgument {
        param(
            [Parameter(Mandatory = $true)]
            [string] $Value
        )

        $escaped = $Value -replace '"', '""'
        return "`"$escaped`""
    }

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (ConvertTo-TidyArgument -Value $ScriptPath),
        '-PackageId', (ConvertTo-TidyArgument -Value $PackageId),
        '-DisplayName', (ConvertTo-TidyArgument -Value $DisplayName),
        '-Manager', (ConvertTo-TidyArgument -Value $Manager),
        '-Elevated',
        '-ResultPath', (ConvertTo-TidyArgument -Value $resultTemp),
        '-PayloadPath', (ConvertTo-TidyArgument -Value $payloadTemp)
    )

    Write-TidyLog -Level Information -Message "Requesting administrator approval to install '$DisplayName'."
    Write-TidyOutput -Message 'Requesting administrator approval. Windows may prompt for permission.'

    try {
        Start-Process -FilePath $shellPath -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait | Out-Null
    }
    catch {
        throw 'Administrator approval was denied or cancelled.'
    }

    if (-not (Test-Path -Path $resultTemp)) {
        throw 'Administrator approval was denied before the operation could start.'
    }

    try {
        $json = Get-Content -Path $resultTemp -Raw -ErrorAction Stop
        $result = ConvertFrom-Json -InputObject $json -ErrorAction Stop
        return $result
    }
    finally {
        Remove-Item -Path $resultTemp -ErrorAction SilentlyContinue
        Remove-Item -Path $payloadTemp -ErrorAction SilentlyContinue
    }
}

function Get-TidyResultProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Result,
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($null -eq $Result) {
        return $null
    }

    $property = $Result.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

if ([string]::IsNullOrWhiteSpace($PackageId)) {
    throw 'PackageId must be provided.'
}

if ([string]::IsNullOrWhiteSpace($DisplayName)) {
    $DisplayName = $PackageId
}

if ([string]::IsNullOrWhiteSpace($Manager)) {
    throw 'Manager must be provided.'
}

if ([string]::IsNullOrWhiteSpace($Command)) {
    throw 'Command must be provided.'
}

$bucketCollector = [System.Collections.Generic.List[string]]::new()
$bucketSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($bucket in @($Buckets)) {
    if ([string]::IsNullOrWhiteSpace($bucket)) {
        continue
    }

    $trimmedBucket = $bucket.Trim()
    if (-not [string]::IsNullOrWhiteSpace($trimmedBucket) -and $bucketSet.Add($trimmedBucket)) {
        [void]$bucketCollector.Add($trimmedBucket)
    }
}

$Buckets = $bucketCollector.ToArray()

try {
    if ($RequiresAdmin.IsPresent -and -not $Elevated.IsPresent -and -not (Test-TidyAdmin)) {
    $result = Request-TidyElevation -ScriptPath $callerModulePath -PackageId $PackageId -DisplayName $DisplayName -Manager $Manager -Command $Command -Buckets $Buckets

        $outputLines = @()
        $resultOutput = Get-TidyResultProperty -Result $result -Name 'Output'
        if ($null -ne $resultOutput) {
            foreach ($line in @($resultOutput)) {
                if ($null -ne $line -and -not [string]::IsNullOrWhiteSpace([string]$line)) {
                    $outputLines += [string]$line
                }
            }
        }

        $errorLines = @()
        $resultErrors = Get-TidyResultProperty -Result $result -Name 'Errors'
        if ($null -ne $resultErrors) {
            foreach ($line in @($resultErrors)) {
                if ($null -ne $line -and -not [string]::IsNullOrWhiteSpace([string]$line)) {
                    $errorLines += [string]$line
                }
            }
        }

        foreach ($line in $outputLines) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            Write-TidyOutput -Message $line
        }

        foreach ($line in $errorLines) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            Write-TidyError -Message $line
        }

        $resultSuccess = Get-TidyResultProperty -Result $result -Name 'Success'
        if ($null -ne $resultSuccess -and -not [bool]$resultSuccess) {
            throw "Installation failed for '$DisplayName' when running with elevated privileges."
        }

        return
    }

    Write-TidyLog -Level Information -Message "Installing '$DisplayName' using manager '$Manager'."

    $commandText = $Command.Trim()

    if ($commandText.IndexOf("`n") -ge 0 -or $commandText.IndexOf("`r") -ge 0) {
        $normalizedLines = @()

        foreach ($segment in ($commandText -split "`r?`n")) {
            if ([string]::IsNullOrWhiteSpace($segment)) {
                continue
            }

            $normalizedLines += $segment.Trim()
        }

        if ($normalizedLines.Count -gt 0) {
            $commandText = [string]::Join(' ', $normalizedLines)
        }
        else {
            $commandText = ''
        }
    }

    if ([string]::IsNullOrWhiteSpace($commandText)) {
        throw "Resolved command text was empty for '$DisplayName'."
    }
    if ($Manager -ieq 'choco') {
        if (-not (Get-Command -Name 'choco' -ErrorAction SilentlyContinue)) {
            throw 'Chocolatey command not found. Install Chocolatey first.'
        }
    }

    if ($Manager -ieq 'scoop') {
        $scoopCommand = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue
        if (-not $scoopCommand) {
            Write-TidyLog -Level Warning -Message 'Scoop command not detected. Attempting bootstrap.'
            $installScript = Invoke-RestMethod -Uri 'https://get.scoop.sh' -UseBasicParsing
            if ([string]::IsNullOrWhiteSpace($installScript)) {
                throw 'Failed to download Scoop bootstrap script.'
            }

            if (Test-TidyAdmin) {
                $tempScript = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('tidywindow-scoop-install-' + ([System.Guid]::NewGuid().ToString('N')) + '.ps1')
                try {
                    Set-Content -Path $tempScript -Value $installScript -Encoding UTF8
                    & $tempScript -RunAsAdmin
                }
                finally {
                    Remove-Item -Path $tempScript -ErrorAction SilentlyContinue
                }
            }
            else {
                & ([scriptblock]::Create($installScript))
            }
            $scoopCommand = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue
        }

        if ($scoopCommand -and $Buckets.Length -gt 0) {
            Ensure-TidyScoopBuckets -Buckets $Buckets
        }
    }

    $invocationOutput = @()
    $success = $true
    $invocationSucceeded = $true
    $exitCode = $null

    try {
        $invocationOutput = Invoke-Expression $commandText 2>&1
        $invocationSucceeded = $?
        if (Test-Path -Path 'variable:LASTEXITCODE') {
            $exitCode = $LASTEXITCODE
        }
        else {
            $exitCode = 0
        }
    }
    catch {
        $success = $false
        $invocationSucceeded = $false
        $exitCode = 1
        $invocationOutput += $_
    }

    foreach ($entry in @($invocationOutput)) {
        if ($null -eq $entry) {
            continue
        }

        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            $entryMessage = [string]$entry
            if (-not [string]::IsNullOrWhiteSpace($entryMessage)) {
                Write-TidyError -Message $entryMessage
            }
        }
        else {
            $outputMessage = [string]$entry
            if (-not [string]::IsNullOrWhiteSpace($outputMessage)) {
                Write-TidyOutput -Message $outputMessage
            }
        }
    }

    $knownOutcome = Resolve-TidyKnownInstallerOutcome -Manager $Manager -ExitCode $exitCode -DisplayName $DisplayName -OutputLines $script:TidyOutputLines.ToArray() -ErrorLines $script:TidyErrorLines.ToArray()

    if ($knownOutcome.Handled) {
        if ($knownOutcome.Success) {
            if (-not [string]::IsNullOrWhiteSpace($knownOutcome.Message)) {
                Write-TidyOutput -Message $knownOutcome.Message
            }

            $success = $true
            $invocationSucceeded = $true
            $exitCode = 0
            $script:TidyErrorLines.Clear()
        }
        else {
            if (-not [string]::IsNullOrWhiteSpace($knownOutcome.Message)) {
                Write-TidyError -Message $knownOutcome.Message
            }
        }
    }

    if (-not $success -or -not $invocationSucceeded) {
        throw "Installation command returned a non-success status for '$DisplayName'."
    }

    if ($exitCode -ne $null -and $exitCode -ne 0) {
        throw "Installer exited with code $exitCode for '$DisplayName'."
    }

    Write-TidyOutput -Message "Installation command completed for '$DisplayName'."
}
catch {
    $script:OperationSucceeded = $false
    $message = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($message)) {
        $message = $_.ToString()
    }

    if (-not [string]::IsNullOrWhiteSpace($message)) {
        Write-TidyError -Message $message
    }
    if (-not $script:UsingResultFile) {
        throw
    }
}
finally {
    Save-TidyResult
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
