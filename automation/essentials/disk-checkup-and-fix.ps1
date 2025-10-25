param(
    [string] $Volume = 'C:',
    [switch] $ScanOnly,
    [switch] $PerformRepair,
    [switch] $IncludeSurfaceScan,
    [switch] $ScheduleIfBusy,
    [switch] $SkipSmart,
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

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = @($output)
    }
}

function Test-TidyAdmin {
    return [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-VolumePath {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw 'Volume parameter cannot be empty.'
    }

    $trimmed = $Value.Trim()
    if ($trimmed.Length -eq 1) {
        $trimmed = "${trimmed}:"
    }

    if ($trimmed.Length -eq 2 -and $trimmed[1] -eq ':') {
        return $trimmed.ToUpperInvariant()
    }

    try {
        $resolved = (Get-Item -LiteralPath $trimmed -ErrorAction Stop).FullName
        if ($resolved.Length -ge 2 -and $resolved[1] -eq ':') {
            return $resolved.Substring(0, 2).ToUpperInvariant()
        }
    }
    catch {
        # Fall through to manual parsing.
    }

    if ($trimmed.StartsWith('\\?\', [System.StringComparison]::OrdinalIgnoreCase)) {
        $trimmed = $trimmed.Substring(4)
    }

    if ($trimmed.Length -ge 2 -and $trimmed[1] -eq ':') {
        return $trimmed.Substring(0, 2).ToUpperInvariant()
    }

    throw "Unable to resolve volume from input '$Value'."
}

function Get-TidyDeviceKey {
    param([object] $Value)

    if ($null -eq $Value) {
        return $null
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    $normalized = ($text.ToUpperInvariant() -replace '[^A-Z0-9]', '')
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return $null
    }

    return $normalized
}

function Get-TidyPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [object] $InputObject,
        [Parameter(Mandatory = $true)]
        [string] $PropertyName
    )

    if ($null -eq $InputObject) {
        return $null
    }

    $psObject = $InputObject.PSObject
    if ($null -eq $psObject) {
        return $null
    }

    $property = $psObject.Properties[$PropertyName]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Convert-TidyToStringArray {
    param([object] $Value)

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [string]) {
        return @($Value)
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        $buffer = @()
        foreach ($item in $Value) {
            if ($null -eq $item) {
                continue
            }

            $buffer += [string]$item
        }

        return $buffer
    }

    return @([string]$Value)
}

