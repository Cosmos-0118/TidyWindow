param(
    [int] $Top = 12,
    [switch] $IncludeDisabled,
    [switch] $IncludeServices,
    [switch] $IncludeScheduledTasks,
    [switch] $IncludeBootTimeline,
    [string] $ExportPath,
    [switch] $ExportAsJson,
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
$script:CollectedRecords = [System.Collections.Generic.List[pscustomobject]]::new()
$script:RecordKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$script:StateCounts = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::OrdinalIgnoreCase)
$script:CategoryCounts = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::OrdinalIgnoreCase)
$script:SignatureCounts = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::OrdinalIgnoreCase)
$script:Observations = [System.Collections.Generic.List[string]]::new()
$script:ExecutionStart = Get-Date
$script:SummaryEmitted = $false
$script:BootEventsCollected = 0
$script:ExportSucceeded = $false
$script:ExportFormat = $null
$script:DisabledEntriesSkipped = 0
$script:NonRunningAutoServices = [System.Collections.Generic.List[string]]::new()

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

$TopClamp = 50
$originalTop = $Top
if ($Top -lt 1 -or $Top -gt $TopClamp) {
    Register-TidyObservation ("Requested Top value {0} adjusted to range 1-{1}." -f $Top, $TopClamp)
    $Top = [Math]::Max(1, [Math]::Min($Top, $TopClamp))
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

function Increment-TidyCounter {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[string, int]] $Map,
        [Parameter(Mandatory = $true)]
        [string] $Key
    )

    if ([string]::IsNullOrWhiteSpace($Key)) {
        $Key = 'Unknown'
    }

    if ($Map.ContainsKey($Key)) {
        $Map[$Key]++
    }
    else {
        $Map[$Key] = 1
    }
}

function Register-TidyObservation {
    param([string] $Message)

    if ([string]::IsNullOrWhiteSpace($Message)) {
        return
    }

    if (-not $script:Observations.Contains($Message)) {
        $script:Observations.Add($Message)
    }
}

function Write-TidyStartupSummary {
    if ($script:SummaryEmitted) {
        return
    }

    $script:SummaryEmitted = $true

    $elapsed = (Get-Date) - $script:ExecutionStart
    Write-TidyOutput -Message '--- Startup insight summary ---'
    Write-TidyOutput -Message ("Total startup entries: {0}" -f $script:CollectedRecords.Count)
    Write-TidyOutput -Message ("Elapsed time: {0:g}" -f $elapsed)

    if ($script:CategoryCounts.Count -gt 0) {
        Write-TidyOutput -Message 'Entries by category:'
        foreach ($pair in $script:CategoryCounts.GetEnumerator() | Sort-Object -Property Key) {
            Write-TidyOutput -Message ("  ↳ {0}: {1}" -f $pair.Key, $pair.Value)
        }
    }

    if ($script:StateCounts.Count -gt 0) {
        Write-TidyOutput -Message 'Entries by state:'
        foreach ($pair in $script:StateCounts.GetEnumerator() | Sort-Object -Property Key) {
            Write-TidyOutput -Message ("  ↳ {0}: {1}" -f $pair.Key, $pair.Value)
        }
    }

    if ($script:DisabledEntriesSkipped -gt 0) {
        Write-TidyOutput -Message ("Disabled entries skipped (use -IncludeDisabled to review): {0}" -f $script:DisabledEntriesSkipped)
    }

    if ($script:SignatureCounts.Count -gt 0) {
        Write-TidyOutput -Message 'File signature health:'
        foreach ($pair in $script:SignatureCounts.GetEnumerator() | Sort-Object -Property Key) {
            Write-TidyOutput -Message ("  ↳ {0}: {1}" -f $pair.Key, $pair.Value)
        }
    }

    if ($script:BootEventsCollected -gt 0) {
        Write-TidyOutput -Message ("Boot timeline events captured: {0}" -f $script:BootEventsCollected)
    }

    if ($script:ExportSucceeded) {
        Write-TidyOutput -Message ("Exported manifest format: {0}" -f $script:ExportFormat)
    }

    if ($script:Observations.Count -gt 0) {
        Write-TidyOutput -Message 'Observations:'
        foreach ($note in $script:Observations) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $note)
        }
    }
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

function Get-StartupApprovalTable {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RootPath
    )

    $table = @{}
    try {
        $key = Get-Item -LiteralPath $RootPath -ErrorAction Stop
        foreach ($property in $key.GetValueNames()) {
            $value = $key.GetValue($property, $null)
            if ($null -eq $value -or $value.Length -eq 0) {
                continue
            }

            $state = switch ($value[0]) {
                0x02 { 'Enabled' }
                0x03 { 'Disabled' }
                0x07 { 'DisabledByPolicy' }
                0x00 { 'Unknown' }
                default { 'Unknown' }
            }

            $table[$property] = $state
        }
    }
    catch {
        # StartupApproved key missing is acceptable.
    }

    return $table
}

