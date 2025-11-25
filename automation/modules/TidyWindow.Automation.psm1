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

function Select-TidyBestVersion {
    # Picks the highest semantic-looking version value from a set of candidates.
    [CmdletBinding()]
    param(
        [Parameter()]
        [System.Collections.IEnumerable] $Values
    )

    if (-not $Values) {
        return $null
    }

    $unique = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $best = $null
    $bestVersion = $null

    foreach ($value in $Values) {
        if ($null -eq $value) {
            continue
        }

        $text = $value.ToString()
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        $trimmed = $text.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        if (-not $unique.Add($trimmed)) {
            continue
        }

        $match = [System.Text.RegularExpressions.Regex]::Match($trimmed, '([0-9A-Za-z]+(?:[\._\-+][0-9A-Za-z]+)*)')
        $candidateValue = if ($match.Success) { $match.Groups[1].Value.Trim() } else { $trimmed }

        if (-not [string]::IsNullOrWhiteSpace($candidateValue)) {
            $normalized = $candidateValue.Replace('_', '.').Replace('-', '.')
            while ($normalized.Contains('..')) {
                $normalized = $normalized.Replace('..', '.')
            }

            $parsed = $null
            if ([version]::TryParse($normalized, [ref]$parsed)) {
                if (($bestVersion -eq $null) -or ($parsed -gt $bestVersion)) {
                    $bestVersion = $parsed
                    $best = $candidateValue
                }

                continue
            }
        }

        if (-not $best) {
            $best = $candidateValue
            if (-not $best) {
                $best = $trimmed
            }
        }
    }

    if (-not $best) {
        foreach ($value in $Values) {
            if ($null -eq $value) { continue }
            $text = $value.ToString()
            if ([string]::IsNullOrWhiteSpace($text)) { continue }
            $trimmed = $text.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
            return $trimmed
        }
    }

    return $best
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

function Get-TidyWingetMsixCandidates {
    # Returns MSIX/Appx package identifiers that appear to match a winget package id.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    $results = [System.Collections.Generic.List[pscustomobject]]::new()

    if ([string]::IsNullOrWhiteSpace($PackageId)) {
        return $results
    }

    $appxCommand = Get-Command -Name 'Get-AppxPackage' -ErrorAction SilentlyContinue
    if (-not $appxCommand) {
        return $results
    }

    $patterns = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    [void]$patterns.Add($PackageId)
    [void]$patterns.Add("*$PackageId")

    $segments = $PackageId.Split('.', [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($segments.Length -gt 1) {
        $suffix = '.' + ([string]::Join('.', $segments[1..($segments.Length - 1)]))
        [void]$patterns.Add("*$suffix")
        if ($segments.Length -gt 2) {
            $tail = [string]::Join('.', $segments[($segments.Length - 2)..($segments.Length - 1)])
            if (-not [string]::IsNullOrWhiteSpace($tail)) {
                [void]$patterns.Add("*.$tail")
            }
        }
    }

    $collected = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($pattern in $patterns) {
        foreach ($scope in @($false, $true)) {
            try {
                $arguments = @{ Name = $pattern; ErrorAction = 'Stop' }
                if ($scope) {
                    $arguments['AllUsers'] = $true
                }

                $packages = Get-AppxPackage @arguments
            }
            catch {
                continue
            }

            foreach ($pkg in @($packages)) {
                if ($null -eq $pkg) { continue }
                $fullName = $pkg.PackageFullName
                if ([string]::IsNullOrWhiteSpace($fullName)) { continue }
                if (-not $collected.Add($fullName)) { continue }

                $versionString = $null
                try {
                    if ($pkg.Version) {
                        $versionString = $pkg.Version.ToString()
                    }
                }
                catch {
                    $versionString = $null
                }

                $results.Add([pscustomobject]@{
                    Identifier = "MSIX\$fullName"
                    Version     = if ([string]::IsNullOrWhiteSpace($versionString)) { $null } else { $versionString }
                })
            }
        }
    }

    return $results
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

    $candidates = [System.Collections.Generic.List[string]]::new()

    try {
        $jsonOutput = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' '--output' 'json' 2>$null
        if ($LASTEXITCODE -eq 0 -and $jsonOutput) {
            $payload = [string]::Join([Environment]::NewLine, $jsonOutput)
            if (-not [string]::IsNullOrWhiteSpace($payload)) {
                $data = ConvertFrom-Json -InputObject $payload -ErrorAction Stop
                if ($data -is [System.Collections.IEnumerable]) {
                    foreach ($entry in $data) {
                        if ($null -eq $entry) { continue }
                        $identifier = $entry.PackageIdentifier
                        if (-not $identifier) { $identifier = $entry.Id }
                        if ($identifier -and -not [string]::Equals($identifier.ToString(), $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                            continue
                        }

                        $installed = $entry.InstalledVersion
                        if (-not $installed) { $installed = $entry.Version }
                        if ($installed) {
                            $value = $installed.ToString()
                            if (-not [string]::IsNullOrWhiteSpace($value)) {
                                [void]$candidates.Add($value.Trim())
                            }
                        }
                    }
                }
                elseif ($data) {
                    $installed = $data.InstalledVersion
                    if (-not $installed) { $installed = $data.Version }
                    if ($installed) {
                        $value = $installed.ToString()
                        if (-not [string]::IsNullOrWhiteSpace($value)) {
                            [void]$candidates.Add($value.Trim())
                        }
                    }
                }
            }
        }
    }
    catch {
        # Continue with text parsing fallback if JSON output is not available.
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
                    [void]$candidates.Add($candidate)
                }
            }
        }
    }
    catch {
        # No further fallback available.
    }

    if ($candidates.Count -eq 0) {
        try {
            $msixCandidates = Get-TidyWingetMsixCandidates -PackageId $PackageId
            foreach ($entry in @($msixCandidates)) {
                if ($null -eq $entry) { continue }
                $value = $entry.Version
                if ([string]::IsNullOrWhiteSpace($value) -and $entry.Identifier) {
                    $match = [System.Text.RegularExpressions.Regex]::Match($entry.Identifier, '_(?<ver>[0-9]+(?:\.[0-9A-Za-z]+)+)_', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                    if ($match.Success) {
                        $value = $match.Groups['ver'].Value
                    }
                }

                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    [void]$candidates.Add($value.Trim())
                }
            }
        }
        catch {
            # Ignore MSIX probing failures and fall through to the default logic.
        }
    }

    if ($candidates.Count -eq 0) {
        return $null
    }

    return (Select-TidyBestVersion -Values $candidates)
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

    $installedCandidates = [System.Collections.Generic.List[string]]::new()
    $otherCandidates = [System.Collections.Generic.List[string]]::new()

    $addCandidate = ({
        param(
            [string] $Value,
            [bool] $IsInstalledHint
        )

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


        $target = $null
        if ($IsInstalledHint) {
            $target = $installedCandidates
        }
        else {
            $target = $otherCandidates
        }

        if ($null -eq $target) {
            return
        }

        [void]$target.Add($trimmed)
    }).GetNewClosure()

    try {
        $output = & $exe 'info' $PackageId 2>$null
        if ($LASTEXITCODE -eq 0 -and $output) {
            foreach ($entry in $output) {
                if ($null -eq $entry) {
                    continue
                }

                if ($entry -is [pscustomobject]) {
                    $candidate = $null
                    $isInstalled = $false
                    if ($entry.PSObject.Properties.Match('Installed')) { $candidate = $entry.Installed; $isInstalled = $true }
                    elseif ($entry.PSObject.Properties.Match('installed')) { $candidate = $entry.installed; $isInstalled = $true }
                    elseif ($entry.PSObject.Properties.Match('Version')) { $candidate = $entry.Version }
                    elseif ($entry.PSObject.Properties.Match('version')) { $candidate = $entry.version }

                    if ($null -ne $candidate) {
                        & $addCandidate -Value ($candidate.ToString()) -IsInstalledHint:$isInstalled
                    }

                    continue
                }

                $text = $entry.ToString()
                if ([string]::IsNullOrWhiteSpace($text)) {
                    continue
                }

                if ($text -match '^Installed\s*:\s*(?<ver>.+)$') {
                    & $addCandidate -Value $matches['ver'] -IsInstalledHint:$true
                    continue
                }

                if ($text -match '^Version\s*:\s*(?<ver>.+)$') {
                    & $addCandidate -Value $matches['ver'] -IsInstalledHint:$false
                    continue
                }

                if ($text -match '^Latest Version\s*:\s*(?<ver>.+)$') {
                    & $addCandidate -Value $matches['ver'] -IsInstalledHint:$false
                    continue
                }

                if ($text -match '^\s*(?<ver>[0-9][0-9A-Za-z\.\-_+]*)\s*$') {
                    & $addCandidate -Value $matches['ver'] -IsInstalledHint:$false
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
                            & $addCandidate -Value ($candidate.ToString()) -IsInstalledHint:$true
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
                    & $addCandidate -Value $match.Groups['ver'].Value -IsInstalledHint:$true
                }
            }
        }
    }
    catch {
        # No further fallback available.
    }

    $selectionPool = if ($installedCandidates.Count -gt 0) { $installedCandidates } else { $otherCandidates }

    if ($selectionPool.Count -eq 0) {
        return $null
    }

    return (Select-TidyBestVersion -Values $selectionPool)
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
        'winget' { return Get-TidyWingetInstalledVersion -PackageId $PackageId }
        'choco' { return Get-TidyChocoInstalledVersion -PackageId $PackageId }
        'chocolatey' { return Get-TidyChocoInstalledVersion -PackageId $PackageId }
        'scoop' { return Get-TidyScoopInstalledVersion -PackageId $PackageId }
        default { return $null }
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

function Resolve-TidyPath {
    [CmdletBinding()]
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    if ($expanded.StartsWith('~')) {
        $home = $env:USERPROFILE
        if (-not [string]::IsNullOrWhiteSpace($home)) {
            $expanded = $home + $expanded.Substring(1)
        }
    }

    try {
        return [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        return $expanded
    }
}

function Get-OblivionAnchorReasonSet {
    return @('InstallRoot', 'Hint', 'RegistryInstallLocation', 'PackageFamilyData', 'WindowsAppsPayload')
}

function Resolve-OblivionFullPath {
    [CmdletBinding()]
    param([string] $Path)

    $resolved = Resolve-TidyPath -Path $Path
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return $null
    }

    return $resolved.TrimEnd('\','/')
}

function Get-OblivionBlockedRootSet {
    [CmdletBinding()]
    param()

    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $candidates = @()
    if ($env:SystemRoot) { $candidates += $env:SystemRoot }
    if ($env:WINDIR) { $candidates += $env:WINDIR }
    $candidates += 'C:\Windows'
    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles 'System32')
        $candidates += (Join-Path $env:ProgramFiles 'Common Files')
        $candidates += (Join-Path $env:ProgramFiles 'WindowsApps')
    }
    $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ($pf86) {
        $candidates += $pf86
        $candidates += (Join-Path $pf86 'System32')
        $candidates += (Join-Path $pf86 'Common Files')
    }

    foreach ($candidate in $candidates) {
        $normalized = Resolve-OblivionFullPath -Path $candidate
        if ($normalized) { $null = $set.Add($normalized) }
    }

    return $set
}

function Get-OblivionTrustedRoots {
    [CmdletBinding()]
    param([psobject] $App)

    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if (-not $App) { return $set }

    $addRoot = {
        param([string] $Value)

        if ([string]::IsNullOrWhiteSpace($Value)) { return }
        $normalized = Resolve-OblivionFullPath -Path $Value
        if (-not $normalized) { return }

        if (-not (Test-Path -LiteralPath $normalized)) {
            try {
                $parent = Split-Path -Path $normalized -Parent -ErrorAction Stop
                if ($parent) { $normalized = $parent.TrimEnd('\','/') }
            }
            catch { }
        }

        if ($normalized) { $null = $set.Add($normalized) }
    }

    if ($App.PSObject.Properties['installRoot']) {
        foreach ($root in @($App.installRoot)) { & $addRoot $root }
    }

    if ($App.PSObject.Properties['artifactHints']) {
        foreach ($hint in @($App.artifactHints)) { & $addRoot $hint }
    }

    if (
        $App.PSObject.Properties['registry'] -and
        $App.registry -and
        $App.registry.PSObject.Properties['installLocation'] -and
        $App.registry.installLocation
    ) {
        & $addRoot $App.registry.installLocation
    }

    if ($App.PSObject.Properties['packageFamilyName'] -and $App.packageFamilyName) {
        $pkg = $App.packageFamilyName.Trim()
        if ($pkg) {
            if ($env:LOCALAPPDATA) {
                $packageData = Join-Path -Path $env:LOCALAPPDATA -ChildPath (Join-Path -Path 'Packages' -ChildPath $pkg)
                & $addRoot $packageData
            }

            if ($env:ProgramFiles) {
                $windowsApps = Join-Path -Path $env:ProgramFiles -ChildPath (Join-Path -Path 'WindowsApps' -ChildPath $pkg)
                & $addRoot $windowsApps
            }
        }
    }

    return $set
}

function Test-OblivionPathUnderRoots {
    [CmdletBinding()]
    param(
        [string] $Path,
        [System.Collections.Generic.HashSet[string]] $Roots
    )

    if (-not $Path -or -not $Roots -or $Roots.Count -eq 0) { return $false }
    $normalized = Resolve-OblivionFullPath -Path $Path
    if (-not $normalized) { return $false }

    foreach ($root in $Roots) {
        if ($root -and $normalized.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-OblivionPathBlocked {
    [CmdletBinding()]
    param(
        [string] $Path,
        [System.Collections.Generic.HashSet[string]] $BlockedRoots
    )

    if (-not $Path -or -not $BlockedRoots -or $BlockedRoots.Count -eq 0) { return $false }

    $normalized = Resolve-OblivionFullPath -Path $Path
    if (-not $normalized) { return $false }

    foreach ($blocked in $BlockedRoots) {
        if ($blocked -and $normalized.StartsWith($blocked, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
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

function Find-TidyRelatedProcesses {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $App,
        [psobject[]] $Snapshot,
        [int] $MaxMatches = 25
    )

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    if (-not $Snapshot) {
        return $results
    }

    $maxMatches = [math]::Max(0, $MaxMatches)
    $trustedRoots = Get-OblivionTrustedRoots -App $App
    $blockedRoots = Get-OblivionBlockedRootSet

    $hintPathSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $hintNameSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if ($App.PSObject.Properties['processHints']) {
        foreach ($hint in @($App.processHints)) {
            if ([string]::IsNullOrWhiteSpace($hint)) { continue }
            if ($hint -match '[\\/]' -or $hint.Contains(':')) {
                $normalizedHint = Resolve-OblivionFullPath -Path $hint
                if ($normalizedHint) { $null = $hintPathSet.Add($normalizedHint) }
            }
            else {
                $null = $hintNameSet.Add($hint.ToLowerInvariant())
            }
        }
    }

    foreach ($proc in $Snapshot) {
        if ($results.Count -ge $maxMatches) { break }
        if (-not $proc) { continue }

        $procPath = $null
        if ($proc.PSObject.Properties['path']) {
            $procPath = $proc.path
        }

        $normalizedPath = if ($procPath) { Resolve-OblivionFullPath -Path $procPath } else { $null }
        if ($normalizedPath -and (Test-OblivionPathBlocked -Path $normalizedPath -BlockedRoots $blockedRoots)) {
            continue
        }

        $matched = $false
        if ($normalizedPath -and ($trustedRoots.Count -gt 0) -and (Test-OblivionPathUnderRoots -Path $normalizedPath -Roots $trustedRoots)) {
            $matched = $true
        }
        elseif ($normalizedPath -and $hintPathSet.Count -gt 0) {
            foreach ($hintPath in $hintPathSet) {
                if ($normalizedPath.StartsWith($hintPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $matched = $true
                    break
                }
            }
        }

        if (-not $matched -and $hintNameSet.Count -gt 0 -and $proc.PSObject.Properties['name']) {
            $procName = $proc.name
            if (-not [string]::IsNullOrWhiteSpace($procName)) {
                if ($hintNameSet.Contains($procName.ToLowerInvariant())) {
                    $matched = $true
                }
            }
        }

        if ($matched) {
            $results.Add($proc) | Out-Null
        }
    }

    return $results
}

function Stop-TidyProcesses {
    [CmdletBinding()]
    param(
        [psobject[]] $Processes,
        [switch] $DryRun,
        [switch] $Force
    )

    if (-not $Processes -or $Processes.Count -eq 0) {
        return 0
    }

    $stopped = 0
    foreach ($proc in $Processes) {
        if ($DryRun) {
            $stopped++
            continue
        }

        try {
            Stop-Process -Id $proc.id -Force:$Force -ErrorAction Stop
            $stopped++
        }
        catch {
            Write-TidyLog -Level 'Warning' -Message "Failed to stop process $($proc.name) ($($proc.id)): $($_.Exception.Message)"
        }
    }

    return $stopped
}

function Test-OblivionProcessAlive {
    [CmdletBinding()]
    param([int] $ProcessId)

    if ($ProcessId -le 0) {
        return $false
    }

    try {
        $proc = Get-Process -Id $ProcessId -ErrorAction Stop
        return -not $proc.HasExited
    }
    catch {
        return $false
    }
}

function Invoke-OblivionProcessTermination {
    [CmdletBinding()]
    param([psobject] $Process)

    if (-not $Process -or -not $Process.id) {
        return [pscustomobject]@{ success = $true; strategy = 'NoProcess'; error = $null }
    }

    $id = [int]$Process.id
    $strategies = @(
        @{ name = 'CloseMainWindow'; action = {
                $target = Get-Process -Id $id -ErrorAction Stop
                if ($target.HasExited) { return $true }
                if ($target.MainWindowHandle -eq 0) { return $false }
                if (-not $target.CloseMainWindow()) { return $false }
                try { Wait-Process -Id $id -Timeout 5 -ErrorAction SilentlyContinue } catch { }
                return -not (Test-OblivionProcessAlive -ProcessId $id)
            }
        },
        @{ name = 'StopProcess'; action = {
                Stop-Process -Id $id -ErrorAction Stop
                return -not (Test-OblivionProcessAlive -ProcessId $id)
            }
        },
        @{ name = 'StopProcessForce'; action = {
                Stop-Process -Id $id -Force -ErrorAction Stop
                return -not (Test-OblivionProcessAlive -ProcessId $id)
            }
        },
        @{ name = 'TaskKill'; action = {
                $arguments = @('/PID', $id, '/T', '/F')
                $null = & taskkill.exe @arguments 2>$null 1>$null
                if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 128) {
                    throw "taskkill.exe exited with code $LASTEXITCODE"
                }
                return -not (Test-OblivionProcessAlive -ProcessId $id)
            }
        },
        @{ name = 'CimTerminate'; action = {
                $terminate = Invoke-CimMethod -ClassName Win32_Process -MethodName Terminate -Arguments @{ ProcessId = $id } -ErrorAction Stop
                if ($terminate.ReturnValue -ne 0 -and $terminate.ReturnValue -ne 2) {
                    throw "Win32_Process.Terminate returned $($terminate.ReturnValue)"
                }
                return -not (Test-OblivionProcessAlive -ProcessId $id)
            }
        }
    )

    $lastError = $null
    foreach ($strategy in $strategies) {
        try {
            if (& $strategy.action) {
                return [pscustomobject]@{ success = $true; strategy = $strategy.name; error = $null }
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }
    }

    return [pscustomobject]@{ success = $false; strategy = 'Exhausted'; error = $lastError }
}

function Invoke-OblivionProcessSweep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $App,
        [switch] $DryRun,
        [int] $MaxPasses = 3,
        [int] $WaitSeconds = 3
    )

    $snapshot = Get-TidyProcessSnapshot
    $related = Find-TidyRelatedProcesses -App $App -Snapshot $snapshot -MaxMatches 200

    $detected = if ($related) { $related.Count } else { 0 }
    $resultLog = New-Object 'System.Collections.Generic.List[psobject]'
    $stopped = 0
    $attempts = 0

    if (-not $DryRun -and $detected -gt 0) {
        Assert-TidyAdmin
    }

    if ($DryRun -or $detected -eq 0) {
        return [pscustomobject]@{
            Detected      = $detected
            Stopped       = $detected
            Attempts      = [math]::Max(1, $MaxPasses)
            Remaining     = @()
            AttemptLog    = $resultLog
        }
    }

    $active = @($related)
    while ($active.Count -gt 0 -and $attempts -lt [math]::Max(1, $MaxPasses)) {
        $attempts++
        $nextActive = @()
        foreach ($proc in $active) {
            $termination = Invoke-OblivionProcessTermination -Process $proc
            $record = [pscustomobject]@{
                attempt   = $attempts
                processId = $proc.id
                name      = $proc.name
                success   = $termination.success
                strategy  = $termination.strategy
                error     = $termination.error
            }
            $resultLog.Add($record) | Out-Null

            if ($termination.success) {
                $stopped++
            }
            else {
                if (Test-OblivionProcessAlive -ProcessId $proc.id) {
                    try {
                        $live = Get-Process -Id $proc.id -ErrorAction Stop
                        $path = $proc.path
                        try { $path = $live.Path } catch { }
                        $nextActive += [pscustomobject]@{ id = $live.Id; name = $live.ProcessName; path = $path }
                    }
                    catch {
                        $nextActive += $proc
                    }
                }
            }
        }

        if ($nextActive.Count -eq 0) {
            break
        }

        $active = $nextActive
        if ($attempts -lt $MaxPasses) {
            Start-Sleep -Seconds ([math]::Max(1, $WaitSeconds))
        }
    }

    return [pscustomobject]@{
        Detected   = $detected
        Stopped    = $stopped
        Attempts   = $attempts
        Remaining  = $active
        AttemptLog = $resultLog
    }
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

function New-OblivionArtifactId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Type,
        [Parameter(Mandatory = $true)][string] $Path,
        [string] $Reason
    )

    $typeKey = if ($Type) { $Type.Trim().ToLowerInvariant() } else { [string]::Empty }
    $normalizedPath = $null
    try {
        $normalizedPath = Resolve-OblivionFullPath -Path $Path
    }
    catch {
        $normalizedPath = $null
    }

    if ([string]::IsNullOrWhiteSpace($normalizedPath)) {
        $normalizedPath = if ($Path) { $Path } else { [string]::Empty }
    }

    $reasonKey = if ([string]::IsNullOrWhiteSpace($Reason)) { [string]::Empty } else { $Reason.Trim().ToLowerInvariant() }
    $input = "$typeKey::$normalizedPath::$reasonKey"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($input)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    $hex = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })
    if ($hex.Length -gt 32) {
        return $hex.Substring(0, 32)
    }

    return $hex
}

function New-TidyFileArtifact {
    [CmdletBinding()]
    param(
        [string] $Path,
        [string] $Reason,
        [bool] $DefaultSelected = $true,
        [string] $Confidence = 'anchor',
        [string] $SourceAnchor,
        [hashtable] $Metadata
    )

    $resolved = Resolve-TidyPath -Path $Path
    if ([string]::IsNullOrWhiteSpace($resolved) -or -not (Test-Path -LiteralPath $resolved)) {
        return $null
    }

    $item = Get-Item -LiteralPath $resolved -ErrorAction SilentlyContinue
    if (-not $item) { return $null }

    $isDirectory = $item.PSIsContainer
    $size = if ($isDirectory) { Measure-TidyDirectoryBytes -Path $resolved } else { $item.Length }

    $programFiles = $env:ProgramFiles
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $requiresElevation = $false
    if ($programFiles -and $resolved.StartsWith($programFiles, [System.StringComparison]::OrdinalIgnoreCase)) {
        $requiresElevation = $true
    }
    elseif ($programFilesX86 -and $resolved.StartsWith($programFilesX86, [System.StringComparison]::OrdinalIgnoreCase)) {
        $requiresElevation = $true
    }

    $metadataPayload = [ordered]@{ reason = $Reason }
    if ($Confidence) { $metadataPayload['confidence'] = $Confidence }
    if ($SourceAnchor) { $metadataPayload['sourceAnchor'] = $SourceAnchor }
    if ($Metadata) {
        foreach ($key in $Metadata.Keys) {
            $metadataPayload[$key] = $Metadata[$key]
        }
    }

    $artifactId = New-OblivionArtifactId -Type ($isDirectory ? 'Directory' : 'File') -Path $resolved -Reason $Reason
    if ([string]::IsNullOrWhiteSpace($artifactId)) {
        $artifactId = New-TidyArtifactId
    }

    return [pscustomobject]@{
        id                = $artifactId
        type              = $isDirectory ? 'Directory' : 'File'
        group             = 'Files'
        path              = $resolved
        displayName       = Split-Path -Path $resolved -Leaf
        sizeBytes         = $size
        defaultSelected   = $DefaultSelected
        requiresElevation = $requiresElevation
        metadata          = $metadataPayload
    }
}

function New-TidyRegistryArtifact {
    [CmdletBinding()]
    param([string] $KeyPath)

    $providerPath = ConvertTo-TidyRegistryPath -KeyPath $KeyPath
    if (-not $providerPath) { return $null }

    $artifactId = New-OblivionArtifactId -Type 'Registry' -Path $providerPath -Reason 'UninstallKey'
    if ([string]::IsNullOrWhiteSpace($artifactId)) {
        $artifactId = New-TidyArtifactId
    }

    return [pscustomobject]@{
        id                = $artifactId
        type              = 'Registry'
        group             = 'Registry'
        path              = $providerPath
        displayName       = $KeyPath
        sizeBytes         = 0
        defaultSelected   = $true
        requiresElevation = $providerPath -like 'Registry::HKEY_LOCAL_MACHINE*'
        metadata          = @{ reason = 'UninstallKey' }
    }
}

function New-TidyServiceArtifact {
    [CmdletBinding()]
    param([string] $ServiceName)

    if ([string]::IsNullOrWhiteSpace($ServiceName)) { return $null }

    $artifactId = New-OblivionArtifactId -Type 'Service' -Path $ServiceName -Reason 'ServiceHint'
    if ([string]::IsNullOrWhiteSpace($artifactId)) {
        $artifactId = New-TidyArtifactId
    }

    return [pscustomobject]@{
        id                = $artifactId
        type              = 'Service'
        group             = 'Services'
        path              = $ServiceName
        displayName       = $ServiceName
        sizeBytes         = 0
        defaultSelected   = $true
        requiresElevation = $true
        metadata          = @{ reason = 'ServiceHint' }
    }
}

function Get-TidyCandidateDataFolders {
    [CmdletBinding()]
    param([psobject] $App)

    $results = @()
    $name = $App.name
    if ([string]::IsNullOrWhiteSpace($name)) { return $results }

    $safe = [System.Text.RegularExpressions.Regex]::Replace($name, '[^A-Za-z0-9]', '')
    if ([string]::IsNullOrWhiteSpace($safe)) { return $results }

    $roots = @()
    if ($env:ProgramData) { $roots += $env:ProgramData }
    else { $roots += 'C:\ProgramData' }
    if ($env:LOCALAPPDATA) { $roots += $env:LOCALAPPDATA }
    if ($env:APPDATA) { $roots += $env:APPDATA }

    foreach ($root in $roots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $candidate = Join-Path -Path $root -ChildPath $safe
        if (Test-Path -LiteralPath $candidate) {
            $results += $candidate
        }
    }

    return $results
}

function Get-TidyArtifacts {
    [CmdletBinding()]
    param([psobject] $App)

    $artifacts = New-Object 'System.Collections.Generic.List[psobject]'

    $primaryInstallRoot = $null
    if ($App.PSObject.Properties['installRoot']) {
        $primaryInstallRoot = $App.installRoot
        foreach ($root in @($primaryInstallRoot)) {
            $artifact = New-TidyFileArtifact -Path $root -Reason 'InstallRoot' -SourceAnchor $root
            if ($artifact) { $artifacts.Add($artifact) | Out-Null }
        }
    }

    if ($App.PSObject.Properties['artifactHints']) {
        foreach ($hint in @($App.artifactHints)) {
            if ([string]::IsNullOrWhiteSpace($hint)) { continue }
            if ($primaryInstallRoot -and [string]::Equals($hint, $primaryInstallRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            $artifact = New-TidyFileArtifact -Path $hint -Reason 'Hint' -SourceAnchor $hint
            if ($artifact) { $artifacts.Add($artifact) | Out-Null }
        }
    }

    foreach ($folder in Get-TidyCandidateDataFolders -App $App) {
        $artifact = New-TidyFileArtifact -Path $folder -Reason 'DataFolder' -DefaultSelected:$false -Confidence 'heuristic'
        if ($artifact) { $artifacts.Add($artifact) | Out-Null }
    }

    if (
        $App.PSObject.Properties['registry'] -and
        $App.registry -and
        $App.registry.PSObject -and
        $App.registry.PSObject.Properties['keyPath'] -and
        $App.registry.keyPath
    ) {
        $artifact = New-TidyRegistryArtifact -KeyPath $App.registry.keyPath
        if ($artifact) { $artifacts.Add($artifact) | Out-Null }
    }

    if ($App.PSObject.Properties['serviceHints']) {
        foreach ($svc in @($App.serviceHints)) {
            $artifact = New-TidyServiceArtifact -ServiceName $svc
            if ($artifact) { $artifacts.Add($artifact) | Out-Null }
        }
    }

    return $artifacts
}

function Invoke-OblivionArtifactDiscovery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $App,
        [psobject[]] $ProcessSnapshot,
        [int] $MaxProgramFilesMatches = 15
    )

    $pathSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $artifacts = New-Object 'System.Collections.Generic.List[psobject]'
    $anchorReasonSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($reason in Get-OblivionAnchorReasonSet) {
        $null = $anchorReasonSet.Add($reason)
    }

    $trustedAnchorDirectories = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $anchorTokenSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $blockedRootSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    $addBlockedRoot = {
        param([string] $Path)

        if ([string]::IsNullOrWhiteSpace($Path)) { return }
        $resolved = Resolve-TidyPath -Path $Path
        if ([string]::IsNullOrWhiteSpace($resolved)) { return }
        $resolved = $resolved.TrimEnd('\','/')
        if ($resolved) { $null = $blockedRootSet.Add($resolved) }
    }

    & $addBlockedRoot $env:SystemRoot
    & $addBlockedRoot $env:WINDIR
    & $addBlockedRoot 'C:\Windows'
    if ($env:ProgramFiles) {
        & $addBlockedRoot (Join-Path -Path $env:ProgramFiles -ChildPath 'Common Files')
        & $addBlockedRoot (Join-Path -Path $env:ProgramFiles -ChildPath 'WindowsApps')
    }
    $pf86Root = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ($pf86Root) {
        & $addBlockedRoot (Join-Path -Path $pf86Root -ChildPath 'Common Files')
    }

    $normalizeDirectory = {
        param([string] $Path)

        if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
        $resolved = Resolve-TidyPath -Path $Path
        if ([string]::IsNullOrWhiteSpace($resolved)) { return $null }
        $candidate = $resolved.TrimEnd('\','/')
        if (-not $candidate) { return $null }

        try {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $candidate = Split-Path -Path $candidate -Parent -ErrorAction Stop
            }
        }
        catch {
            return $null
        }

        return $candidate.TrimEnd('\','/')
    }

    $isUnderBlockedRoot = {
        param([string] $Path)

        if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
        foreach ($blocked in $blockedRootSet) {
            if ($blocked -and $Path.StartsWith($blocked, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        return $false
    }

    $addAnchorTokens = {
        param([string] $Path)

        if ([string]::IsNullOrWhiteSpace($Path)) { return }
        try {
            $leaf = Split-Path -Path $Path -Leaf -ErrorAction Stop
        }
        catch {
            $leaf = $Path
        }

        $normalized = [System.Text.RegularExpressions.Regex]::Replace($leaf, '[^A-Za-z0-9]+', ' ')
        foreach ($token in $normalized.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)) {
            if ($token.Length -lt 3) { continue }
            $null = $anchorTokenSet.Add($token)
        }
    }

    $recordAnchor = {
        param([string] $Path)

        $anchorDir = & $normalizeDirectory $Path
        if (-not $anchorDir) { return }
        $null = $trustedAnchorDirectories.Add($anchorDir)
        & $addAnchorTokens $anchorDir
        try {
            $parent = Split-Path -Path $anchorDir -Parent -ErrorAction Stop
            if ($parent) { & $addAnchorTokens $parent }
        }
        catch { }
    }

    $getAnchorForPath = {
        param([string] $Path)

        if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
        foreach ($anchor in $trustedAnchorDirectories) {
            if ($Path.StartsWith($anchor, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $anchor
            }
        }

        return $null
    }

    $getAnchoredScopes = {
        param([string] $Root)

        if ([string]::IsNullOrWhiteSpace($Root)) { return @() }
        $scopes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($anchor in $trustedAnchorDirectories) {
            if (-not $anchor.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            $relative = $anchor.Substring($Root.Length).TrimStart('\','/')
            if (-not $relative) { continue }
            $segments = $relative.Split('\', [System.StringSplitOptions]::RemoveEmptyEntries)
            if ($segments.Length -eq 0) { continue }
            $scope = Join-Path -Path $Root -ChildPath $segments[0]
            $null = $scopes.Add($scope)
        }

        return @($scopes)
    }
    foreach ($baseArtifact in @(Get-TidyArtifacts -App $App)) {
        if (-not $baseArtifact) { continue }

        $artifacts.Add($baseArtifact) | Out-Null
        if ($baseArtifact.path) {
            $null = $pathSet.Add($baseArtifact.path)
            if (
                $baseArtifact.metadata -and
                $baseArtifact.metadata.reason -and
                $anchorReasonSet.Contains($baseArtifact.metadata.reason)
            ) {
                & $recordAnchor $baseArtifact.path
            }
        }
    }

    $added = 0
    $details = New-Object 'System.Collections.Generic.List[psobject]'
    $maxMatches = [math]::Max(0, $MaxProgramFilesMatches)

    $addArtifact = {
        param(
            [string] $Kind,
            [string] $Path,
            [string] $Reason,
            [switch] $IsCandidate,
            [string] $SourceAnchor
        )

        if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

        $confidence = if ($IsCandidate) { 'heuristic' } else { 'anchor' }
        $candidate = switch ($Kind) {
            'Registry' { New-TidyRegistryArtifact -KeyPath $Path }
            'Service'  { New-TidyServiceArtifact -ServiceName $Path }
            Default    { New-TidyFileArtifact -Path $Path -Reason $Reason -DefaultSelected:(!$IsCandidate) -Confidence $confidence -SourceAnchor $SourceAnchor }
        }

        if (-not $candidate -or -not $candidate.path) { return $null }
        if ($IsCandidate -and (& $isUnderBlockedRoot $candidate.path)) { return $null }
        if ($pathSet.Contains($candidate.path)) { return $null }

        $null = $pathSet.Add($candidate.path)
        $artifacts.Add($candidate) | Out-Null
        $details.Add([pscustomobject]@{
            path         = $candidate.path
            type         = $candidate.type
            reason       = $Reason
            confidence   = $confidence
            sourceAnchor = $SourceAnchor
        }) | Out-Null
        Set-Variable -Name 'added' -Scope 1 -Value ($added + 1)
        if (-not $IsCandidate -and $anchorReasonSet.Contains($Reason)) {
            & $recordAnchor $candidate.path
        }

        return $candidate
    }

    if (
        $App.PSObject.Properties['registry'] -and
        $App.registry -and
        $App.registry.PSObject -and
        $App.registry.PSObject.Properties['installLocation'] -and
        $App.registry.installLocation
    ) {
        $null = & $addArtifact 'File' $App.registry.installLocation 'RegistryInstallLocation' -SourceAnchor $App.registry.installLocation
    }

    if (
        $App.PSObject.Properties['registry'] -and
        $App.registry -and
        $App.registry.PSObject -and
        $App.registry.PSObject.Properties['displayIcon'] -and
        $App.registry.displayIcon
    ) {
        $displayIcon = $App.registry.displayIcon.Split(',')[0]
        try {
            $parent = Split-Path -Path $displayIcon -Parent -ErrorAction Stop
            $null = & $addArtifact 'File' $parent 'RegistryDisplayIcon' -SourceAnchor $parent
        }
        catch {
            $null = & $addArtifact 'File' $displayIcon 'RegistryDisplayIcon' -SourceAnchor $displayIcon
        }
    }

    if ($App.PSObject.Properties['packageFamilyName'] -and $App.packageFamilyName) {
        $pkg = $App.packageFamilyName.Trim()
        if ($pkg) {
            $localPackages = if ($env:LOCALAPPDATA) { Join-Path -Path $env:LOCALAPPDATA -ChildPath (Join-Path -Path 'Packages' -ChildPath $pkg) } else { $null }
            if ($localPackages) { $null = & $addArtifact 'File' $localPackages 'PackageFamilyData' -SourceAnchor $localPackages }

            $windowsApps = if ($env:ProgramFiles) { Join-Path -Path $env:ProgramFiles -ChildPath (Join-Path -Path 'WindowsApps' -ChildPath $pkg) } else { $null }
            if ($windowsApps) { $null = & $addArtifact 'File' $windowsApps 'WindowsAppsPayload' -SourceAnchor $windowsApps }
        }
    }

    if (-not $ProcessSnapshot) {
        $ProcessSnapshot = Get-TidyProcessSnapshot
    }
    $related = Find-TidyRelatedProcesses -App $App -Snapshot $ProcessSnapshot -MaxMatches 200
    foreach ($proc in $related) {
        if (-not $proc.path) { continue }
        try {
            $parent = Split-Path -Path $proc.path -Parent -ErrorAction Stop
        }
        catch {
            continue
        }

        if (-not $parent) { continue }
        $anchorMatch = & $getAnchorForPath $parent
        if (-not $anchorMatch) { continue }
        $null = & $addArtifact 'File' $parent 'ProcessImageDirectory' -IsCandidate -SourceAnchor $anchorMatch
    }

    $tokens = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $primaryTokens = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    $noiseTokens = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($noise in @('microsoft', 'windows', 'corporation', 'software', 'installer', 'setup', 'update', 'utility', 'helper', 'system', 'apps', 'app', 'tools', 'suite')) {
        $null = $noiseTokens.Add($noise)
    }

    $addTokens = {
        param(
            [string] $Source,
            [System.Collections.Generic.HashSet[string][]] $TargetSets
        )

        if ([string]::IsNullOrWhiteSpace($Source) -or -not $TargetSets -or $TargetSets.Count -eq 0) { return }

        $normalized = [System.Text.RegularExpressions.Regex]::Replace($Source, '[^A-Za-z0-9]+', ' ')
        foreach ($token in $normalized.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)) {
            $trimmed = $token.Trim()
            if ($trimmed.Length -lt 3) { continue }
            if ($noiseTokens.Contains($trimmed)) { continue }
            foreach ($set in $TargetSets) {
                if ($set) { $null = $set.Add($trimmed) }
            }
        }
    }

    & $addTokens $App.name @($tokens, $primaryTokens)

    if ($App.PSObject.Properties['artifactHints']) {
        foreach ($hint in @($App.artifactHints)) {
            if ([string]::IsNullOrWhiteSpace($hint)) { continue }
            try {
                $leaf = Split-Path -Path $hint -Leaf -ErrorAction Stop
            }
            catch {
                $leaf = $hint
            }
            & $addTokens $leaf @($tokens, $primaryTokens)
        }
    }

    if ($App.PSObject.Properties['processHints']) {
        foreach ($hint in @($App.processHints)) {
            if ([string]::IsNullOrWhiteSpace($hint)) { continue }
            try {
                $leaf = Split-Path -Path $hint -Leaf -ErrorAction Stop
            }
            catch {
                $leaf = $hint
            }
            & $addTokens $leaf @($tokens, $primaryTokens)
        }
    }

    if ($App.PSObject.Properties['tags']) {
        foreach ($tag in @($App.tags)) {
            & $addTokens $tag @($tokens)
        }
    }

    & $addTokens $App.publisher @($tokens)
    & $addTokens $App.appId @($tokens)

    $directoryTokens = if ($primaryTokens.Count -gt 0) { $primaryTokens } else { $tokens }
    $heuristicTokens = if ($anchorTokenSet.Count -gt 0) { $anchorTokenSet } else { $directoryTokens }

    $scanDirectories = {
        param(
            [string] $Root,
            [string] $Reason,
            [int] $Budget,
            [System.Collections.Generic.HashSet[string]] $TokenSet,
            [string] $SourceAnchor
        )

        if (
            $Budget -le 0 -or
            -not $TokenSet -or
            $TokenSet.Count -eq 0 -or
            [string]::IsNullOrWhiteSpace($Root) -or
            -not (Test-Path -LiteralPath $Root)
        ) {
            return 0
        }

        $addedLocal = 0
        try {
            $directories = Get-ChildItem -LiteralPath $Root -Directory -ErrorAction Stop
        }
        catch {
            return 0
        }

        foreach ($dir in $directories) {
            if ($addedLocal -ge $Budget) { break }

            foreach ($token in $TokenSet) {
                if ($dir.Name.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $anchorPath = if ($SourceAnchor) { $SourceAnchor } else { $Root }
                    $artifactCandidate = & $addArtifact 'File' $dir.FullName $Reason -IsCandidate -SourceAnchor $anchorPath
                    if ($artifactCandidate) {
                        $addedLocal++
                    }
                    break
                }
            }
        }

        return $addedLocal
    }

    if ($trustedAnchorDirectories.Count -gt 0 -and $heuristicTokens.Count -gt 0 -and $maxMatches -gt 0) {
        $programRoots = @()
        if ($env:ProgramFiles) { $programRoots += $env:ProgramFiles }
        $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        if ($pf86) { $programRoots += $pf86 }

        foreach ($root in $programRoots) {
            if ($maxMatches -le 0) { break }
            $scopes = & $getAnchoredScopes $root
            foreach ($scope in $scopes) {
                if ($maxMatches -le 0) { break }
                $budget = [math]::Min(5, $maxMatches)
                $maxMatches -= & $scanDirectories $scope 'ProgramFilesHeuristic' $budget $heuristicTokens $scope
            }
        }
    }

    $userProfile = [Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)
    $localLowPath = $null
    if ($userProfile) {
        $localLowPath = Join-Path $userProfile 'AppData\LocalLow'
    }
    $profileBudgets = @(
        @{ path = $env:LOCALAPPDATA; reason = 'LocalAppDataToken'; budget = 6 },
        @{ path = $env:APPDATA; reason = 'RoamingAppDataToken'; budget = 4 },
        @{ path = $localLowPath; reason = 'LocalLowToken'; budget = 3 }
    )

    foreach ($profile in $profileBudgets) {
        if (-not $profile.path -or -not (Test-Path -LiteralPath $profile.path)) { continue }
        if ($trustedAnchorDirectories.Count -eq 0 -or $heuristicTokens.Count -eq 0) { break }

        $scopes = & $getAnchoredScopes $profile.path
        foreach ($scope in $scopes) {
            if ($profile['budget'] -le 0) { break }
            $consumed = & $scanDirectories $scope $profile.reason $profile['budget'] $heuristicTokens $scope
            $profile['budget'] -= $consumed
        }
    }

    $programDataBudgets = @()
    if ($env:ProgramData) {
        $programDataBudgets += @{ path = (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'); reason = 'StartMenuGroup'; budget = 5 }
        $programDataBudgets += @{ path = (Join-Path $env:ProgramData 'Package Cache'); reason = 'PackageCache'; budget = 4 }
    }

    foreach ($entry in $programDataBudgets) {
        if (-not $entry.path -or -not (Test-Path -LiteralPath $entry.path)) { continue }
        if ($trustedAnchorDirectories.Count -eq 0 -or $heuristicTokens.Count -eq 0) { break }

        $scopes = & $getAnchoredScopes $entry.path
        foreach ($scope in $scopes) {
            if ($entry['budget'] -le 0) { break }
            $consumed = & $scanDirectories $scope $entry.reason $entry['budget'] $heuristicTokens $scope
            $entry['budget'] -= $consumed
        }
    }

    $startMenuRoots = @()
    if ($env:ProgramData) { $startMenuRoots += (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs') }
    if ($env:APPDATA) { $startMenuRoots += (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') }

    $scanStartMenu = {
        param([string] $Root, [int] $Budget)

        if (
            $Budget -le 0 -or
            -not (Test-Path -LiteralPath $Root) -or
            $trustedAnchorDirectories.Count -eq 0
        ) {
            return
        }

        try {
            $shortcuts = Get-ChildItem -LiteralPath $Root -Filter '*.lnk' -Recurse -ErrorAction Stop
        }
        catch {
            return
        }

        $shell = $null
        foreach ($shortcut in $shortcuts) {
            if ($Budget -le 0) { break }

            $matched = $false
            $target = $null
            foreach ($token in $tokens) {
                if ($shortcut.Name.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $matched = $true
                    break
                }
            }

            if (-not $matched -or -not $target) {
                try {
                    if (-not $shell) { $shell = New-Object -ComObject WScript.Shell }
                    $target = $shell.CreateShortcut($shortcut.FullName).TargetPath
                    foreach ($token in $tokens) {
                        if ($target -and $target.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                            $matched = $true
                            break
                        }
                    }
                }
                catch {
                    $target = $null
                    if (-not $matched) { $matched = $false }
                }
            }

            if (-not $matched) { continue }

            $targetParent = $null
            if ($target) {
                try {
                    $targetParent = Split-Path -Path $target -Parent -ErrorAction Stop
                }
                catch {
                    $targetParent = $null
                }
            }

            if (-not $targetParent) { continue }
            $anchorForShortcut = & $getAnchorForPath $targetParent
            if (-not $anchorForShortcut) { continue }

            $null = & $addArtifact 'File' $shortcut.FullName 'StartMenuShortcut' -IsCandidate -SourceAnchor $anchorForShortcut
            $null = & $addArtifact 'File' $targetParent 'StartMenuShortcutTarget' -IsCandidate -SourceAnchor $anchorForShortcut
            $Budget--
        }
    }

    if ($trustedAnchorDirectories.Count -gt 0) {
        foreach ($root in $startMenuRoots) {
            & $scanStartMenu $root 6
        }
    }

    if ($App.PSObject.Properties['serviceHints']) {
        foreach ($svc in @($App.serviceHints)) {
            $null = & $addArtifact 'Service' $svc 'ServiceHint'
        }
    }

    return [pscustomobject]@{
        Artifacts = $artifacts
        AddedCount = $added
        Details = $details
    }
}

function Get-OblivionArtifactMetadataValue {
    [CmdletBinding()]
    param(
        [psobject] $Artifact,
        [string] $Key
    )

    if (-not $Artifact -or -not $Key) { return $null }
    if (-not $Artifact.PSObject.Properties['metadata']) { return $null }
    $metadata = $Artifact.metadata
    if (-not $metadata) { return $null }

    if ($metadata -is [System.Collections.IDictionary]) {
        return $metadata[$Key]
    }

    if ($metadata.PSObject -and $metadata.PSObject.Properties[$Key]) {
        return $metadata.$Key
    }

    return $null
}

function Get-OblivionArtifactAnchors {
    [CmdletBinding()]
    param([psobject] $Artifact)

    $anchors = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if (-not $Artifact) { return @() }

    $sourceAnchor = Get-OblivionArtifactMetadataValue -Artifact $Artifact -Key 'sourceAnchor'
    $sourcePath = Resolve-OblivionFullPath -Path $sourceAnchor
    if ($sourcePath) { $null = $anchors.Add($sourcePath) }

    $reason = Get-OblivionArtifactMetadataValue -Artifact $Artifact -Key 'reason'
    $anchorReasons = Get-OblivionAnchorReasonSet
    if ($reason -and ($anchorReasons -contains $reason)) {
        switch ($Artifact.type) {
            'Directory' {
                $pathAnchor = Resolve-OblivionFullPath -Path $Artifact.path
                if ($pathAnchor) { $null = $anchors.Add($pathAnchor) }
            }
            'File' {
                try {
                    $parent = Split-Path -Path $Artifact.path -Parent -ErrorAction Stop
                    $parentAnchor = Resolve-OblivionFullPath -Path $parent
                    if ($parentAnchor) { $null = $anchors.Add($parentAnchor) }
                }
                catch { }
            }
        }
    }

    return @($anchors)
}

function Test-OblivionArtifactRemovalAllowed {
    [CmdletBinding()]
    param([psobject] $Artifact)

    if (-not $Artifact) {
        return [pscustomobject]@{ allowed = $false; reason = 'Artifact missing.' }
    }

    $blockedRoots = Get-OblivionBlockedRootSet
    $type = $Artifact.type
    switch ($type) {
        'Directory' {
            $path = $Artifact.path
            if (Test-OblivionPathBlocked -Path $path -BlockedRoots $blockedRoots) {
                return [pscustomobject]@{ allowed = $false; reason = 'Path resides under a blocked system directory.' }
            }

            $anchors = Get-OblivionArtifactAnchors -Artifact $Artifact
            if (-not $anchors -or $anchors.Count -eq 0) {
                return [pscustomobject]@{ allowed = $false; reason = 'Artifact lacks a trusted anchor.' }
            }

            $normalized = Resolve-OblivionFullPath -Path $path
            foreach ($anchor in $anchors) {
                if ($normalized -and $anchor -and $normalized.StartsWith($anchor, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return [pscustomobject]@{ allowed = $true; reason = $null }
                }
            }

            return [pscustomobject]@{ allowed = $false; reason = 'Path is outside approved roots.' }
        }
        'File' {
            $path = $Artifact.path
            if (Test-OblivionPathBlocked -Path $path -BlockedRoots $blockedRoots) {
                return [pscustomobject]@{ allowed = $false; reason = 'Path resides under a blocked system directory.' }
            }

            $anchors = Get-OblivionArtifactAnchors -Artifact $Artifact
            if (-not $anchors -or $anchors.Count -eq 0) {
                return [pscustomobject]@{ allowed = $false; reason = 'Artifact lacks a trusted anchor.' }
            }

            $normalized = Resolve-OblivionFullPath -Path $path
            foreach ($anchor in $anchors) {
                if ($normalized -and $anchor -and $normalized.StartsWith($anchor, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return [pscustomobject]@{ allowed = $true; reason = $null }
                }
            }

            return [pscustomobject]@{ allowed = $false; reason = 'Path is outside approved roots.' }
        }
        'Registry' {
            $reason = Get-OblivionArtifactMetadataValue -Artifact $Artifact -Key 'reason'
            if ([string]::Equals($reason, 'UninstallKey', [System.StringComparison]::OrdinalIgnoreCase)) {
                return [pscustomobject]@{ allowed = $true; reason = $null }
            }

            $allowedPrefixes = @(
                'Registry::HKEY_LOCAL_MACHINE\SOFTWARE',
                'Registry::HKEY_CURRENT_USER\Software'
            )

            foreach ($prefix in $allowedPrefixes) {
                if ($Artifact.path -and $Artifact.path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return [pscustomobject]@{ allowed = $true; reason = $null }
                }
            }

            return [pscustomobject]@{ allowed = $false; reason = 'Registry path is outside approved hives.' }
        }
        'Service' {
            $reason = Get-OblivionArtifactMetadataValue -Artifact $Artifact -Key 'reason'
            if ([string]::Equals($reason, 'ServiceHint', [System.StringComparison]::OrdinalIgnoreCase)) {
                return [pscustomobject]@{ allowed = $true; reason = $null }
            }

            return [pscustomobject]@{ allowed = $false; reason = 'Service not whitelisted for removal.' }
        }
        Default {
            return [pscustomobject]@{ allowed = $false; reason = 'Unsupported artifact type.' }
        }
    }
}

function Remove-TidyArtifacts {
    [CmdletBinding()]
    param(
        [psobject[]] $Artifacts,
        [switch] $DryRun
    )

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    if (-not $Artifacts -or $Artifacts.Count -eq 0) {
        return [pscustomobject]@{ Results = $results; RemovedCount = 0; FailureCount = 0; FreedBytes = 0 }
    }

    $order = @{ Directory = 0; File = 1; Registry = 2; Service = 3 }
    $ordered = $Artifacts | Sort-Object -Property @{ Expression = { if ($order.ContainsKey($_.type)) { $order[$_.type] } else { 99 } } }, path

    $removed = 0
    $freed = 0

    foreach ($artifact in $ordered) {
        $entry = [ordered]@{
            artifactId = $artifact.id
            type       = $artifact.type
            path       = $artifact.path
            success    = $false
            error      = $null
        }

        if ($DryRun) {
            $entry.success = $true
        }
        else {
            try {
                switch ($artifact.type) {
                    'Directory' {
                        if (Test-Path -LiteralPath $artifact.path) {
                            Remove-Item -LiteralPath $artifact.path -Recurse -Force -ErrorAction Stop
                        }
                        $entry.success = $true
                    }
                    'File' {
                        if (Test-Path -LiteralPath $artifact.path) {
                            Remove-Item -LiteralPath $artifact.path -Force -ErrorAction Stop
                        }
                        $entry.success = $true
                    }
                    'Registry' {
                        $providerPath = $artifact.path
                        try {
                            Remove-Item -LiteralPath $providerPath -Recurse -Force -ErrorAction Stop
                        }
                        catch [System.Management.Automation.ItemNotFoundException] {
                            # Already removed; continue to verification.
                        }

                        Start-Sleep -Milliseconds 150
                        if (Test-Path -LiteralPath $providerPath) {
                            throw 'Registry key still exists after removal attempt.'
                        }

                        $entry.success = $true
                    }
                    'Service' {
                        try { Stop-Service -Name $artifact.path -Force -ErrorAction SilentlyContinue } catch { }
                        & sc.exe 'delete' $artifact.path *> $null
                        if ($LASTEXITCODE -ne 0) {
                            throw "sc.exe delete failed with exit code $LASTEXITCODE"
                        }
                        $entry.success = $true
                    }
                    Default {
                        $entry.error = 'Unsupported artifact type.'
                    }
                }
            }
            catch {
                $entry.error = $_.Exception.Message
            }
        }

        if ($entry.success) {
            $removed++
            if ($artifact.sizeBytes) {
                $freed += [long]$artifact.sizeBytes
            }
        }

        $results.Add([pscustomobject]$entry) | Out-Null
    }

    $failures = $results | Where-Object { -not $_.success }
    return [pscustomobject]@{
        Results      = $results
        RemovedCount = $removed
        FailureCount = $failures.Count
        FreedBytes   = $freed
    }
}

function Publish-OblivionForcePlan {
    [CmdletBinding()]
    param([psobject] $Artifact)

    if (-not $Artifact) { return }

    $strategies = switch ($Artifact.type) {
        'Directory' { @('UnlockAttributes', 'TakeOwnership', 'RobocopyPurge', 'CmdRd', 'PendingDelete') }
        'File'      { @('UnlockAttributes', 'TakeOwnership', 'CmdDel', 'PendingDelete') }
        'Registry'  { @('RegDelete') }
        'Service'   { @('StopService', 'ScDelete') }
        Default     { @() }
    }

    $payload = [ordered]@{
        artifactId = $Artifact.id
        type       = $Artifact.type
        path       = $Artifact.path
        strategies = $strategies
        metadata   = $Artifact.metadata
        defaultSelected = $Artifact.defaultSelected
    }

    Write-TidyStructuredEvent -Type 'forceRemovalPlan' -Payload $payload
}

function Invoke-OblivionForceRemoval {
    [CmdletBinding()]
    param(
        [psobject[]] $Artifacts,
        [switch] $DryRun
    )

    if (-not $Artifacts -or $Artifacts.Count -eq 0) {
        return [pscustomobject]@{ Results = @(); RemovedCount = 0; FailureCount = 0; FreedBytes = 0 }
    }

    $initial = Remove-TidyArtifacts -Artifacts $Artifacts -DryRun:$DryRun
    if ($DryRun) {
        return $initial
    }

    $results = $initial.Results
    $removedCount = $initial.RemovedCount
    $freedBytes = [long]$initial.FreedBytes
    $failures = $results | Where-Object { -not $_.success }

    if (-not $failures -or $failures.Count -eq 0) {
        return $initial
    }

    Assert-TidyAdmin

    $publishedPlans = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($entry in $failures) {
        $artifact = $Artifacts | Where-Object { $_.id -eq $entry.artifactId } | Select-Object -First 1
        if (-not $artifact) { continue }

        if ($artifact.id -and -not $publishedPlans.Contains($artifact.id)) {
            $null = $publishedPlans.Add($artifact.id)
            Publish-OblivionForcePlan -Artifact $artifact
        }

        $retry = Invoke-OblivionForceArtifactRemoval -Artifact $artifact
        if (-not $entry.PSObject.Properties['retryStrategy']) {
            $entry | Add-Member -NotePropertyName 'retryStrategy' -NotePropertyValue $null
        }
        $entry.retryStrategy = $retry.strategy

        if ($retry.success) {
            if (-not $entry.success) {
                $removedCount++
                if ($artifact.sizeBytes) {
                    $freedBytes += [long]$artifact.sizeBytes
                }
            }

            $entry.success = $true
            $entry.error = $null
        }
        elseif ($retry.error) {
            $entry.error = $retry.error
        }
    }

    foreach ($entry in $results) {
        if (-not $entry.success) { continue }
        $artifact = $Artifacts | Where-Object { $_.id -eq $entry.artifactId } | Select-Object -First 1
        if (-not $artifact) { continue }

        if ($artifact.id -and -not $publishedPlans.Contains($artifact.id)) {
            $null = $publishedPlans.Add($artifact.id)
            Publish-OblivionForcePlan -Artifact $artifact
        }

        if (-not (Test-OblivionArtifactRemoved -Artifact $artifact)) {
            $verification = Invoke-OblivionForceArtifactRemoval -Artifact $artifact
            if (-not $entry.PSObject.Properties['retryStrategy']) {
                $entry | Add-Member -NotePropertyName 'retryStrategy' -NotePropertyValue $null
            }
            $entry.retryStrategy = $verification.strategy

            if (-not $verification.success -or -not (Test-OblivionArtifactRemoved -Artifact $artifact)) {
                $entry.success = $false
                if (-not $entry.error) {
                    $entry.error = if ($verification.error) { $verification.error } else { 'Artifact still detected after verification.' }
                }

                if ($removedCount -gt 0) {
                    $removedCount--
                }

                if ($artifact.sizeBytes) {
                    $freedBytes = [math]::Max(0, $freedBytes - [long]$artifact.sizeBytes)
                }
            }
        }
    }

    $failureCount = ($results | Where-Object { -not $_.success }).Count

    return [pscustomobject]@{
        Results      = $results
        RemovedCount = $removedCount
        FailureCount = $failureCount
        FreedBytes   = $freedBytes
    }
}

function Invoke-OblivionForceArtifactRemoval {
    [CmdletBinding()]
    param([psobject] $Artifact)

    if (-not $Artifact) {
        return [pscustomobject]@{ success = $false; strategy = 'Unknown'; error = 'No artifact supplied.' }
    }

    $validation = Test-OblivionArtifactRemovalAllowed -Artifact $Artifact
    if (-not $validation.allowed) {
        $reason = if ($validation.reason) { $validation.reason } else { 'Artifact did not pass safety validation.' }
        return [pscustomobject]@{ success = $false; strategy = 'Safeguard'; error = $reason }
    }

    switch ($Artifact.type) {
        'Directory' { return Invoke-OblivionForceDirectoryRemoval -Path $Artifact.path }
        'File'      { return Invoke-OblivionForceFileRemoval -Path $Artifact.path }
        'Registry'  { return Invoke-OblivionForceRegistryRemoval -Path $Artifact.path }
        'Service'   { return Invoke-OblivionForceServiceRemoval -Name $Artifact.path }
        Default     { return [pscustomobject]@{ success = $false; strategy = 'Unsupported'; error = 'Artifact type not supported for force removal.' } }
    }
}

function Test-OblivionArtifactRemoved {
    [CmdletBinding()]
    param([psobject] $Artifact)

    if (-not $Artifact) {
        return $true
    }

    try {
        switch ($Artifact.type) {
            'Directory' { return -not (Test-Path -LiteralPath $Artifact.path) }
            'File'      { return -not (Test-Path -LiteralPath $Artifact.path) }
            'Registry'  { return -not (Test-Path -LiteralPath $Artifact.path) }
            'Service'   {
                $svc = Get-Service -Name $Artifact.path -ErrorAction SilentlyContinue
                return -not $svc
            }
            Default     { return $true }
        }
    }
    catch {
        return $false
    }
}

function Invoke-OblivionForceDirectoryRemoval {
    [CmdletBinding()]
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [pscustomobject]@{ success = $false; strategy = 'Directory'; error = 'Path missing.' }
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ success = $true; strategy = 'AlreadyRemoved'; error = $null }
    }

    $strategies = @(
        @{ name = 'UnlockAttributes'; action = {
                Invoke-OblivionUnlockAttributes -Path $Path -IsDirectory
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            }
        },
        @{ name = 'TakeOwnership'; action = {
                Invoke-OblivionTakeOwnership -Path $Path -IsDirectory
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            }
        },
        @{ name = 'RobocopyPurge'; action = {
                Invoke-OblivionTakeOwnership -Path $Path -IsDirectory
                $empty = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('oblivion-empty-' + [guid]::NewGuid().ToString('N'))
                try {
                    $null = New-Item -ItemType Directory -Path $empty -ErrorAction Stop
                    $null = & robocopy.exe $empty $Path /MIR /NFL /NDL /NJH /NJS /NC /NS 2>$null 1>$null
                }
                finally {
                    if (Test-Path -LiteralPath $empty) {
                        Remove-Item -LiteralPath $empty -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            }
        },
        @{ name = 'CmdRd'; action = {
                Invoke-OblivionTakeOwnership -Path $Path -IsDirectory
                $arguments = @('/c', "rd /s /q `"$Path`"")
                $process = Start-Process -FilePath 'cmd.exe' -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
                if ($process.ExitCode -ne 0) { throw "cmd.exe rd exited with code $($process.ExitCode)" }
            }
        }
    )

    $lastError = $null
    foreach ($strategy in $strategies) {
        try {
            & $strategy.action
            if (-not (Test-Path -LiteralPath $Path)) {
                return [pscustomobject]@{ success = $true; strategy = $strategy.name; error = $null }
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }
    }

    if (Add-OblivionPendingDeleteEntry -Path $Path) {
        return [pscustomobject]@{ success = $true; strategy = 'PendingDelete'; error = $lastError }
    }

    return [pscustomobject]@{ success = $false; strategy = 'Directory'; error = $lastError }
}

function Invoke-OblivionForceFileRemoval {
    [CmdletBinding()]
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [pscustomobject]@{ success = $false; strategy = 'File'; error = 'Path missing.' }
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ success = $true; strategy = 'AlreadyRemoved'; error = $null }
    }

    $strategies = @(
        @{ name = 'UnlockAttributes'; action = {
                Invoke-OblivionUnlockAttributes -Path $Path
                Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
            }
        },
        @{ name = 'TakeOwnership'; action = {
                Invoke-OblivionTakeOwnership -Path $Path
                Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
            }
        },
        @{ name = 'CmdDel'; action = {
                Invoke-OblivionTakeOwnership -Path $Path
                $arguments = @('/c', "del /f /q `"$Path`"")
                $process = Start-Process -FilePath 'cmd.exe' -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
                if ($process.ExitCode -ne 0) { throw "cmd.exe del exited with code $($process.ExitCode)" }
            }
        }
    )

    $lastError = $null
    foreach ($strategy in $strategies) {
        try {
            & $strategy.action
            if (-not (Test-Path -LiteralPath $Path)) {
                return [pscustomobject]@{ success = $true; strategy = $strategy.name; error = $null }
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }
    }

    if (Add-OblivionPendingDeleteEntry -Path $Path) {
        return [pscustomobject]@{ success = $true; strategy = 'PendingDelete'; error = $lastError }
    }

    return [pscustomobject]@{ success = $false; strategy = 'File'; error = $lastError }
}

function Add-OblivionPendingDeleteEntry {
    [CmdletBinding()]
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return $true
    }

    try {
        Assert-TidyAdmin
    }
    catch {
        return $false
    }

    $normalized = Resolve-TidyPath -Path $Path
    if (-not $normalized) { $normalized = $Path }
    $devicePath = if ($normalized.StartsWith('\\?\')) { $normalized } else { "\\??\\$normalized" }

    try {
        $regPath = 'SYSTEM\CurrentControlSet\Control\Session Manager'
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($regPath, $true)
        if (-not $key) {
            return $false
        }

        $existing = $key.GetValue('PendingFileRenameOperations', @(), [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if (-not $existing) { $existing = @() }

        $updated = @($existing + $devicePath + '')
        $key.SetValue('PendingFileRenameOperations', $updated, [Microsoft.Win32.RegistryValueKind]::MultiString)
        $key.Close()
        return $true
    }
    catch {
        return $false
    }
}

function Invoke-OblivionForceRegistryRemoval {
    [CmdletBinding()]
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [pscustomobject]@{ success = $false; strategy = 'Registry'; error = 'Path missing.' }
    }

    $providerPath = $Path
    $regPath = Invoke-OblivionNormalizeRegistryPath -ProviderPath $providerPath
    if (-not $regPath) {
        return [pscustomobject]@{ success = $false; strategy = 'Registry'; error = 'Invalid registry path.' }
    }

    try {
        $arguments = @('delete', $regPath, '/f')
        $null = & reg.exe @arguments 2>$null 1>$null
        Start-Sleep -Milliseconds 150
        if (Test-Path -Path $providerPath) {
            throw 'Registry key still exists after deletion attempt.'
        }
        return [pscustomobject]@{ success = $true; strategy = 'Registry'; error = $null }
    }
    catch {
        return [pscustomobject]@{ success = $false; strategy = 'Registry'; error = $_.Exception.Message }
    }
}

function Invoke-OblivionForceServiceRemoval {
    [CmdletBinding()]
    param([string] $Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return [pscustomobject]@{ success = $false; strategy = 'Service'; error = 'Service name missing.' }
    }

    try { Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue } catch { }

    $null = & sc.exe 'delete' $Name 2>$null 1>$null
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1060) {
        return [pscustomobject]@{ success = $false; strategy = 'Service'; error = "sc.exe delete exited with code $LASTEXITCODE" }
    }

    Start-Sleep -Milliseconds 200
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service) {
        return [pscustomobject]@{ success = $false; strategy = 'Service'; error = 'Service still registered.' }
    }

    return [pscustomobject]@{ success = $true; strategy = 'Service'; error = $null }
}

function Invoke-OblivionUnlockAttributes {
    [CmdletBinding()]
    param(
        [string] $Path,
        [switch] $IsDirectory
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return
    }

    $args = @('-R', '-S', '-H', $Path)
    if ($IsDirectory) {
        $args += '/S'
        $args += '/D'
    }

    try { $null = & attrib.exe @args 2>$null 1>$null } catch { }
}

function Invoke-OblivionTakeOwnership {
    [CmdletBinding()]
    param(
        [string] $Path,
        [switch] $IsDirectory
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return
    }

    $takeArgs = @('/f', $Path, '/a')
    if ($IsDirectory) {
        $takeArgs += '/r'
        $takeArgs += '/d'
        $takeArgs += 'y'
    }

    try { $null = & takeown.exe @takeArgs 2>$null 1>$null } catch { }

    $icaclsArgs = @($Path, '/grant', 'Administrators:F', '/C', '/Q')
    if ($IsDirectory) {
        $icaclsArgs += '/T'
    }

    try { $null = & icacls.exe @icaclsArgs 2>$null 1>$null } catch { }
}

function Invoke-OblivionNormalizeRegistryPath {
    [CmdletBinding()]
    param([string] $ProviderPath)

    if ([string]::IsNullOrWhiteSpace($ProviderPath)) {
        return $null
    }

    if ($ProviderPath.StartsWith('Registry::', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $ProviderPath.Substring(10)
    }

    return $ProviderPath
}

Export-ModuleMember -Function Convert-TidyLogMessage, Write-TidyLog, Get-TidyCommandPath, Get-TidyWingetMsixCandidates, Get-TidyWingetInstalledVersion, Get-TidyChocoInstalledVersion, Get-TidyScoopInstalledVersion, Get-TidyInstalledPackageVersion, Assert-TidyAdmin, Set-TidyMenuShowDelay, Set-TidyWindowAnimation, Set-TidyVisualEffectsProfile, Set-TidyPrefetchingMode, Set-TidyTelemetryLevel, Set-TidyCortanaPolicy, Set-TidyNetworkLatencyProfile, Set-TidySysMainState, Set-TidyLowDiskAlertPolicy, Set-TidyAutoRestartSignOn, Set-TidyAutoEndTasks, Set-TidyHungAppTimeouts, Set-TidyLockWorkstationPolicy, Resolve-TidyPath, ConvertTo-TidyNameKey, Get-TidyProgramDataDirectory, New-TidyFeatureRunDirectory, Write-TidyStructuredEvent, Write-TidyRunLog, Invoke-TidyCommandLine, Get-TidyProcessSnapshot, Get-TidyServiceSnapshot, Find-TidyRelatedProcesses, Stop-TidyProcesses, ConvertTo-TidyRegistryPath, Measure-TidyDirectoryBytes, New-TidyArtifactId, New-TidyFileArtifact, New-TidyRegistryArtifact, New-TidyServiceArtifact, Get-TidyCandidateDataFolders, Get-TidyArtifacts, Remove-TidyArtifacts, Invoke-OblivionProcessSweep, Invoke-OblivionArtifactDiscovery, Invoke-OblivionForceRemoval