function Convert-TidyToNullableBool {
    param([object] $Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [bool]) {
        return $Value
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    $parsed = $false
    if ([bool]::TryParse($text, [ref]$parsed)) {
        return $parsed
    }

    $trimmed = $text.Trim()
    switch ($trimmed) {
        '0' { return $false }
        '1' { return $true }
    }

    return $null
}

function Get-TidyInsightKeyCandidates {
    param(
        [Nullable[int]] $DiskNumber,
        [string] $SerialNumber,
        [string] $FriendlyName,
        [string] $Model
    )

    $candidates = [System.Collections.Generic.List[string]]::new()

    if ($DiskNumber -ne $null) {
        $candidates.Add("NUM:$DiskNumber")
    }

    $serialKey = Get-TidyDeviceKey $SerialNumber
    if (-not [string]::IsNullOrWhiteSpace($serialKey)) {
        $candidates.Add("SER:$serialKey")
    }

    if (-not [string]::IsNullOrWhiteSpace($FriendlyName)) {
        $candidates.Add("NAM:" + ($FriendlyName.ToUpperInvariant() -replace '\s+', ''))
    }

    if (-not [string]::IsNullOrWhiteSpace($Model)) {
        $candidates.Add("MOD:" + ($Model.ToUpperInvariant() -replace '\s+', ''))
    }

    if ($candidates.Count -eq 0) {
        $candidates.Add('UNK:' + [Guid]::NewGuid().ToString('N'))
    }

    return $candidates
}

function Merge-TidyDiskInsight {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Target,
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Source
    )

    if ($null -eq $Target.DiskNumber -and $null -ne $Source.DiskNumber) {
        $Target.DiskNumber = $Source.DiskNumber
    }

    if ([string]::IsNullOrWhiteSpace($Target.FriendlyName) -and -not [string]::IsNullOrWhiteSpace($Source.FriendlyName)) {
        $Target.FriendlyName = $Source.FriendlyName
    }

    if ([string]::IsNullOrWhiteSpace($Target.Model) -and -not [string]::IsNullOrWhiteSpace($Source.Model)) {
        $Target.Model = $Source.Model
    }

    if ([string]::IsNullOrWhiteSpace($Target.SerialNumber) -and -not [string]::IsNullOrWhiteSpace($Source.SerialNumber)) {
        $Target.SerialNumber = $Source.SerialNumber
    }

    if ($null -eq $Target.SizeBytes -and $null -ne $Source.SizeBytes -and $Source.SizeBytes -gt 0) {
        $Target.SizeBytes = $Source.SizeBytes
    }

    if ($Target.PredictFailure -ne $true) {
        if ($Source.PredictFailure -eq $true) {
            $Target.PredictFailure = $true
        }
        elseif ($Target.PredictFailure -eq $null -and $Source.PredictFailure -ne $null) {
            $Target.PredictFailure = $Source.PredictFailure
        }
    }

    if ([string]::IsNullOrWhiteSpace($Target.HealthStatus) -and -not [string]::IsNullOrWhiteSpace($Source.HealthStatus)) {
        $Target.HealthStatus = $Source.HealthStatus
    }

    $operational = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($state in @($Target.OperationalStatus)) {
        if (-not [string]::IsNullOrWhiteSpace($state)) {
            [void]$operational.Add($state)
        }
    }
    foreach ($state in @($Source.OperationalStatus)) {
        if (-not [string]::IsNullOrWhiteSpace($state)) {
            [void]$operational.Add($state)
        }
    }
    $Target.OperationalStatus = [System.Linq.Enumerable]::ToArray($operational)

    $notes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($note in @($Target.Notes)) {
        if (-not [string]::IsNullOrWhiteSpace($note)) {
            [void]$notes.Add($note)
        }
    }
    foreach ($note in @($Source.Notes)) {
        if (-not [string]::IsNullOrWhiteSpace($note)) {
            [void]$notes.Add($note)
        }
    }
    $Target.Notes = [System.Linq.Enumerable]::ToArray($notes)

    if (-not $Target.IsTargetVolume -and $Source.IsTargetVolume) {
        $Target.IsTargetVolume = $true
    }

    return $Target
}
function Get-SmartStatus {
    param(
        [int[]] $TargetDiskNumbers
    )

    $entries = [System.Collections.Generic.Dictionary[string, pscustomobject]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $diskKeyByNumber = [System.Collections.Generic.Dictionary[int, string]]::new()

    function Convert-TidyDeviceLabel {
        param([string] $Value)

        if ([string]::IsNullOrWhiteSpace($Value)) {
            return 'Unknown device'
        }

        $normalized = ($Value -replace '\s+', ' ').Trim()
        if ([string]::IsNullOrWhiteSpace($normalized)) {
            return 'Unknown device'
        }

        return $normalized
    }

    function Ensure-TidyDiskInsight {
        param(
            [Parameter(Mandatory = $true)]
            [System.Collections.Generic.Dictionary[string, pscustomobject]] $Map,
            [Parameter(Mandatory = $true)]
            [System.Collections.Generic.Dictionary[int, string]] $IndexMap,
            [string] $Key,
            [Nullable[int]] $DiskNumber
        )

        $safeKey = $Key
        if ([string]::IsNullOrWhiteSpace($safeKey) -and $DiskNumber -ne $null -and $IndexMap.ContainsKey([int]$DiskNumber)) {
            $safeKey = $IndexMap[[int]$DiskNumber]
        }

        if ([string]::IsNullOrWhiteSpace($safeKey)) {
            $safeKey = [Guid]::NewGuid().ToString('N')
        }

        if (-not $Map.ContainsKey($safeKey)) {
            $Map[$safeKey] = [pscustomobject]@{
                DiskNumber        = $null
                FriendlyName      = $null
                Model             = $null
                SerialNumber      = $null
                SizeBytes         = $null
                PredictFailure    = $null
                HealthStatus      = $null
                OperationalStatus = [System.Collections.Generic.List[string]]::new()
                Notes             = [System.Collections.Generic.List[string]]::new()
                IsTargetVolume    = $false
            }
        }

        $entry = $Map[$safeKey]

        if ($DiskNumber -ne $null) {
            $diskNumberValue = [int]$DiskNumber
            if ($null -eq $entry.DiskNumber) {
                $entry.DiskNumber = $diskNumberValue
            }

            $IndexMap[$diskNumberValue] = $safeKey
        }

        return $entry
    }

    if ($null -eq $TargetDiskNumbers) {
        $TargetDiskNumbers = @()
    }

    $win32Disks = @()
    if (Get-Command -Name Get-CimInstance -ErrorAction SilentlyContinue) {
        try {
            $win32Disks = Get-CimInstance -ClassName Win32_DiskDrive -ErrorAction Stop
        }
        catch {
            Write-TidyOutput -Message ("Win32_DiskDrive telemetry unavailable ({0})." -f $_.Exception.Message)
        }
    }
    elseif (Get-Command -Name Get-WmiObject -ErrorAction SilentlyContinue) {
        try {
            $win32Disks = Get-WmiObject -Class Win32_DiskDrive -ErrorAction Stop
        }
        catch {
            Write-TidyOutput -Message ("Win32_DiskDrive telemetry unavailable ({0})." -f $_.Exception.Message)
        }
    }

    foreach ($disk in @($win32Disks)) {
        if ($null -eq $disk) {
            continue
        }

        $diskNumber = $null
        if ($disk.PSObject.Properties['Index']) {
            try {
                $diskNumber = [int]$disk.Index
            }
            catch {
                $diskNumber = $null
            }
        }

        if ($diskNumber -eq $null -and $disk.PSObject.Properties['DeviceID']) {
            $idMatch = [System.Text.RegularExpressions.Regex]::Match([string]$disk.DeviceID, 'PHYSICALDRIVE(?<num>\d+)$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($idMatch.Success) {
                try {
                    $diskNumber = [int]$idMatch.Groups['num'].Value
                }
                catch {
                    $diskNumber = $null
                }
            }
        }

        $key = Get-TidyDeviceKey $disk.PNPDeviceID
        if (-not $key) {
            $key = Get-TidyDeviceKey $disk.DeviceID
        }
        if (-not $key) {
            $key = Get-TidyDeviceKey $disk.SerialNumber
        }

        $entry = Ensure-TidyDiskInsight -Map $entries -IndexMap $diskKeyByNumber -Key $key -DiskNumber $diskNumber
        if (-not [string]::IsNullOrWhiteSpace($disk.Model)) {
            $entry.Model = $disk.Model
        }
        if (-not [string]::IsNullOrWhiteSpace($disk.SerialNumber)) {
            $entry.SerialNumber = $disk.SerialNumber
        }
        if ($disk.Size -gt 0) {
            $entry.SizeBytes = [int64]$disk.Size
        }
        if (-not [string]::IsNullOrWhiteSpace($entry.FriendlyName)) {
            continue
        }

        $entry.FriendlyName = Convert-TidyDeviceLabel $disk.Model
    }

    $healthMap = @{
        0 = 'Unknown'
        1 = 'Healthy'
        2 = 'Warning'
        3 = 'Unhealthy'
        4 = 'Critical'
    }

    $operationalMap = @{
        0  = 'Unknown'
        1  = 'Other'
        2  = 'OK'
        3  = 'Degraded'
        4  = 'Stressed'
        5  = 'Predictive Failure'
        6  = 'Error'
        7  = 'Non-Recoverable Error'
        8  = 'Starting'
        9  = 'Stopping'
        10 = 'Stopped'
        11 = 'In Service'
        12 = 'No Contact'
        13 = 'Lost Communication'
        14 = 'Aborted'
        15 = 'Dormant'
        16 = 'Supporting Entity In Error'
        17 = 'Completed'
        18 = 'Power Mode'
        19 = 'Relocating'
    }

    $detailProvider = $null
    if (Get-Command -Name Get-CimInstance -ErrorAction SilentlyContinue) {
        $detailProvider = { Get-CimInstance -Namespace 'root/microsoft/windows/storage' -ClassName 'MSFT_PhysicalDisk' -ErrorAction Stop }
    }
    elseif (Get-Command -Name Get-WmiObject -ErrorAction SilentlyContinue) {
        $detailProvider = { Get-WmiObject -Namespace 'root/microsoft/windows/storage' -Class 'MSFT_PhysicalDisk' -ErrorAction Stop }
    }

    if ($null -ne $detailProvider) {
        try {
            $detailEntries = & $detailProvider
            foreach ($detail in @($detailEntries)) {
                if ($null -eq $detail) {
                    continue
                }

                $detailDiskNumber = $null
                if ($detail.PSObject.Properties['DeviceId']) {
                    try {
                        $detailDiskNumber = [int]$detail.DeviceId
                    }
                    catch {
                        $detailDiskNumber = $null
                    }
                }

                $key = $null
                if ($detail.PSObject.Properties['SerialNumber'] -and -not [string]::IsNullOrWhiteSpace([string]$detail.SerialNumber)) {
                    $key = Get-TidyDeviceKey $detail.SerialNumber
                }
                if (-not $key -and $detail.PSObject.Properties['DeviceId']) {
                    $key = Get-TidyDeviceKey $detail.DeviceId
                }
                if (-not $key -and $detail.PSObject.Properties['FriendlyName']) {
                    $key = Get-TidyDeviceKey $detail.FriendlyName
                }

                $entry = Ensure-TidyDiskInsight -Map $entries -IndexMap $diskKeyByNumber -Key $key -DiskNumber $detailDiskNumber

                if ($detail.PSObject.Properties['FriendlyName'] -and -not [string]::IsNullOrWhiteSpace([string]$detail.FriendlyName)) {
                    $entry.FriendlyName = $detail.FriendlyName
                }

                if ($detail.PSObject.Properties['SerialNumber'] -and -not [string]::IsNullOrWhiteSpace([string]$detail.SerialNumber)) {
                    $entry.SerialNumber = $detail.SerialNumber
                }

                if ($detail.PSObject.Properties['Size'] -and $detail.Size -gt 0) {
                    $entry.SizeBytes = [int64]$detail.Size
                }

                if ($detail.PSObject.Properties['HealthStatus']) {
                    $statusValue = [int]$detail.HealthStatus
                    if ($healthMap.ContainsKey($statusValue)) {
                        $entry.HealthStatus = $healthMap[$statusValue]
                    }
                    else {
                        $entry.HealthStatus = $detail.HealthStatus.ToString()
                    }
                }

                if ($detail.PSObject.Properties['OperationalStatus']) {
                    foreach ($statusValue in @($detail.OperationalStatus)) {
                        if ($null -eq $statusValue) {
                            continue
                        }

                        $text = $null
                        if ($statusValue -is [int]) {
                            if ($operationalMap.ContainsKey($statusValue)) {
                                $text = $operationalMap[$statusValue]
                            }
                            else {
                                $text = $statusValue.ToString()
                            }
                        }
                        else {
                            $text = $statusValue.ToString()
                        }

                        if ([string]::IsNullOrWhiteSpace($text)) {
                            continue
                        }

                        if (-not $entry.OperationalStatus.Contains($text)) {
                            $entry.OperationalStatus.Add($text)
                        }
                    }
                }

                if ($detail.PSObject.Properties['HealthStatus'] -and $detail.HealthStatus -ne 0) {
                    if (-not $entry.Notes.Contains('Storage stack reported degraded health.')) {
                        $entry.Notes.Add('Storage stack reported degraded health.')
                    }
                }
            }
        }
        catch {
            Write-TidyOutput -Message ("Physical disk health telemetry unavailable ({0})." -f $_.Exception.Message)
        }
    }

    $statusProvider = $null
    if (Get-Command -Name Get-CimInstance -ErrorAction SilentlyContinue) {
        $statusProvider = { Get-CimInstance -Namespace 'root/wmi' -ClassName 'MSStorageDriver_FailurePredictStatus' -ErrorAction Stop }
    }
    elseif (Get-Command -Name Get-WmiObject -ErrorAction SilentlyContinue) {
        $statusProvider = { Get-WmiObject -Namespace 'root/wmi' -Class 'MSStorageDriver_FailurePredictStatus' -ErrorAction Stop }
    }

    if ($null -ne $statusProvider) {
        try {
            $statusEntries = & $statusProvider
            foreach ($status in @($statusEntries)) {
                if ($null -eq $status) {
                    continue
                }

                $key = Get-TidyDeviceKey $status.InstanceName
                $entry = Ensure-TidyDiskInsight -Map $entries -IndexMap $diskKeyByNumber -Key $key -DiskNumber $null

                if ($status.PredictFailure) {
                    $entry.PredictFailure = $true
                }
                elseif ($null -eq $entry.PredictFailure) {
                    $entry.PredictFailure = $false
                }

                if ($null -ne $status.Reason -and -not [string]::IsNullOrWhiteSpace([string]$status.Reason)) {
                    $entry.Notes.Add([string]$status.Reason)
                }

                if ([string]::IsNullOrWhiteSpace($entry.FriendlyName)) {
                    $entry.FriendlyName = Convert-TidyDeviceLabel $status.InstanceName
                }
            }
        }
        catch {
            Write-TidyOutput -Message ("SMART predictive status provider not available ({0})." -f $_.Exception.Message)
        }
    }
    else {
        Write-TidyOutput -Message 'SMART predictive status provider APIs are not available on this platform.'
    }

    if ($TargetDiskNumbers.Count -gt 0) {
        foreach ($diskNumber in $TargetDiskNumbers) {
            if ($diskKeyByNumber.ContainsKey($diskNumber)) {
                $key = $diskKeyByNumber[$diskNumber]
                $entry = Ensure-TidyDiskInsight -Map $entries -IndexMap $diskKeyByNumber -Key $key -DiskNumber $diskNumber
                $entry.IsTargetVolume = $true
            }
        }
    }

    return $entries.Values |
        ForEach-Object {
            $statusList = $_.OperationalStatus
            $notesList = $_.Notes
            [pscustomobject]@{
                DiskNumber        = $_.DiskNumber
                FriendlyName      = $_.FriendlyName
                Model             = $_.Model
                SerialNumber      = $_.SerialNumber
                SizeBytes         = $_.SizeBytes
                PredictFailure    = $_.PredictFailure
                HealthStatus      = $_.HealthStatus
                OperationalStatus = if ($null -ne $statusList) { [System.Linq.Enumerable]::ToArray($statusList) } else { @() }
                Notes             = if ($null -ne $notesList) { [System.Linq.Enumerable]::ToArray($notesList) } else { @() }
                IsTargetVolume    = $_.IsTargetVolume
            }
        } |
        Sort-Object -Property @{ Expression = 'IsTargetVolume'; Descending = $true }, @{ Expression = 'DiskNumber'; Descending = $false }, @{ Expression = 'FriendlyName'; Descending = $false }
}

function Analyze-ChkdskOutput {
    param(
        [string[]] $Lines,
        [int] $ExitCode,
        [string] $Mode
    )

    $findings = [System.Collections.Generic.List[string]]::new()
    $result = [pscustomobject]@{
        Mode                 = $Mode
        Severity             = 'Info'
        Summary              = 'CHKDSK completed.'
        KeyFindings          = $findings
        ManualActionRequired = $false
        FoundBadSectors      = $false
        RepairsScheduled     = $false
    }

    foreach ($line in @($Lines)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $text = $line.Trim()
        $upper = $text.ToUpperInvariant()

        if ($upper -match 'WINDOWS HAS SCANNED THE FILE SYSTEM AND FOUND NO PROBLEMS') {
            $result.Summary = 'File system is healthy.'
        }

        if ($upper -match 'NO FURTHER ACTION IS REQUIRED') {
            if ($result.Summary -eq 'CHKDSK completed.') {
                $result.Summary = 'No further action required.'
            }
        }

        if ($upper -match 'WINDOWS HAS MADE CORRECTIONS TO THE FILE SYSTEM') {
            if (-not $findings.Contains('Repairs applied to the file system.')) {
                $findings.Add('Repairs applied to the file system.')
            }

            if ($result.Severity -ne 'Error') {
                $result.Severity = 'Warning'
            }
        }

        if ($upper -match 'WINDOWS FOUND PROBLEMS WITH THE FILE SYSTEM') {
            if (-not $findings.Contains('File system issues detected.')) {
                $findings.Add('File system issues detected.')
            }

            $result.Severity = 'Error'
            $result.ManualActionRequired = $true
        }

        if ($upper -match 'FAILED TO TRANSFER LOGGED MESSAGES TO THE EVENT LOG') {
            if (-not $findings.Contains('Failed to persist results to the Event Log.')) {
                $findings.Add('Failed to persist results to the Event Log.')
            }

            if ($result.Severity -ne 'Error') {
                $result.Severity = 'Warning'
            }
        }

        if ($upper -match 'CANNOT OPEN VOLUME FOR DIRECT ACCESS' -or $upper -match 'ACCESS DENIED') {
            if (-not $findings.Contains('Volume locked by another process.')) {
                $findings.Add('Volume locked by another process.')
            }

            $result.Severity = 'Error'
            $result.ManualActionRequired = $true
        }

        $mediaIssueDetected = $false
        $mediaPatterns = @(
            '(?i)(?<value>[\d,]+)\s+KB\s+in\s+bad\s+sectors',
            '(?i)(?<value>[\d,]+)\s+bad\s+sectors',
            '(?i)(?<value>[\d,]+)\s+bad\s+clusters'
        )

        foreach ($pattern in $mediaPatterns) {
            $match = [System.Text.RegularExpressions.Regex]::Match($text, $pattern)
            if (-not $match.Success) {
                continue
            }

            $raw = $match.Groups['value'].Value
            $numeric = 0L
            $parsed = [long]::TryParse(($raw -replace ',', ''), [ref]$numeric)

            if ($parsed -and $numeric -eq 0) {
                continue
            }

            $mediaIssueDetected = $true
            break
        }

        if (-not $mediaIssueDetected) {
            if ([System.Text.RegularExpressions.Regex]::IsMatch($text, '(?i)bad\s+sectors?\s+were\s+found') -or [System.Text.RegularExpressions.Regex]::IsMatch($text, '(?i)bad\s+sectors?\s+detected')) {
                $mediaIssueDetected = $true
            }
        }

        if ($mediaIssueDetected) {
            if (-not $findings.Contains('Physical media issues detected.')) {
                $findings.Add('Physical media issues detected.')
            }

            $result.FoundBadSectors = $true
            if ($result.Severity -ne 'Error') {
                $result.Severity = 'Warning'
            }

            $result.ManualActionRequired = $true
        }

        if ($upper -match 'WILL BE CHECKED THE NEXT TIME THE SYSTEM RESTARTS') {
            if (-not $findings.Contains('CHKDSK scheduled for next boot.')) {
                $findings.Add('CHKDSK scheduled for next boot.')
            }

            $result.RepairsScheduled = $true
        }
    }

    if ($ExitCode -ne 0 -and $result.Severity -eq 'Info') {
        $result.Severity = 'Warning'
        $findings.Add('CHKDSK returned a non-zero exit code.')
    }

    if ($result.FoundBadSectors) {
        $result.Summary = 'Physical media errors detected.'
    }
    elseif ($result.ManualActionRequired) {
        $result.Summary = 'Manual follow-up required.'
    }
    elseif ($result.Severity -eq 'Warning') {
        $result.Summary = 'Repairs applied; monitor the disk.'
    }

    return $result
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Disk checkup requires an elevated PowerShell session. Restart as administrator.'
    }

    $targetVolume = Resolve-VolumePath -Value $Volume
    Write-TidyOutput -Message ("Target volume: {0}" -f $targetVolume)

    Write-TidyLog -Level Information -Message ("Starting disk check for volume {0}." -f $targetVolume)

    $arguments = @($targetVolume)
    $modeDescription = 'online scan'

    if ($IncludeSurfaceScan.IsPresent -and -not $PerformRepair.IsPresent) {
        throw 'Surface scan requires -PerformRepair (it implies an offline /r pass).'
    }

    if ($IncludeSurfaceScan.IsPresent) {
        $PerformRepair = $true
    }

    if ($PerformRepair.IsPresent) {
        $arguments += '/f'
        $modeDescription = 'repair'
        if ($IncludeSurfaceScan.IsPresent) {
            $arguments += '/r'
            $modeDescription = 'repair with surface scan'
        }
    }
    elseif (-not $ScanOnly.IsPresent) {
        $arguments += '/scan'
        $modeDescription = 'online scan'
    }

    $targetDiskNumbers = @()
    $driveLetter = $null
    if ($targetVolume.Length -ge 1) {
        $driveLetter = $targetVolume.Substring(0, 1).ToUpperInvariant()
    }

    if ($driveLetter -and (Get-Command -Name Get-Partition -ErrorAction SilentlyContinue)) {
        try {
            $partitions = Get-Partition -DriveLetter $driveLetter -ErrorAction Stop
            if ($partitions) {
                $targetDiskNumbers = @($partitions | Select-Object -ExpandProperty DiskNumber -Unique)
                if ($targetDiskNumbers.Count -gt 0) {
                    $diskLabels = $targetDiskNumbers | Sort-Object | ForEach-Object { "Disk $_" }
                    Write-TidyOutput -Message ("Backing physical disk(s): {0}" -f ($diskLabels -join ', '))
                }
            }
        }
        catch {
            Write-TidyOutput -Message ("Unable to resolve backing disk information ({0})." -f $_.Exception.Message)
        }
    }

    Write-TidyOutput -Message ("Running CHKDSK in {0} mode." -f $modeDescription)
    $chkdskResult = Invoke-TidyCommand -Command { param($args) & chkdsk @args } -Arguments @($arguments) -Description ("CHKDSK {0}" -f ($arguments -join ' '))

    $chkdskExit = 0
    $chkdskOutput = @()

    if ($null -ne $chkdskResult) {
        if ($chkdskResult.PSObject.Properties['ExitCode']) {
            $chkdskExit = [int]$chkdskResult.ExitCode
        }

        if ($chkdskResult.PSObject.Properties['Output']) {
            $chkdskOutput = @($chkdskResult.Output)
        }
        elseif ($chkdskResult -is [System.Collections.IEnumerable]) {
            $chkdskOutput = @($chkdskResult)
        }
    }

    $chkdskLines = @()
    foreach ($entry in @($chkdskOutput)) {
        if ($null -eq $entry) {
            continue
        }

        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            $chkdskLines += $entry.ToString()
        }
        else {
            $chkdskLines += [string]$entry
        }
    }

    $scheduleRequired = $false
    foreach ($text in $chkdskLines) {
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        if ($text -match 'cannot lock current drive' -or $text -match 'schedule this volume to be checked') {
            $scheduleRequired = $true
            break
        }
    }

    $repairScheduledNow = $false
    if ($scheduleRequired -and $PerformRepair.IsPresent) {
        if ($ScheduleIfBusy.IsPresent) {
            Write-TidyOutput -Message 'Volume is busy. Scheduling repair for next reboot.'
            $confirmArgs = @($targetVolume, '/f')
            if ($IncludeSurfaceScan.IsPresent) {
                $confirmArgs += '/r'
            }

            $scheduleResult = Invoke-TidyCommand -Command { param($drive, $params) cmd.exe /c ("echo Y|chkdsk {0} {1}" -f $drive, ($params -join ' ')) } -Arguments @($targetVolume, $confirmArgs) -Description 'Scheduling CHKDSK at next reboot.'

            $scheduleExit = 0
            $scheduleOutput = @()
            if ($scheduleResult) {
                if ($scheduleResult.PSObject.Properties['ExitCode']) {
                    $scheduleExit = [int]$scheduleResult.ExitCode
                }

                if ($scheduleResult.PSObject.Properties['Output']) {
                    $scheduleOutput = @($scheduleResult.Output)
                }
                elseif ($scheduleResult -is [System.Collections.IEnumerable]) {
                    $scheduleOutput = @($scheduleResult)
                }
            }

            foreach ($entry in @($scheduleOutput)) {
                if ([string]::IsNullOrWhiteSpace($entry)) {
                    continue
                }

                if ($entry -match 'will be checked the next time the system restarts') {
                    Write-TidyOutput -Message 'Repair successfully scheduled. Reboot to run the offline pass.'
                    $repairScheduledNow = $true
                    break
                }
            }

            if ($repairScheduledNow -and ($chkdskLines -notcontains 'CHKDSK scheduled for next boot.')) {
                $chkdskLines += 'CHKDSK scheduled for next boot.'
            }
        }
        else {
            Write-TidyOutput -Message 'Volume is busy. Re-run with -ScheduleIfBusy or manually confirm the prompt to repair at next boot.'
        }
    }

    $chkdskAnalysis = Analyze-ChkdskOutput -Lines $chkdskLines -ExitCode $chkdskExit -Mode $modeDescription
    if ($repairScheduledNow) {
        $chkdskAnalysis.RepairsScheduled = $true
    }

    if ($chkdskAnalysis) {
        $initialChkdskAnalysis = $chkdskAnalysis
        if (-not [string]::IsNullOrWhiteSpace($chkdskAnalysis.Summary)) {
            Write-TidyOutput -Message ("CHKDSK summary: {0}" -f $chkdskAnalysis.Summary)
        }

        foreach ($finding in @($chkdskAnalysis.KeyFindings)) {
            if ([string]::IsNullOrWhiteSpace($finding)) {
                continue
            }

            Write-TidyOutput -Message ("  ↳ {0}" -f $finding)
        }

        if (($chkdskAnalysis.ManualActionRequired -or $chkdskAnalysis.RepairsScheduled -or $chkdskAnalysis.Severity -eq 'Error') -and -not $PerformRepair.IsPresent) {
            Write-TidyOutput -Message 'Attempting automatic repair because the scan detected issues and -PerformRepair was not supplied.'
            $autoRepairArgs = @($targetVolume, '/f')
            $autoRepairIncludeSurfaceScan = $chkdskAnalysis.FoundBadSectors
            if ($autoRepairIncludeSurfaceScan) {
                Write-TidyOutput -Message 'Physical media issues were detected; including a surface scan in the automatic repair.'
                $autoRepairArgs += '/r'
            }

            $autoRepairModeDescription = if ($autoRepairIncludeSurfaceScan) { 'repair with surface scan (automatic)' } else { 'repair (automatic)' }
            Write-TidyOutput -Message ("Running CHKDSK in {0} mode." -f $autoRepairModeDescription)

            $autoRepairResult = Invoke-TidyCommand -Command { param($args) & chkdsk @args } -Arguments @($autoRepairArgs) -Description ("CHKDSK {0}" -f ($autoRepairArgs -join ' '))

            $autoRepairExit = 0
            $autoRepairOutput = @()
            if ($null -ne $autoRepairResult) {
                if ($autoRepairResult.PSObject.Properties['ExitCode']) {
                    $autoRepairExit = [int]$autoRepairResult.ExitCode
                }

                if ($autoRepairResult.PSObject.Properties['Output']) {
                    $autoRepairOutput = @($autoRepairResult.Output)
                }
                elseif ($autoRepairResult -is [System.Collections.IEnumerable]) {
                    $autoRepairOutput = @($autoRepairResult)
                }
            }

            $autoRepairLines = @()
            foreach ($entry in @($autoRepairOutput)) {
                if ($null -eq $entry) {
                    continue
                }

                if ($entry -is [System.Management.Automation.ErrorRecord]) {
                    $autoRepairLines += $entry.ToString()
                }
                else {
                    $autoRepairLines += [string]$entry
                }
            }

            $autoRepairScheduleRequired = $false
            foreach ($text in $autoRepairLines) {
                if ([string]::IsNullOrWhiteSpace($text)) {
                    continue
                }

                if ($text -match 'cannot lock current drive' -or $text -match 'schedule this volume to be checked') {
                    $autoRepairScheduleRequired = $true
                    break
                }
            }

            $autoRepairScheduledNow = $false
            if ($autoRepairScheduleRequired) {
                Write-TidyOutput -Message 'Volume is busy. Scheduling repair for next reboot automatically.'
                $scheduleResult = Invoke-TidyCommand -Command { param($drive, $params) cmd.exe /c ("echo Y|chkdsk {0} {1}" -f $drive, ($params -join ' ')) } -Arguments @($targetVolume, $autoRepairArgs) -Description 'Scheduling CHKDSK (automatic repair).'

                $scheduleOutput = @()
                if ($scheduleResult) {
                    if ($scheduleResult.PSObject.Properties['Output']) {
                        $scheduleOutput = @($scheduleResult.Output)
                    }
                    elseif ($scheduleResult -is [System.Collections.IEnumerable]) {
                        $scheduleOutput = @($scheduleResult)
                    }
                }

                foreach ($entry in @($scheduleOutput)) {
                    if ([string]::IsNullOrWhiteSpace($entry)) {
                        continue
                    }

                    if ($entry -match 'will be checked the next time the system restarts') {
                        Write-TidyOutput -Message 'Repair successfully scheduled. Reboot to run the offline pass.'
                        $autoRepairScheduledNow = $true
                        break
                    }
                }

                if ($autoRepairScheduledNow) {
                    if ($autoRepairLines -notcontains 'CHKDSK scheduled for next boot.') {
                        $autoRepairLines += 'CHKDSK scheduled for next boot.'
                    }
                }
                else {
                    Write-TidyOutput -Message 'Unable to confirm scheduling automatically. Please schedule the repair manually if prompted.'
                }
            }

            $autoRepairAnalysis = Analyze-ChkdskOutput -Lines $autoRepairLines -ExitCode $autoRepairExit -Mode $autoRepairModeDescription
            if ($autoRepairAnalysis -and $autoRepairScheduledNow) {
                $autoRepairAnalysis.RepairsScheduled = $true
            }

            $autoRepairHasMeaningfulData = ($autoRepairLines.Count -gt 0) -or ($autoRepairExit -ne 0) -or $autoRepairScheduledNow
            if ($autoRepairAnalysis -and $autoRepairHasMeaningfulData) {
                if (-not [string]::IsNullOrWhiteSpace($autoRepairAnalysis.Summary)) {
                    Write-TidyOutput -Message ("CHKDSK (automatic repair) summary: {0}" -f $autoRepairAnalysis.Summary)
                }

                foreach ($finding in @($autoRepairAnalysis.KeyFindings)) {
                    if ([string]::IsNullOrWhiteSpace($finding)) {
                        continue
                    }

                    Write-TidyOutput -Message ("  ↳ {0}" -f $finding)
                }

                $chkdskAnalysis = $autoRepairAnalysis
            }
            else {
                if (-not $autoRepairHasMeaningfulData) {
                    Write-TidyOutput -Message 'Automatic repair attempt did not return detailed output. Review CHKDSK logs in Event Viewer for more information.'
                }

                $chkdskAnalysis = $initialChkdskAnalysis
            }
        }
    }

    $smartData = @()
    if (-not $SkipSmart.IsPresent) {
        Write-TidyOutput -Message 'Collecting SMART health indicators.'
        $smartData = Get-SmartStatus -TargetDiskNumbers $targetDiskNumbers
        if ($null -eq $smartData -or $smartData.Count -eq 0) {
            Write-TidyOutput -Message 'SMART data unavailable on this platform or storage bus.'
            $smartData = @()
        }
        else {
            $normalizedMap = [System.Collections.Generic.Dictionary[string, pscustomobject]]::new([System.StringComparer]::OrdinalIgnoreCase)
            $normalizedList = [System.Collections.Generic.List[pscustomobject]]::new()

            foreach ($entry in @($smartData)) {
                if ($null -eq $entry) {
                    continue
                }

                $diskNumber = $null
                $diskNumberValue = Get-TidyPropertyValue -InputObject $entry -PropertyName 'DiskNumber'
                if ($null -ne $diskNumberValue) {
                    try {
                        $diskNumber = [int]$diskNumberValue
                    }
                    catch {
                        $diskNumber = $null
                    }
                }

                $friendlyName = Get-TidyPropertyValue -InputObject $entry -PropertyName 'FriendlyName'
                if ($null -ne $friendlyName) {
                    $friendlyName = [string]$friendlyName
                    if ([string]::IsNullOrWhiteSpace($friendlyName)) {
                        $friendlyName = $null
                    }
                }

                $model = Get-TidyPropertyValue -InputObject $entry -PropertyName 'Model'
                if ($null -ne $model) {
                    $model = [string]$model
                    if ([string]::IsNullOrWhiteSpace($model)) {
                        $model = $null
                    }
                }

                $serialNumber = Get-TidyPropertyValue -InputObject $entry -PropertyName 'SerialNumber'
                if ($null -ne $serialNumber) {
                    $serialNumber = [string]$serialNumber
                    if ([string]::IsNullOrWhiteSpace($serialNumber)) {
                        $serialNumber = $null
                    }
                }

                $sizeBytes = $null
                $sizeValue = Get-TidyPropertyValue -InputObject $entry -PropertyName 'SizeBytes'
                if ($null -ne $sizeValue) {
                    try {
                        $sizeBytes = [double]$sizeValue
                    }
                    catch {
                        $sizeBytes = $null
                    }
                }

                $predictFailure = Convert-TidyToNullableBool -Value (Get-TidyPropertyValue -InputObject $entry -PropertyName 'PredictFailure')

                $healthStatus = Get-TidyPropertyValue -InputObject $entry -PropertyName 'HealthStatus'
                if ($null -ne $healthStatus) {
                    $healthStatus = [string]$healthStatus
                    if ([string]::IsNullOrWhiteSpace($healthStatus)) {
                        $healthStatus = $null
                    }
                }

                $operationalSource = Get-TidyPropertyValue -InputObject $entry -PropertyName 'OperationalStatus'
                $operationalStatus = @((Convert-TidyToStringArray -Value $operationalSource) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)

                $notesSource = Get-TidyPropertyValue -InputObject $entry -PropertyName 'Notes'
                $notes = @((Convert-TidyToStringArray -Value $notesSource) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)

                $isTargetVolume = (Convert-TidyToNullableBool -Value (Get-TidyPropertyValue -InputObject $entry -PropertyName 'IsTargetVolume')) -eq $true

                $hasIdentity = ($null -ne $diskNumber) -or (-not [string]::IsNullOrWhiteSpace($serialNumber)) -or (-not [string]::IsNullOrWhiteSpace($friendlyName)) -or (-not [string]::IsNullOrWhiteSpace($model))
                if (-not $hasIdentity) {
                    continue
                }

                $insight = [pscustomobject]@{
                    DiskNumber        = $diskNumber
                    FriendlyName      = $friendlyName
                    Model             = $model
                    SerialNumber      = $serialNumber
                    SizeBytes         = $sizeBytes
                    PredictFailure    = $predictFailure
                    HealthStatus      = $healthStatus
                    OperationalStatus = $operationalStatus
                    Notes             = $notes
                    IsTargetVolume    = $isTargetVolume
                }

                $keyCandidates = Get-TidyInsightKeyCandidates -DiskNumber $insight.DiskNumber -SerialNumber $insight.SerialNumber -FriendlyName $insight.FriendlyName -Model $insight.Model

                $existing = $null
                foreach ($candidate in $keyCandidates) {
                    if ($normalizedMap.ContainsKey($candidate)) {
                        $existing = $normalizedMap[$candidate]
                        break
                    }
                }

                if ($null -eq $existing) {
                    foreach ($candidate in $keyCandidates) {
                        if (-not $normalizedMap.ContainsKey($candidate)) {
                            $normalizedMap[$candidate] = $insight
                        }
                    }

                    $normalizedList.Add($insight)
                }
                else {
                    $merged = Merge-TidyDiskInsight -Target $existing -Source $insight
                    foreach ($candidate in $keyCandidates) {
                        $normalizedMap[$candidate] = $merged
                    }
                }
            }

            foreach ($insight in $normalizedList) {
                if ($null -eq $insight.OperationalStatus) {
                    $insight.OperationalStatus = @()
                }

                if ($null -eq $insight.Notes) {
                    $insight.Notes = @()
                }

                $labelParts = @()
                if ($null -ne $insight.DiskNumber) {
                    $labelParts += ("Disk {0}" -f $insight.DiskNumber)
                }

                if (-not [string]::IsNullOrWhiteSpace($insight.FriendlyName)) {
                    $labelParts += $insight.FriendlyName
                }
                elseif (-not [string]::IsNullOrWhiteSpace($insight.Model)) {
                    $labelParts += $insight.Model
                }

                $label = if ($labelParts.Count -gt 0) { $labelParts -join ' · ' } else { 'Unknown disk' }
                if ($insight.IsTargetVolume) {
                    $label = "{0} (hosts {1})" -f $label, $targetVolume
                }

                $statusLabel = 'Unknown'
                if ($insight.PredictFailure -eq $true) {
                    $statusLabel = 'At Risk'
                }
                elseif ($insight.PredictFailure -eq $false) {
                    $statusLabel = 'Healthy'
                }

                Write-TidyOutput -Message ("[{0}] {1}" -f $statusLabel, $label)

                if (-not [string]::IsNullOrWhiteSpace($insight.Model) -and ($insight.Model -ne $insight.FriendlyName)) {
                    Write-TidyOutput -Message ("  ↳ Model: {0}" -f $insight.Model)
                }

                if (-not [string]::IsNullOrWhiteSpace($insight.SerialNumber)) {
                    Write-TidyOutput -Message ("  ↳ Serial: {0}" -f $insight.SerialNumber)
                }

                if ($null -ne $insight.SizeBytes -and $insight.SizeBytes -gt 0) {
                    $sizeGiB = [Math]::Round($insight.SizeBytes / 1GB, 1)
                    Write-TidyOutput -Message ("  ↳ Capacity: {0} GiB" -f $sizeGiB)
                }

                if (-not [string]::IsNullOrWhiteSpace($insight.HealthStatus)) {
                    Write-TidyOutput -Message ("  ↳ Health: {0}" -f $insight.HealthStatus)
                }

                $states = ($insight.OperationalStatus | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ', '
                if (-not [string]::IsNullOrWhiteSpace($states)) {
                    Write-TidyOutput -Message ("  ↳ Operational: {0}" -f $states)
                }

                foreach ($note in $insight.Notes) {
                    if ([string]::IsNullOrWhiteSpace($note)) {
                        continue
                    }

                    Write-TidyOutput -Message ("  ↳ {0}" -f $note)
                }
            }

            $smartData = $normalizedList.ToArray()
        }
    }

    $overallAssessment = 'Healthy'
    $recommendation = 'No immediate action required.'
    $alertReasons = @()

    if ($chkdskAnalysis) {
        if ($chkdskAnalysis.Severity -eq 'Error' -or $chkdskAnalysis.ManualActionRequired) {
            $overallAssessment = 'Action required'
            $recommendation = 'Resolve the reported file system issues and rerun CHKDSK after an offline repair.'
            $alertReasons += 'CHKDSK reported unresolved issues.'
        }
        elseif ($chkdskAnalysis.Severity -eq 'Warning') {
            if ($overallAssessment -eq 'Healthy') {
                $overallAssessment = 'Monitor'
                $recommendation = 'Review the CHKDSK findings and monitor the disk for recurring warnings.'
            }

            $alertReasons += 'CHKDSK completed with warnings.'
        }

        if ($chkdskAnalysis.FoundBadSectors) {
            $overallAssessment = 'Degraded'
            $recommendation = 'Back up critical data and consider replacing the disk due to bad sectors.'
            if ($alertReasons -notcontains 'Physical media issues detected.') {
                $alertReasons += 'Physical media issues detected.'
            }
        }
    }

    if ($smartData.Count -gt 0) {
        if ($smartData | Where-Object { $_.PredictFailure -eq $true }) {
            $overallAssessment = 'Critical'
            $recommendation = 'Back up data immediately and plan to replace the failing disk.'
            if ($alertReasons -notcontains 'SMART predicts imminent failure.') {
                $alertReasons += 'SMART predicts imminent failure.'
            }
        }
        elseif ($smartData | Where-Object { $_.HealthStatus -and $_.HealthStatus -notin @('Healthy', 'OK') }) {
            if ($overallAssessment -notin @('Critical', 'Degraded')) {
                $overallAssessment = 'Monitor'
            }

            if ($recommendation -eq 'No immediate action required.') {
                $recommendation = 'Monitor SMART health and run vendor diagnostics if the status deteriorates.'
            }

            if ($alertReasons -notcontains 'SMART health degraded.') {
                $alertReasons += 'SMART health degraded.'
            }
        }
    }

    Write-TidyOutput -Message ("Overall assessment: {0}" -f $overallAssessment)
    if ($alertReasons.Count -gt 0) {
        Write-TidyOutput -Message ("  ↳ Reasons: {0}" -f ($alertReasons -join '; '))
    }
    Write-TidyOutput -Message ("Recommendation: {0}" -f $recommendation)

    Write-TidyOutput -Message 'Disk checkup completed.'
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
    Write-TidyLog -Level Information -Message 'Disk checkup script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
