function Remove-TidyAnsiSequences {
    # Strips ANSI escape codes so UI output remains readable.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string] $Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $Text
    }

    $clean = [System.Text.RegularExpressions.Regex]::Replace($Text, '\x1B\[[0-9;?]*[ -/]*[@-~]', '')
    $clean = [System.Text.RegularExpressions.Regex]::Replace($clean, '\x1B', '')
    $clean = [System.Text.RegularExpressions.Regex]::Replace($clean, '\[[0-9;]{1,5}[A-Za-z]', '')
    return $clean
}

function Convert-TidyLogMessage {
    # Normalizes log payloads into printable strings to avoid binding errors.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object] $InputObject
    )

    if ($null -eq $InputObject) {
        return '<null>'
    }

    if ($InputObject -is [string]) {
        return Remove-TidyAnsiSequences -Text $InputObject
    }

    if ($InputObject -is [pscustomobject]) {
        $pairs = foreach ($prop in $InputObject.PSObject.Properties) {
            $key = if ([string]::IsNullOrEmpty($prop.Name)) { '<unnamed>' } else { $prop.Name }
            $value = Convert-TidyLogMessage -InputObject $prop.Value
            "$key=$value"
        }

        return $pairs -join '; '
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        $pairs = @()
        foreach ($entry in $InputObject.GetEnumerator()) {
            $key = if ($null -eq $entry.Key) { '<null>' } else { $entry.Key.ToString() }
            $value = Convert-TidyLogMessage -InputObject $entry.Value
            $pairs += "$key=$value"
        }
        return $pairs -join '; '
    }

    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $items = foreach ($item in $InputObject) { Convert-TidyLogMessage -InputObject $item }
        return $items -join [Environment]::NewLine
    }

    try {
        $converted = [System.Management.Automation.LanguagePrimitives]::ConvertTo($InputObject, [string])
        return Remove-TidyAnsiSequences -Text $converted
    }
    catch {
        $fallback = ($InputObject | Out-String).TrimEnd()
        return Remove-TidyAnsiSequences -Text $fallback
    }
}

function Write-TidyLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Information', 'Warning', 'Error')]
        [string] $Level,
        [Parameter(Mandatory = $true, ValueFromPipeline = $true, ValueFromRemainingArguments = $true)]
        [object[]] $Message
    )

    process {
    $timestamp = [DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz', [System.Globalization.CultureInfo]::InvariantCulture)
        $parts = foreach ($segment in $Message) { Convert-TidyLogMessage -InputObject $segment }
        $text = ($parts -join ' ').Trim()
        if ([string]::IsNullOrWhiteSpace($text)) {
            $text = '<empty>'
        }

        Write-Host "[$timestamp][$Level] $text"
    }
}

function Get-TidyCommandPath {
    # Resolves the absolute path to a CLI tool when available.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommandName
    )

    $command = Get-Command -Name $CommandName -ErrorAction SilentlyContinue
    if (-not $command) {
        return $null
    }

    if (-not [string]::IsNullOrWhiteSpace($command.Source)) {
        return $command.Source
    }

    if (-not [string]::IsNullOrWhiteSpace($command.Path)) {
        return $command.Path
    }

    return $command.Name
}

function Get-TidyWingetInstalledVersion {
    # Detects the installed version of a winget package if present.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    $exe = Get-TidyCommandPath -CommandName 'winget'
    if (-not $exe) {
        return $null
    }

    try {
        $fallback = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null
        foreach ($line in @($fallback)) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $text = [string]$line
            $clean = [System.Text.RegularExpressions.Regex]::Replace($text, '\x1B\[[0-9;]*[A-Za-z]', '')
            $clean = $clean.Replace("`r", '')
            if ([string]::IsNullOrWhiteSpace($clean)) {
                continue
            }

            if ($clean -match '^(?i)\s*Name\s+Id\s+Version') { continue }
            if ($clean -match '^-{3,}') { continue }
            if ($clean -match '^(?i).*no installed package.*') { return $null }
            if ($clean -match '^(?i).*no installed packages found.*') { return $null }

            $trimmed = $clean.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }

            $pattern = '^(?<name>.+?)\s+' + [System.Text.RegularExpressions.Regex]::Escape($PackageId) + '\s+(?<version>[^\s]+)'
            $match = [System.Text.RegularExpressions.Regex]::Match($trimmed, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($match.Success) {
                $candidate = $match.Groups['version'].Value.Trim()
                if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                    return $candidate
                }
            }
        }
    }
    catch {
        # No further fallback available.
    }

    return $null
}