function Resolve-ExecutablePath {
    param([string] $CommandLine)

    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return $null
    }

    $trimmed = $CommandLine.Trim()
    if ($trimmed.StartsWith('"')) {
        $closing = $trimmed.IndexOf('"', 1)
        if ($closing -gt 1) {
            $candidate = $trimmed.Substring(1, $closing - 1)
            if (Test-Path -LiteralPath $candidate) {
                return [System.IO.Path]::GetFullPath($candidate)
            }
        }
    }

    $firstToken = $trimmed.Split(' ')[0]
    $clean = $firstToken.Trim('"')
    if (Test-Path -LiteralPath $clean) {
        return [System.IO.Path]::GetFullPath($clean)
    }

    $lookup = Get-Command -Name $clean -ErrorAction SilentlyContinue
    if ($lookup -and -not [string]::IsNullOrWhiteSpace($lookup.Path)) {
        return $lookup.Path
    }

    return $null
}

function Enrich-StartupRecord {
    param([pscustomobject] $Record)

    $path = $Record.ExecutablePath
    $company = $null
    $product = $null
    $fileVersion = $null
    $fileSize = $null
    $lastWrite = $null
    $signatureStatus = 'Unknown'

    if (-not [string]::IsNullOrWhiteSpace($path)) {
        try {
            $item = Get-Item -LiteralPath $path -ErrorAction Stop
            $signature = Get-AuthenticodeSignature -FilePath $path -ErrorAction SilentlyContinue

            $company = $item.VersionInfo.CompanyName
            $product = $item.VersionInfo.ProductName
            $fileVersion = $item.VersionInfo.FileVersion
            $fileSize = [Math]::Round($item.Length / 1MB, 2)
            $lastWrite = $item.LastWriteTime
            if ($null -ne $signature) {
                $signatureStatus = $signature.Status
            }
        }
        catch {
            # Leave defaults when metadata cannot be resolved.
        }
    }

    return $Record | Select-Object *,
        @{ Name = 'Company'; Expression = { $company } },
        @{ Name = 'Product'; Expression = { $product } },
        @{ Name = 'FileVersion'; Expression = { $fileVersion } },
        @{ Name = 'FileSizeMB'; Expression = { $fileSize } },
        @{ Name = 'LastWriteTime'; Expression = { $lastWrite } },
        @{ Name = 'SignatureStatus'; Expression = { $signatureStatus } }
}

function Add-StartupRecord {
    param(
        [string] $Name,
        [string] $Command,
        [string] $Source,
        [string] $Category,
        [string] $UserContext,
        [string] $State
    )

    $executable = Resolve-ExecutablePath -CommandLine $Command
    $record = [pscustomobject]@{
        Name           = $Name
        Command        = $Command
        Source         = $Source
        Category       = $Category
        UserContext    = $UserContext
        State          = if ($State) { $State } else { 'Unknown' }
        ExecutablePath = $executable
    }

    $enriched = Enrich-StartupRecord -Record $record
    $fingerprint = '{0}|{1}|{2}|{3}' -f ($Category ?? 'Unknown'), ($Source ?? 'Unknown'), ($Name ?? 'Unknown'), ($UserContext ?? 'Unknown')
    if (-not $script:RecordKeys.Add($fingerprint)) {
        Register-TidyObservation ("Skipped duplicate startup entry: {0} ({1})" -f $Name, $Source)
        return
    }

    $script:CollectedRecords.Add($enriched) | Out-Null
    Increment-TidyCounter -Map $script:CategoryCounts -Key $Category
    Increment-TidyCounter -Map $script:StateCounts -Key $enriched.State
    Increment-TidyCounter -Map $script:SignatureCounts -Key ($enriched.SignatureStatus ?? 'Unknown')
}

