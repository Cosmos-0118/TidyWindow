function Resolve-TidyPath {
    [CmdletBinding()]
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    if ($expanded.StartsWith('~')) {
        # Avoid assigning to the read-only $HOME automatic variable
        $userProfile = $env:USERPROFILE
        if (-not [string]::IsNullOrWhiteSpace($userProfile)) {
            $expanded = $userProfile + $expanded.Substring(1)
        }
    }

    try {
        return [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        return $expanded
    }
}

function ConvertTo-TidyNameKey {
    [CmdletBinding()]
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $clean = $Value.ToLowerInvariant()
    $clean = [System.Text.RegularExpressions.Regex]::Replace($clean, '[^a-z0-9]', '')
    if ([string]::IsNullOrWhiteSpace($clean)) {
        return $null
    }

    return $clean
}

function Get-TidyProgramDataDirectory {
    [CmdletBinding()]
    param()

    $programData = $env:ProgramData
    if ([string]::IsNullOrWhiteSpace($programData)) {
        $programData = 'C:\ProgramData'
    }

    $root = Join-Path -Path $programData -ChildPath 'TidyWindow'
    if (-not (Test-Path -LiteralPath $root)) {
        [void](New-Item -Path $root -ItemType Directory -Force)
    }

    return $root
}

function New-TidyFeatureRunDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $FeatureName,
        [Parameter(Mandatory = $true)]
        [string] $AppIdentifier
    )

    $root = Get-TidyProgramDataDirectory
    $featureRoot = Join-Path -Path $root -ChildPath $FeatureName
    if (-not (Test-Path -LiteralPath $featureRoot)) {
        [void](New-Item -Path $featureRoot -ItemType Directory -Force)
    }

    $safeId = [System.Text.RegularExpressions.Regex]::Replace($AppIdentifier, '[^A-Za-z0-9_-]', '_')
    if ([string]::IsNullOrWhiteSpace($safeId)) {
        $safeId = 'app'
    }

    $target = Join-Path -Path $featureRoot -ChildPath $safeId
    if (-not (Test-Path -LiteralPath $target)) {
        [void](New-Item -Path $target -ItemType Directory -Force)
    }

    return $target
}

function Write-TidyStructuredEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Type,
        [object] $Payload
    )

    $envelope = [ordered]@{
        type      = $Type
        timestamp = [DateTimeOffset]::UtcNow.ToString('o')
    }

    if ($Payload) {
        if ($Payload -is [System.Collections.IDictionary]) {
            foreach ($key in $Payload.Keys) {
                $envelope[$key] = $Payload[$key]
            }
        }
        elseif ($Payload -is [pscustomobject]) {
            foreach ($prop in $Payload.PSObject.Properties) {
                if (-not [string]::IsNullOrWhiteSpace($prop.Name)) {
                    $envelope[$prop.Name] = $prop.Value
                }
            }
        }
        else {
            $envelope['payload'] = $Payload
        }
    }

    $json = $envelope | ConvertTo-Json -Depth 6 -Compress
    Write-Output $json
}

function Write-TidyRunLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [hashtable] $Payload
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
        [void](New-Item -Path $directory -ItemType Directory -Force)
    }

    $Payload | ConvertTo-Json -Depth 6 | Out-File -FilePath $Path -Encoding utf8 -Force
}

function Invoke-TidyCommandLine {
    [CmdletBinding()]
    param([string] $CommandLine)

    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return [pscustomobject]@{ exitCode = 0; output = ''; errors = ''; durationMs = 0 }
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = "/d /s /c ""$CommandLine"""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    $start = [DateTimeOffset]::UtcNow
    $null = $process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $duration = ([DateTimeOffset]::UtcNow - $start).TotalMilliseconds

    return [pscustomobject]@{
        exitCode  = $process.ExitCode
        output    = $stdout
        errors    = $stderr
        durationMs = [math]::Round($duration, 0)
    }
}

function Get-TidyProcessSnapshot {
    [CmdletBinding()]
    param()

    $list = New-Object 'System.Collections.Generic.List[psobject]'
    try {
        Get-Process | ForEach-Object {
            $path = $null
            try { $path = $_.Path } catch { $path = $null }
            $list.Add([pscustomobject]@{ id = $_.Id; name = $_.ProcessName; path = $path }) | Out-Null
        }
    }
    catch {
        # Ignore snapshot failures.
    }

    return $list
}

function Get-TidyServiceSnapshot {
    [CmdletBinding()]
    param()

    $list = New-Object 'System.Collections.Generic.List[psobject]'
    try {
        Get-CimInstance -ClassName Win32_Service | ForEach-Object {
            $list.Add([pscustomobject]@{
                name        = $_.Name
                displayName = $_.DisplayName
                path        = $_.PathName
                state       = $_.State
            }) | Out-Null
        }
    }
    catch {
        # Ignore snapshot failures.
    }

    return $list
}

function ConvertTo-TidyRegistryPath {
    [CmdletBinding()]
    param([string] $KeyPath)

    if ([string]::IsNullOrWhiteSpace($KeyPath)) { return $null }

    switch -Regex ($KeyPath) {
        '^(HKEY_LOCAL_MACHINE|HKLM)\\(.+)$' { return "Registry::HKEY_LOCAL_MACHINE\$($matches[2])" }
        '^(HKEY_CURRENT_USER|HKCU)\\(.+)$'  { return "Registry::HKEY_CURRENT_USER\$($matches[2])" }
        '^(HKEY_CLASSES_ROOT|HKCR)\\(.+)$'  { return "Registry::HKEY_CLASSES_ROOT\$($matches[2])" }
        '^(HKEY_USERS|HKU)\\(.+)$'          { return "Registry::HKEY_USERS\$($matches[2])" }
        '^(HKEY_CURRENT_CONFIG|HKCC)\\(.+)$'{ return "Registry::HKEY_CURRENT_CONFIG\$($matches[2])" }
        Default { return $KeyPath }
    }
}

function Measure-TidyDirectoryBytes {
    [CmdletBinding()]
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return 0 }

    try {
        $items = Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue
        return ($items | Measure-Object -Property Length -Sum).Sum
    }
    catch {
        return 0
    }
}

function New-TidyArtifactId {
    [CmdletBinding()]
    param()

    return [Guid]::NewGuid().ToString('n')
}