function Get-TidyChocoInstalledVersion {
    # Detects the installed version of a Chocolatey package.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    $installRoot = $env:ChocolateyInstall
    if ([string]::IsNullOrWhiteSpace($installRoot)) {
        $installRoot = 'C:\ProgramData\chocolatey'
    }

    if (-not (Test-Path -LiteralPath $installRoot)) {
        return $null
    }

    $libRoot = Join-Path -Path $installRoot -ChildPath 'lib'
    if (-not (Test-Path -LiteralPath $libRoot)) {
        return $null
    }

    $candidateDirs = @()
    $directPath = Join-Path -Path $libRoot -ChildPath $PackageId
    if (Test-Path -LiteralPath $directPath) {
        $candidateDirs += (Get-Item -LiteralPath $directPath)
    }

    if ($candidateDirs.Count -eq 0) {
        try {
            $candidateDirs = Get-ChildItem -Path $libRoot -Directory -ErrorAction Stop | Where-Object {
                [string]::Equals($_.Name, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)
            }
        }
        catch {
            $candidateDirs = @()
        }
    }

    if ($candidateDirs.Count -eq 0) {
        try {
            $candidateDirs = Get-ChildItem -Path $libRoot -Directory -ErrorAction Stop | Where-Object {
                $nuspec = Get-ChildItem -Path $_.FullName -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
                if (-not $nuspec) {
                    $false
                }
                else {
                    $matchesId = $false
                    try {
                        $xml = [xml](Get-Content -LiteralPath $nuspec.FullName -Raw -ErrorAction Stop)
                        $metadata = $xml.package.metadata
                        if ($metadata) {
                            $idValue = $metadata.id
                            if (-not [string]::IsNullOrWhiteSpace($idValue) -and [string]::Equals($idValue, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                                $matchesId = $true
                            }
                        }
                    }
                    catch {
                        $matchesId = $false
                    }

                    $matchesId
                }
            }
        }
        catch {
            $candidateDirs = @()
        }
    }

    foreach ($dir in $candidateDirs) {
        try {
            $nuspec = Get-ChildItem -Path $dir.FullName -Filter '*.nuspec' -File -ErrorAction Stop | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $nuspec) { continue }

            $xml = [xml](Get-Content -LiteralPath $nuspec.FullName -Raw -ErrorAction Stop)
            $metadata = $xml.package.metadata
            if ($metadata -and $metadata.version) {
                $candidate = $metadata.version.ToString().Trim()
                if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                    return $candidate
                }
            }
        }
        catch {
            continue
        }
    }

    return $null
}