function Collect-RegistryStartupEntries {
    $roots = @(
        @{ Path = 'Registry::HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run'; Category = 'Registry'; Context = 'CurrentUser'; Approval = 'Registry::HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run' },
        @{ Path = 'Registry::HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce'; Category = 'Registry'; Context = 'CurrentUser'; Approval = 'Registry::HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\RunOnce' },
        @{ Path = 'Registry::HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run'; Category = 'Registry'; Context = 'LocalMachine'; Approval = 'Registry::HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run' },
        @{ Path = 'Registry::HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce'; Category = 'Registry'; Context = 'LocalMachine'; Approval = 'Registry::HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\RunOnce' },
        @{ Path = 'Registry::HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run'; Category = 'Registry'; Context = 'LocalMachine (x86)'; Approval = 'Registry::HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run' }
    )

    foreach ($root in $roots) {
        try {
            $key = Get-Item -LiteralPath $root.Path -ErrorAction Stop
            $approval = Get-StartupApprovalTable -RootPath $root.Approval
            foreach ($valueName in $key.GetValueNames()) {
                $value = $key.GetValue($valueName, $null)
                if ([string]::IsNullOrWhiteSpace($value)) {
                    continue
                }

                $state = if ($approval.ContainsKey($valueName)) { $approval[$valueName] } else { 'Unknown' }
                if (-not $IncludeDisabled.IsPresent -and $state -match 'Disabled') {
                    $script:DisabledEntriesSkipped++
                    continue
                }

                Add-StartupRecord -Name $valueName -Command ([string]$value) -Source $root.Path -Category $root.Category -UserContext $root.Context -State $state
            }
        }
        catch {
            continue
        }
    }
}

function Collect-WmiStartupEntries {
    try {
        $entries = Get-CimInstance -ClassName Win32_StartupCommand -ErrorAction Stop
        foreach ($entry in @($entries)) {
            $state = if ($IncludeDisabled.IsPresent) { 'Unknown' } else { 'Enabled' }
            Add-StartupRecord -Name $entry.Name -Command $entry.Command -Source $entry.Location -Category 'WMI' -UserContext $entry.User -State $state
        }
        if (-not $entries) {
            Register-TidyObservation 'Win32_StartupCommand returned no entries.'
        }
    }
    catch {
        Write-TidyError -Message ("Failed to query Win32_StartupCommand. {0}" -f $_.Exception.Message)
        Register-TidyObservation 'Win32_StartupCommand query failed; ensure WMI service is healthy.'
    }
}

