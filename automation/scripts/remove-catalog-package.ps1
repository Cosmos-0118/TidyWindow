param(
    [Parameter(Mandatory = $true)]
    [string] $Manager,
    [Parameter(Mandatory = $true)]
    [string] $PackageId,
    [string] $DisplayName,
    [switch] $RequiresAdmin,
    [switch] $Elevated,
    [string] $ResultPath,
    [switch] $DryRun,
    [switch] $ForceCleanup
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

function ConvertTo-TidyStringArray {
    param([object] $Source)

    if ($null -eq $Source) {
        return [string[]]@()
    }

    if ($Source -is [System.Management.Automation.PSObject]) {
        return ConvertTo-TidyStringArray -Source $Source.PSObject.BaseObject
    }

    if ($Source -is [string[]]) {
        return [string[]]$Source
    }

    if ($Source -is [string]) {
        return [string[]]@([string]$Source)
    }

    $collector = [System.Collections.Generic.List[string]]::new()

    if ($Source -is [System.Collections.IEnumerable]) {
        foreach ($item in $Source) {
            if ($null -eq $item) { continue }
            $collector.Add([string]$item)
        }
    }
    else {
        $collector.Add([string]$Source)
    }

    if ($collector.Count -eq 0) {
        return [string[]]@()
    }

    $result = [string[]]::new($collector.Count)
    $collector.CopyTo($result, 0)
    return $result
}

function Get-TidyCollectionCount {
    param([object] $Source)

    if ($null -eq $Source) {
        return 0
    }

    if ($Source -is [System.Management.Automation.PSObject]) {
        return Get-TidyCollectionCount -Source $Source.PSObject.BaseObject
    }

    if ($Source -is [string]) {
        return if ([string]::IsNullOrWhiteSpace($Source)) { 0 } else { 1 }
    }

    if ($Source -is [System.Array]) {
        return $Source.Length
    }

    if ($Source -is [System.Collections.ICollection]) {
        return [int]$Source.Count
    }

    if ($Source -is [System.Collections.IEnumerable]) {
        $count = 0
        foreach ($item in $Source) {
            $count++
        }
        return $count
    }

    return 1
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
        [Parameter(Mandatory = $true)][string] $DisplayName,
        [switch] $ForceCleanup
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

    if ($ForceCleanup.IsPresent) {
        $arguments += '-ForceCleanup'
    }

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

    $result = Request-TidyElevation -ScriptPath $scriptPath -Manager $normalizedManager -PackageId $PackageId -DisplayName $DisplayName -ForceCleanup:$ForceCleanup
    $result | ConvertTo-Json -Depth 6 -Compress
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
    $baseArguments = switch ($Key) {
        'winget' { @('uninstall', '--id', $PackageId, '-e', '--accept-source-agreements', '--disable-interactivity') }
        'choco' { @('uninstall', $PackageId, '-y', '--no-progress') }
        'chocolatey' { @('uninstall', $PackageId, '-y', '--no-progress') }
        'scoop' { @('uninstall', $PackageId) }
        default { throw "Unsupported package manager '$Key' for removal." }
    }

    $logs = [System.Collections.Generic.List[string]]::new()
    $errors = [System.Collections.Generic.List[string]]::new()

    $invokeAndCollect = {
        param([string[]] $ArgumentList)

        $result = & $exe @ArgumentList 2>&1
        $code = $LASTEXITCODE

        foreach ($entry in @($result)) {
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

        return $code
    }

    $exitCode = & $invokeAndCollect $baseArguments

    $summary = 'Removal command completed.'

    if ($exitCode -ne 0) {
        switch ($Key) {
            'winget' {
                $needsRetry = $false
                foreach ($collection in @($logs, $errors)) {
                    foreach ($message in $collection) {
                        if ([string]::IsNullOrWhiteSpace($message)) { continue }
                        if ($message -like '*Multiple versions of this package are installed*') {
                            $needsRetry = $true
                            break
                        }
                    }

                    if ($needsRetry) { break }
                }

                if (-not $needsRetry -and $exitCode -eq -1978335210) {
                    $needsRetry = $true
                }

                if ($needsRetry) {
                    [void]$logs.Add('Detected multiple installed versions; retrying uninstall with --all-versions.')
                    $retryArgs = @('uninstall', '--id', $PackageId, '-e', '--accept-source-agreements', '--disable-interactivity', '--all-versions')
                    $exitCode = & $invokeAndCollect $retryArgs
                    if ($exitCode -eq 0) {
                        $summary = 'Removal completed after retry with --all-versions.'
                    }
                }
            }
            'choco' {
                $needsRetry = $false
                foreach ($collection in @($logs, $errors)) {
                    foreach ($message in $collection) {
                        if ([string]::IsNullOrWhiteSpace($message)) { continue }
                        if ($message -like '*is not installed*' -or $message -like '*not installed*') {
                            $needsRetry = $false
                            break
                        }

                        if ($message -like '*Unable to resolve dependency*' -or $message -like '*cannot uninstall a package that has dependencies*') {
                            $needsRetry = $true
                            break
                        }
                    }

                    if ($needsRetry) { break }
                }

                if ($needsRetry -and $exitCode -ne 0) {
                    [void]$logs.Add('Detected dependency or multiple install scenario; retrying with --all-versions --remove-dependencies.')
                    $retryArgs = @('uninstall', $PackageId, '-y', '--no-progress', '--all-versions', '--remove-dependencies')
                    $exitCode = & $invokeAndCollect $retryArgs
                    if ($exitCode -eq 0) {
                        $summary = 'Removal completed after retry with --all-versions and dependency cleanup.'
                    }
                }
            }
            'chocolatey' {
                $needsRetry = $false
                foreach ($collection in @($logs, $errors)) {
                    foreach ($message in $collection) {
                        if ([string]::IsNullOrWhiteSpace($message)) { continue }
                        if ($message -like '*is not installed*' -or $message -like '*not installed*') {
                            $needsRetry = $false
                            break
                        }

                        if ($message -like '*Unable to resolve dependency*' -or $message -like '*cannot uninstall a package that has dependencies*') {
                            $needsRetry = $true
                            break
                        }
                    }

                    if ($needsRetry) { break }
                }

                if ($needsRetry -and $exitCode -ne 0) {
                    [void]$logs.Add('Detected dependency or multiple install scenario; retrying with --all-versions --remove-dependencies.')
                    $retryArgs = @('uninstall', $PackageId, '-y', '--no-progress', '--all-versions', '--remove-dependencies')
                    $exitCode = & $invokeAndCollect $retryArgs
                    if ($exitCode -eq 0) {
                        $summary = 'Removal completed after retry with --all-versions and dependency cleanup.'
                    }
                }
            }
            'scoop' {
                $needsRetry = $false
                foreach ($collection in @($logs, $errors)) {
                    foreach ($message in $collection) {
                        if ([string]::IsNullOrWhiteSpace($message)) { continue }
                        if ($message -like '*is not installed*') {
                            $needsRetry = $false
                            break
                        }

                        if ($message -like '*Cannot find app*' -or $message -like '*has multiple versions*' -or $message -like '*use "scoop uninstall* -a"*') {
                            $needsRetry = $true
                            break
                        }
                    }

                    if ($needsRetry) { break }
                }

                if ($needsRetry -and $exitCode -ne 0) {
                    [void]$logs.Add('Detected multiple installed versions; retrying uninstall with scoop -a flag.')
                    $retryArgs = @('uninstall', $PackageId, '-a')
                    $exitCode = & $invokeAndCollect $retryArgs
                    if ($exitCode -eq 0) {
                        $summary = 'Removal completed after retry with scoop -a flag.'
                    }
                }
            }
        }
    }

    if ($Key -eq 'winget' -and $exitCode -ne 0) {
        try {
            $msixCandidates = Get-TidyWingetMsixCandidates -PackageId $PackageId
            foreach ($candidate in @($msixCandidates)) {
                if ($null -eq $candidate) { continue }
                $identifier = $candidate.Identifier
                if ([string]::IsNullOrWhiteSpace($identifier)) { continue }

                [void]$logs.Add("winget uninstall: retrying with identifier '$identifier'.")
                $alternateArgs = @('uninstall', '--id', $identifier, '-e', '--accept-source-agreements', '--disable-interactivity')
                $alternateExit = & $invokeAndCollect $alternateArgs

                if ($alternateExit -eq 0) {
                    $exitCode = 0
                    $summary = 'Removal completed using MSIX identifier.'
                    break
                }
                else {
                    $exitCode = $alternateExit
                }
            }
        }
        catch {
            $msixError = $_.Exception.Message
            if ([string]::IsNullOrWhiteSpace($msixError)) { $msixError = $_.ToString() }
            [void]$errors.Add("winget uninstall: unable to probe MSIX packages: $msixError")
        }
    }

    if ($exitCode -ne 0) {
        $summary = "Removal command exited with code $exitCode."
    }

    return [pscustomobject]@{
        Attempted = $true
        ExitCode  = $exitCode
        Output    = ConvertTo-TidyStringArray -Source $logs
        Errors    = ConvertTo-TidyStringArray -Source $errors
        Summary   = $summary
    }

        if ($result.Output.Count -eq 0 -and $logs -is [System.Collections.IEnumerable]) {
            $materializedLogs = @()
            foreach ($item in $logs) { $materializedLogs += [string]$item }
            $result.Output = $materializedLogs
        }

        if ($result.Errors.Count -eq 0 -and $errors -is [System.Collections.IEnumerable]) {
            $materializedErrors = @()
            foreach ($item in $errors) { $materializedErrors += [string]$item }
            $result.Errors = $materializedErrors
        }
}

function Get-TidyNormalizedName {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $clean = [System.Text.RegularExpressions.Regex]::Replace($Value, '[^A-Za-z0-9]+', ' ')
    $clean = $clean.Trim()
    if ([string]::IsNullOrWhiteSpace($clean)) {
        return $null
    }

    return $clean.ToLowerInvariant()
}

function Get-TidyNameFragments {
    param(
        [string] $DisplayName,
        [string] $PackageId
    )

    $fragments = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($word in ($DisplayName -split '[^A-Za-z0-9]+')) {
        if ([string]::IsNullOrWhiteSpace($word)) { continue }
        $trimmed = $word.Trim()
        if ($trimmed.Length -lt 4) { continue }
        [void]$fragments.Add($trimmed)
    }

    foreach ($segment in ($PackageId -split '[^A-Za-z0-9]+')) {
        if ([string]::IsNullOrWhiteSpace($segment)) { continue }
        $trimmed = $segment.Trim()
        if ($trimmed.Length -lt 4) { continue }
        [void]$fragments.Add($trimmed)
    }

    return ConvertTo-TidyStringArray -Source $fragments
}

function Get-TidySafePath {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    try {
        $expanded = [System.Environment]::ExpandEnvironmentVariables($Value)
        $full = [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($full)) {
        return $null
    }

    $normalized = $full.TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return $null
    }

    $root = [System.IO.Path]::GetPathRoot($normalized)
    if ($root -and [string]::Equals($normalized, $root.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    $protected = @(
        [System.Environment]::GetFolderPath('Windows'),
        [System.Environment]::GetFolderPath('ProgramFiles'),
        [System.Environment]::GetFolderPath('ProgramFilesX86'),
    [System.Environment]::GetFolderPath('CommonApplicationData'),
        [System.Environment]::GetFolderPath('System'),
        [System.Environment]::GetFolderPath('SystemX86'),
        [System.Environment]::GetFolderPath('Desktop')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($protectedPath in $protected) {
        if ([string]::Equals($normalized, $protectedPath.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
            return $null
        }
    }

    return $normalized
}

function Get-TidyPathFromCommand {
    param([string] $Command)

    if ([string]::IsNullOrWhiteSpace($Command)) {
        return $null
    }

    $expanded = [System.Environment]::ExpandEnvironmentVariables($Command.Trim())
    if ($expanded.Contains(',')) {
        $expanded = $expanded.Split(',')[0]
    }

    if ($expanded.StartsWith('"')) {
        $end = $expanded.IndexOf('"', 1)
        if ($end -gt 1) {
            return $expanded.Substring(1, $end - 1)
        }
    }

    $first = $expanded.Split(' ')[0]
    if ([string]::IsNullOrWhiteSpace($first)) {
        return $null
    }

    if ($first.StartsWith('"') -and $first.EndsWith('"')) {
        return $first.Trim('"')
    }

    return $first
}

function Get-TidyUninstallEntries {
    param(
        [string] $DisplayName,
        [string] $PackageId
    )

    $entries = [System.Collections.Generic.List[pscustomobject]]::new()
    $normalizedReference = Get-TidyNormalizedName -Value $DisplayName
    $normalizedId = if ([string]::IsNullOrWhiteSpace($PackageId)) { $null } else { $PackageId.Trim().ToLowerInvariant() }

    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }

        foreach ($key in Get-ChildItem -Path $root -ErrorAction SilentlyContinue) {
            try {
                $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction Stop
            }
            catch {
                continue
            }

            $displayNameProperty = $props.PSObject.Properties['DisplayName']
            if ($null -eq $displayNameProperty) {
                continue
            }

            $candidate = $displayNameProperty.Value
            if ([string]::IsNullOrWhiteSpace($candidate)) { continue }

            $normalizedCandidate = Get-TidyNormalizedName -Value $candidate
            $matches = $false

            if ($normalizedReference -and $normalizedCandidate -and $normalizedCandidate -eq $normalizedReference) {
                $matches = $true
            }
            elseif ($normalizedReference -and $normalizedCandidate -and $normalizedCandidate.Contains($normalizedReference)) {
                $matches = $true
            }
            elseif ($normalizedReference -and $normalizedCandidate -and $normalizedReference.Contains($normalizedCandidate)) {
                $matches = $true
            }
            elseif ($normalizedId -and $candidate.ToLowerInvariant().Contains($normalizedId)) {
                $matches = $true
            }
            elseif ($normalizedId -and $props.PSChildName -and $props.PSChildName.ToLowerInvariant().Contains($normalizedId)) {
                $matches = $true
            }

            if (-not $matches) {
                continue
            }

            $installLocation = $null
            $installLocationProperty = $props.PSObject.Properties['InstallLocation']
            if ($installLocationProperty) { $installLocation = $installLocationProperty.Value }

            $displayIcon = $null
            $displayIconProperty = $props.PSObject.Properties['DisplayIcon']
            if ($displayIconProperty) { $displayIcon = $displayIconProperty.Value }

            $uninstallString = $null
            $uninstallStringProperty = $props.PSObject.Properties['UninstallString']
            if ($uninstallStringProperty) { $uninstallString = $uninstallStringProperty.Value }

            $quietUninstallString = $null
            $quietUninstallStringProperty = $props.PSObject.Properties['QuietUninstallString']
            if ($quietUninstallStringProperty) { $quietUninstallString = $quietUninstallStringProperty.Value }

            $entries.Add([pscustomobject]@{
                Path = $key.PSPath
                DisplayName = $candidate
                InstallLocation = $installLocation
                DisplayIcon = $displayIcon
                UninstallString = $uninstallString
                QuietUninstallString = $quietUninstallString
            })
        }
    }

    return $entries
}

function Stop-TidyProcesses {
    param(
        [string[]] $CandidatePaths,
        [string[]] $Fragments
    )

    $stopped = 0
    $currentPid = [int]$PID
    $parentPid = $null

    try {
        $processes = Get-CimInstance Win32_Process -ErrorAction Stop

        foreach ($proc in $processes) {
            if ([int]$proc.ProcessId -eq $currentPid) {
                if ($proc.ParentProcessId -gt 0) {
                    $parentPid = [int]$proc.ParentProcessId
                }
                break
            }
        }
    }
    catch {
        Add-TidyError "Force cleanup: unable to enumerate processes: $($_.Exception.Message)"
        return 0
    }

    foreach ($process in $processes) {
        if ([int]$process.ProcessId -eq $currentPid) {
            continue
        }

        if ($parentPid -and [int]$process.ProcessId -eq $parentPid) {
            continue
        }

        # Avoid terminating the PowerShell host that is executing this script.
        $exe = $process.ExecutablePath
        $command = $process.CommandLine
        $matched = $false

        if ($CandidatePaths) {
            foreach ($path in $CandidatePaths) {
                if ([string]::IsNullOrWhiteSpace($path)) { continue }

                if (-not [string]::IsNullOrWhiteSpace($exe) -and $exe.StartsWith($path, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $matched = $true
                    break
                }

                if (-not $matched -and -not [string]::IsNullOrWhiteSpace($command) -and $command.IndexOf($path, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $matched = $true
                    break
                }
            }
        }

        if (-not $matched -and $Fragments) {
            foreach ($fragment in $Fragments) {
                if ([string]::IsNullOrWhiteSpace($fragment) -or $fragment.Length -lt 4) { continue }

                $matchesFragment = $false

                if (-not [string]::IsNullOrWhiteSpace($process.Name) -and $process.Name.IndexOf($fragment, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $matchesFragment = $true
                }
                elseif (-not [string]::IsNullOrWhiteSpace($exe) -and $exe.IndexOf($fragment, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $matchesFragment = $true
                }
                elseif (-not [string]::IsNullOrWhiteSpace($command) -and $command.IndexOf($fragment, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $matchesFragment = $true
                }

                if ($matchesFragment) {
                    $matched = $true
                    break
                }
            }
        }

        if (-not $matched) {
            continue
        }

        try {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
            Add-TidyOutput "Force cleanup: terminated process $($process.Name) (PID $($process.ProcessId))."
            $stopped++
        }
        catch {
            Add-TidyError "Force cleanup: failed to terminate process $($process.Name) (PID $($process.ProcessId)): $($_.Exception.Message)"
        }
    }

    return $stopped
}

function Remove-TidyDirectorySafe {
    param([string] $Path)

    $safePath = Get-TidySafePath -Value $Path
    if (-not $safePath) {
        return $false
    }

    if (-not (Test-Path -LiteralPath $safePath)) {
        return $false
    }

    try {
        Remove-Item -LiteralPath $safePath -Recurse -Force -ErrorAction Stop
        Add-TidyOutput "Force cleanup: removed directory '$safePath'."
        return $true
    }
    catch {
        Add-TidyError "Force cleanup: failed to remove directory '$safePath': $($_.Exception.Message)"
        return $false
    }
}

function Remove-TidyRegistryEntry {
    param([string] $Path, [string] $DisplayName)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        Add-TidyOutput "Force cleanup: removed uninstall registry entry '$DisplayName'."
        return $true
    }
    catch {
        Add-TidyError "Force cleanup: failed to remove uninstall registry entry '$DisplayName': $($_.Exception.Message)"
        return $false
    }
}

function Invoke-TidyForceCleanup {
    param(
        [string] $ManagerKey,
        [string] $PackageId,
        [string] $DisplayName
    )

    Add-TidyOutput "Force cleanup: attempting manual removal for '$DisplayName'."

    $entries = Get-TidyUninstallEntries -DisplayName $DisplayName -PackageId $PackageId
    $fragmentResults = Get-TidyNameFragments -DisplayName $DisplayName -PackageId $PackageId
    $fragments = ConvertTo-TidyStringArray -Source $fragmentResults

    if ((Get-TidyCollectionCount -Source $entries) -eq 0) {
        Add-TidyOutput "Force cleanup: no uninstall registry entries matched '$DisplayName'."
    }

    $candidateCollector = [System.Collections.Generic.List[string]]::new()

    foreach ($entry in $entries) {
        Add-TidyOutput "Force cleanup: inspecting uninstall entry '$($entry.DisplayName)'."

        foreach ($value in @($entry.InstallLocation, $entry.DisplayIcon, $entry.UninstallString, $entry.QuietUninstallString)) {
            if ([string]::IsNullOrWhiteSpace($value)) { continue }
            $path = Get-TidyPathFromCommand -Command $value
            if ([string]::IsNullOrWhiteSpace($path)) { continue }

            if ($path.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
                $parent = Split-Path -Parent $path
                if (-not [string]::IsNullOrWhiteSpace($parent)) {
                    $candidateCollector.Add($parent)
                }
            }
            else {
                $candidateCollector.Add($path)
            }
        }
    }

    if ((Get-TidyCollectionCount -Source $candidateCollector) -eq 0 -and $fragments.Length -gt 0) {
        $programRoots = @(
            [System.Environment]::GetFolderPath('ProgramFiles'),
            [System.Environment]::GetFolderPath('ProgramFilesX86')
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

        foreach ($root in $programRoots) {
            if (-not (Test-Path -LiteralPath $root)) { continue }

            foreach ($fragment in $fragments) {
                if ([string]::IsNullOrWhiteSpace($fragment) -or $fragment.Length -lt 4) { continue }

                try {
                    foreach ($match in Get-ChildItem -Path $root -Directory -Filter "*${fragment}*" -ErrorAction SilentlyContinue) {
                        $candidateCollector.Add($match.FullName)
                    }
                }
                catch {
                    continue
                }
            }
        }
    }

    $uniquePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $candidatePaths = [System.Collections.Generic.List[string]]::new()

    foreach ($candidate in $candidateCollector) {
        $safe = Get-TidySafePath -Value $candidate
        if (-not $safe) { continue }
        if ($uniquePaths.Add($safe)) {
            $candidatePaths.Add($safe)
        }
    }

    $candidatePathCount = Get-TidyCollectionCount -Source $candidatePaths

    if ($candidatePathCount -gt 0) {
        Add-TidyOutput "Force cleanup: queued $candidatePathCount candidate path(s) for removal."
    }

    $candidatePathArray = ConvertTo-TidyStringArray -Source $candidatePaths
    $fragmentArray = ConvertTo-TidyStringArray -Source $fragments

    $stopped = Stop-TidyProcesses -CandidatePaths $candidatePathArray -Fragments $fragmentArray

    $removedPaths = 0
    foreach ($path in ($candidatePathArray | Sort-Object -Property { $_.Length } -Descending)) {
        if (Remove-TidyDirectorySafe -Path $path) {
            $removedPaths++
        }
    }

    if ($fragmentArray.Length -gt 0) {
        $appDataRoots = @(
            [System.Environment]::GetFolderPath('LocalApplicationData'),
            [System.Environment]::GetFolderPath('ApplicationData'),
            [System.Environment]::GetFolderPath('CommonApplicationData')
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

        foreach ($root in $appDataRoots) {
            if (-not (Test-Path -LiteralPath $root)) { continue }

            foreach ($fragment in $fragmentArray) {
                if ([string]::IsNullOrWhiteSpace($fragment) -or $fragment.Length -lt 4) { continue }

                try {
                    foreach ($match in Get-ChildItem -Path $root -Directory -Filter "*${fragment}*" -ErrorAction SilentlyContinue) {
                        if (Remove-TidyDirectorySafe -Path $match.FullName) {
                            $removedPaths++
                        }
                    }
                }
                catch {
                    continue
                }
            }
        }
    }

    $removedRegistry = 0
    foreach ($entry in $entries) {
        if (Remove-TidyRegistryEntry -Path $entry.Path -DisplayName $entry.DisplayName) {
            $removedRegistry++
        }
    }

    $installed = Get-TidyInstalledPackageVersion -Manager $ManagerKey -PackageId $PackageId
    $success = [string]::IsNullOrWhiteSpace($installed)

    if ($success) {
        if ($removedPaths -gt 0 -or $removedRegistry -gt 0 -or $stopped -gt 0) {
            $summary = "Force cleanup completed for '$DisplayName'."
        }
        else {
            $summary = "Force cleanup found no remaining artifacts for '$DisplayName'."
        }
    }
    else {
        $summary = "Force cleanup attempted, but '$DisplayName' still reports as installed. Manual intervention may be required."
    }

    return [pscustomobject]@{
        Attempted          = $true
        Success            = $success
        Summary            = $summary
        ExitCode           = if ($success) { 0 } else { -1 }
        ProcessesTerminated = $stopped
        RemovedPaths       = $removedPaths
        RemovedRegistry    = $removedRegistry
    }
}

$installedBefore = Get-TidyInstalledPackageVersion -Manager $managerKey -PackageId $PackageId
$statusBefore = if ([string]::IsNullOrWhiteSpace($installedBefore)) { 'NotInstalled' } else { 'Installed' }
$attempted = $false
$exitCode = 0
$operationSucceeded = $false
$summary = $null
$forceCleanupInvoked = $false

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

if ($ForceCleanup.IsPresent -and -not $DryRun.IsPresent)
{
    $forceCleanupInvoked = $true
    $forceResult = Invoke-TidyForceCleanup -ManagerKey $managerKey -PackageId $PackageId -DisplayName $DisplayName
    if ($forceResult.Attempted) {
        $attempted = $true
    }

    if (-not $operationSucceeded) {
        $operationSucceeded = [bool]$forceResult.Success
        if ($operationSucceeded) {
            $exitCode = 0
        }
        elseif ($exitCode -eq 0) {
            $exitCode = $forceResult.ExitCode
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($forceResult.Summary)) {
        $summary = $forceResult.Summary
    }
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
    operation        = if ($forceCleanupInvoked) { 'force-remove' } else { 'remove' }
    manager          = $normalizedManager
    packageId        = $PackageId
    displayName      = $DisplayName
    requiresAdmin    = $needsElevation
    statusBefore     = $statusBefore
    statusAfter      = $statusAfter
    installedVersion = if ([string]::IsNullOrWhiteSpace($installedAfter)) { $null } else { $installedAfter }
    succeeded        = [bool]$operationSucceeded
    attempted        = [bool]$attempted
    exitCode         = [int]$exitCode
    summary          = $summary
    output           = $script:TidyOutput
    errors           = $script:TidyErrors
}

try {
    Save-TidyResult
}
finally {
    $script:ResultPayload | ConvertTo-Json -Depth 6 -Compress
}