function Get-TidyScoopInstalledVersion {
    # Detects the installed version of a Scoop package.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    $exe = Get-TidyCommandPath -CommandName 'scoop'
    if (-not $exe) {
        return $null
    }

    $candidates = [System.Collections.Generic.List[string]]::new()

    function Add-CandidateValue {
        param([string] $Value)

        if ([string]::IsNullOrWhiteSpace($Value)) {
            return
        }

        $trimmed = $Value.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            return
        }

        if ($trimmed -in @('-', 'n/a', 'Not installed', 'not installed', 'Not Installed')) {
            return
        }

        [void]$candidates.Add($trimmed)
    }

    try {
        $output = & $exe 'info' $PackageId 2>$null
        if ($LASTEXITCODE -eq 0 -and $output) {
            foreach ($entry in $output) {
                if ($null -eq $entry) {
                    continue
                }

                if ($entry -is [pscustomobject]) {
                    $candidate = $null
                    if ($entry.PSObject.Properties.Match('Installed')) { $candidate = $entry.Installed }
                    elseif ($entry.PSObject.Properties.Match('installed')) { $candidate = $entry.installed }
                    elseif ($entry.PSObject.Properties.Match('Version')) { $candidate = $entry.Version }
                    elseif ($entry.PSObject.Properties.Match('version')) { $candidate = $entry.version }

                    if ($null -ne $candidate) {
                        Add-CandidateValue -Value ($candidate.ToString())
                    }

                    continue
                }

                $text = $entry.ToString()
                if ([string]::IsNullOrWhiteSpace($text)) {
                    continue
                }

                if ($text -match '^Installed\s*:\s*(?<ver>.+)$') {
                    Add-CandidateValue -Value $matches['ver']
                    continue
                }

                if ($text -match '^Version\s*:\s*(?<ver>.+)$') {
                    Add-CandidateValue -Value $matches['ver']
                    continue
                }

                if ($text -match '^Latest Version\s*:\s*(?<ver>.+)$') {
                    Add-CandidateValue -Value $matches['ver']
                    continue
                }

                if ($text -match '^\s*(?<ver>[0-9][0-9A-Za-z\.\-_+]*)\s*$') {
                    Add-CandidateValue -Value $matches['ver']
                }
            }
        }
    }
    catch {
        # Continue to list parsing.
    }

    try {
        $output = & $exe 'list' $PackageId 2>$null
        if ($LASTEXITCODE -eq 0 -and $output) {
            foreach ($entry in $output) {
                if ($null -eq $entry) {
                    continue
                }

                if ($entry -is [pscustomobject]) {
                    $name = $entry.Name
                    if (-not $name) { $name = $entry.name }
                    if (-not $name) { continue }

                    if ([string]::Equals($name, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $candidate = $entry.Version
                        if (-not $candidate) { $candidate = $entry.version }
                        if ($candidate) {
                            Add-CandidateValue -Value ($candidate.ToString())
                        }
                    }

                    continue
                }

                $text = $entry.ToString()
                if ([string]::IsNullOrWhiteSpace($text)) {
                    continue
                }

                if ($text -like 'Installed apps matching*') { continue }
                if ($text -match '^[\s-]+$') { continue }
                if ($text -match '^(Name|----)\s') { continue }

                $match = [System.Text.RegularExpressions.Regex]::Match($text, '^\s*(?<name>\S+)\s+(?<ver>\S+)')
                if ($match.Success -and [string]::Equals($match.Groups['name'].Value, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                    Add-CandidateValue -Value $match.Groups['ver'].Value
                }
            }
        }
    }
    catch {
        # No further fallback available.
    }

    if ($candidates.Count -eq 0) {
        return $null
    }

    $unique = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $best = $null
    $bestVersion = $null

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $trimmed = $candidate.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        if (-not $unique.Add($trimmed)) {
            continue
        }

        $match = [System.Text.RegularExpressions.Regex]::Match($trimmed, '([0-9]+(?:[\._][0-9A-Za-z]+)*)')
        $parsed = $null
        if ($match.Success -and [version]::TryParse($match.Groups[1].Value.Replace('_', '.'), [ref]$parsed)) {
            if (($bestVersion -eq $null) -or ($parsed -gt $bestVersion)) {
                $bestVersion = $parsed
                $best = $match.Groups[1].Value.Replace('_', '.')
            }
        }
        elseif (-not $best) {
            $best = $trimmed
        }
    }

    return $best
}

function Get-TidyInstalledPackageVersion {
    # Normalizes package manager hints and retrieves installed versions when available.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Manager,
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    if ([string]::IsNullOrWhiteSpace($Manager) -or [string]::IsNullOrWhiteSpace($PackageId)) {
        return $null
    }

    $normalized = $Manager.Trim().ToLowerInvariant()
    switch ($normalized) {
        'winget'       { return Get-TidyWingetInstalledVersion -PackageId $PackageId }
        'choco'        { return Get-TidyChocoInstalledVersion -PackageId $PackageId }
        'chocolatey'   { return Get-TidyChocoInstalledVersion -PackageId $PackageId }
        'scoop'        { return Get-TidyScoopInstalledVersion -PackageId $PackageId }
        default        { return $null }
    }
}

function Assert-TidyAdmin {
    if (-not ([bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) {
        throw 'Administrator privileges are required for this operation.'
    }
}

function Invoke-TidyRegistryScript {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $ScriptName,
        [hashtable] $Parameters
    )

    $registryRoot = Join-Path -Path $PSScriptRoot -ChildPath '..\registry'
    $scriptPath = Join-Path -Path $registryRoot -ChildPath $ScriptName
    $scriptPath = [System.IO.Path]::GetFullPath($scriptPath)

    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "Registry automation script not found at path '$scriptPath'."
    }

    if (-not $Parameters) {
        $Parameters = @{}
    }

    & $scriptPath @Parameters
}

function Add-TidyShouldProcessParameters {
    param(
        [hashtable] $Parameters,
        [System.Collections.IDictionary] $BoundParameters
    )

    if (-not $Parameters) {
        $Parameters = @{}
    }

    if ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'ContainsKey') -and $BoundParameters.ContainsKey('WhatIf')) {
        $Parameters['WhatIf'] = $BoundParameters['WhatIf']
    }
    elseif ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'Contains') -and $BoundParameters.Contains('WhatIf')) {
        $Parameters['WhatIf'] = $BoundParameters['WhatIf']
    }

    if ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'ContainsKey') -and $BoundParameters.ContainsKey('Confirm')) {
        $Parameters['Confirm'] = $BoundParameters['Confirm']
    }
    elseif ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'Contains') -and $BoundParameters.Contains('Confirm')) {
        $Parameters['Confirm'] = $BoundParameters['Confirm']
    }

    return $Parameters
}