function Collect-ScheduledTaskEntries {
    if (-not $IncludeScheduledTasks.IsPresent) {
        return
    }

    try {
        $tasks = Get-ScheduledTask | Where-Object {
            $_.TaskPath -notlike '\\Microsoft\\Windows\\Windows Defender*' -and
            ($_.Triggers | Where-Object { $_.TriggerType -in ('AtStartup', 'AtLogon') })
        }

        foreach ($task in @($tasks)) {
            $triggerKinds = $task.Triggers | ForEach-Object { $_.TriggerType }
            $state = if ($task.State -eq 'Disabled') { 'Disabled' } else { 'Enabled' }
            if (-not $IncludeDisabled.IsPresent -and $state -eq 'Disabled') {
                $script:DisabledEntriesSkipped++
                continue
            }

            $action = $null
            if ($task.Actions) {
                $action = $task.Actions | Select-Object -First 1
            }

            $command = $null
            if ($action) {
                if (-not [string]::IsNullOrWhiteSpace($action.Arguments)) {
                    $command = "{0} {1}" -f $action.Execute, $action.Arguments
                }
                else {
                    $command = $action.Execute
                }
            }

            $userContext = if (-not [string]::IsNullOrWhiteSpace($task.Principal.UserId)) {
                $task.Principal.UserId
            }
            elseif ($task.Principal.LogonType) {
                [string]$task.Principal.LogonType
            }
            else {
                'System'
            }

            Add-StartupRecord -Name $task.TaskName -Command $command -Source ("ScheduledTask:{0}" -f ($task.TaskPath.Trim('\'))) -Category 'ScheduledTask' -UserContext $userContext -State $state
        }

        if (-not $tasks) {
            Register-TidyObservation 'No startup-scheduled tasks detected.'
        }
    }
    catch {
        Write-TidyError -Message ("Failed to enumerate scheduled tasks. {0}" -f $_.Exception.Message)
        Register-TidyObservation 'Scheduled task enumeration failed; Task Scheduler service may be unavailable.'
    }
}

function Collect-ServiceEntries {
    if (-not $IncludeServices.IsPresent) {
        return
    }

    try {
        $services = Get-CimInstance -ClassName Win32_Service -Filter "StartMode='Auto'" -ErrorAction Stop | Where-Object { $_.DelayedAutoStart -ne $true }
        foreach ($service in @($services)) {
            $state = switch ($service.State) {
                'Running' { 'Enabled'; break }
                'Stopped' { 'Stopped'; break }
                'Stop Pending' { 'Stopping'; break }
                'Paused' { 'Paused'; break }
                'Start Pending' { 'Starting'; break }
                default { 'Unknown' }
            }
            if ($state -ne 'Enabled') {
                $script:NonRunningAutoServices.Add($service.Name) | Out-Null
            }
            $command = if ($service.PathName) { $service.PathName } else { $service.Name }
            Add-StartupRecord -Name $service.Name -Command $command -Source 'ServiceControlManager' -Category 'Service' -UserContext $service.StartName -State $state
        }

        if (-not $services) {
            Register-TidyObservation 'No automatic services (non-delayed) were detected.'
        }
        elseif ($script:NonRunningAutoServices.Count -gt 0) {
            $preview = ($script:NonRunningAutoServices | Select-Object -First 5) -join ', '
            $more = if ($script:NonRunningAutoServices.Count -gt 5) { ' and others' } else { '' }
            Register-TidyObservation ("Detected {0} auto-start services not running: {1}{2}" -f $script:NonRunningAutoServices.Count, $preview, $more)
        }
    }
    catch {
        Write-TidyError -Message ("Failed to gather auto-start services. {0}" -f $_.Exception.Message)
        Register-TidyObservation 'Service enumeration failed; WMI or SCM access may be restricted.'
    }
}

function Output-StartupSummary {
    param([int] $Count)

    if ($script:CollectedRecords.Count -eq 0) {
        Write-TidyOutput -Message 'No startup entries discovered.'
        return
    }

    $topEntries = $script:CollectedRecords
    if (-not $IncludeDisabled.IsPresent) {
        $topEntries = $topEntries | Where-Object { $_.State -ne 'Disabled' }
    }

    $topEntries = $topEntries | Sort-Object -Property @{ Expression = { if ($_.FileSizeMB) { $_.FileSizeMB } else { 0 } } ; Descending = $true }, LastWriteTime -Descending | Select-Object -First $Count

    Write-TidyOutput -Message ("Top {0} impact candidates:" -f $Count)
    foreach ($entry in $topEntries) {
        $sizeText = if ($entry.FileSizeMB) { "{0} MB" -f [Math]::Round($entry.FileSizeMB, 2) } else { 'Size N/A' }
        $pathDisplay = if (-not [string]::IsNullOrWhiteSpace($entry.ExecutablePath)) { $entry.ExecutablePath } else { $entry.Command }
        Write-TidyOutput -Message ("  • {0} — {1} ({2})" -f $entry.Name, $pathDisplay, $sizeText)
    }
}

function Output-BootTimeline {
    if (-not $IncludeBootTimeline.IsPresent) {
        return
    }

    Write-TidyOutput -Message 'Collecting recent boot performance events (Event 100/200).'
    try {
        $bootEvents = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Diagnostics-Performance/Operational'; Id = 100 } -MaxEvents 5 -ErrorAction Stop
        foreach ($evt in $bootEvents) {
            $duration = $evt.Properties[1].Value
            $mainPath = $evt.Properties[6].Value
            Write-TidyOutput -Message ("Boot at {0:g} — {1} ms total — MainPath: {2} ms" -f $evt.TimeCreated, $duration, $mainPath)
            $script:BootEventsCollected++

            if ([int]$duration -gt 100000) {
                Register-TidyObservation ("Boot on {0:g} exceeded 100 seconds (total {1} ms)." -f $evt.TimeCreated, $duration)
            }
        }
    }
    catch {
        Write-TidyError -Message ("Failed to read boot timeline events. {0}" -f $_.Exception.Message)
        Register-TidyObservation 'Boot timeline unavailable; diagnostics-performance log could not be queried.'
    }
}

try {
    Write-TidyLog -Level Information -Message 'Starting startup impact analyzer.'

    Collect-RegistryStartupEntries
    Collect-WmiStartupEntries
    Collect-ScheduledTaskEntries
    Collect-ServiceEntries

    Output-StartupSummary -Count ([Math]::Max(1, $Top))
    Output-BootTimeline

    if ($ExportPath) {
        $resolvedExport = [System.IO.Path]::GetFullPath($ExportPath)
        $directory = Split-Path -Parent $resolvedExport
        if (-not (Test-Path -LiteralPath $directory)) {
            [void](New-Item -ItemType Directory -Path $directory -Force)
        }

        if ($ExportAsJson.IsPresent) {
            $json = $script:CollectedRecords | ConvertTo-Json -Depth 4
            Set-Content -Path $resolvedExport -Value $json -Encoding UTF8
            Write-TidyOutput -Message ("Exported startup manifest to {0} (JSON)." -f $resolvedExport)
        }
        else {
            $script:CollectedRecords | Export-Csv -Path $resolvedExport -NoTypeInformation -Force
            Write-TidyOutput -Message ("Exported startup manifest to {0} (CSV)." -f $resolvedExport)
        }
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
    Write-TidyLog -Level Information -Message 'Startup impact analyzer finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