function Set-TidyMenuShowDelay {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateRange(0, 2000)]
        [int] $DelayMilliseconds = 120,
        [switch] $RevertToDefault,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('DelayMilliseconds')) {
        $parameters['DelayMilliseconds'] = $DelayMilliseconds
    }
    if ($RevertToDefault.IsPresent) {
        $parameters['RevertToDefault'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-menu-show-delay.ps1' -Parameters $parameters
}

function Set-TidyWindowAnimation {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('Disable', 'Enable')]
        [string] $AnimationState = 'Disable',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('AnimationState')) {
        $parameters['AnimationState'] = $AnimationState
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-window-animation.ps1' -Parameters $parameters
}

function Set-TidyVisualEffectsProfile {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('Balanced', 'Performance', 'Appearance', 'Default')]
        [string] $Profile = 'Balanced',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('Profile')) {
        $parameters['Profile'] = $Profile
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-visual-effects.ps1' -Parameters $parameters
}

function Set-TidyPrefetchingMode {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('SsdRecommended', 'Default', 'Disabled')]
        [string] $Mode = 'SsdRecommended',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('Mode')) {
        $parameters['Mode'] = $Mode
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'manage-prefetching.ps1' -Parameters $parameters
}

function Set-TidyTelemetryLevel {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('Security', 'Basic', 'Enhanced', 'Full')]
        [string] $Level = 'Security',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('Level')) {
        $parameters['Level'] = $Level
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-telemetry-level.ps1' -Parameters $parameters
}

function Set-TidyCortanaPolicy {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-cortana-policy.ps1' -Parameters $parameters
}

function Set-TidyNetworkLatencyProfile {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Revert,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Revert.IsPresent) {
        $parameters['Revert'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'tune-network-latency.ps1' -Parameters $parameters
}

function Set-TidySysMainState {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'stop-sysmain.ps1' -Parameters $parameters
}

function Set-TidyLowDiskAlertPolicy {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $DisableAlerts,
        [switch] $EnableAlerts,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($DisableAlerts.IsPresent) {
        $parameters['DisableAlerts'] = $true
    }
    if ($EnableAlerts.IsPresent) {
        $parameters['EnableAlerts'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'hide-low-disk-alerts.ps1' -Parameters $parameters
}

function Set-TidyAutoRestartSignOn {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'configure-auto-restart-sign-on.ps1' -Parameters $parameters
}

function Set-TidyAutoEndTasks {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'auto-end-tasks.ps1' -Parameters $parameters
}

function Set-TidyHungAppTimeouts {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateRange(1000, 20000)]
        [int] $HungAppTimeout = 5000,
        [ValidateRange(1000, 20000)]
        [int] $WaitToKillAppTimeout = 5000,
        [switch] $Revert,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('HungAppTimeout')) {
        $parameters['HungAppTimeout'] = $HungAppTimeout
    }
    if ($PSBoundParameters.ContainsKey('WaitToKillAppTimeout')) {
        $parameters['WaitToKillAppTimeout'] = $WaitToKillAppTimeout
    }
    if ($Revert.IsPresent) {
        $parameters['Revert'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'adjust-hung-app-timeouts.ps1' -Parameters $parameters
}

function Set-TidyLockWorkstationPolicy {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'toggle-lock-workstation.ps1' -Parameters $parameters
}

Export-ModuleMember -Function Convert-TidyLogMessage, Write-TidyLog, Get-TidyCommandPath, Get-TidyWingetInstalledVersion, Get-TidyChocoInstalledVersion, Get-TidyScoopInstalledVersion, Get-TidyInstalledPackageVersion, Assert-TidyAdmin, Set-TidyMenuShowDelay, Set-TidyWindowAnimation, Set-TidyVisualEffectsProfile, Set-TidyPrefetchingMode, Set-TidyTelemetryLevel, Set-TidyCortanaPolicy, Set-TidyNetworkLatencyProfile, Set-TidySysMainState, Set-TidyLowDiskAlertPolicy, Set-TidyAutoRestartSignOn, Set-TidyAutoEndTasks, Set-TidyHungAppTimeouts, Set-TidyLockWorkstationPolicy
