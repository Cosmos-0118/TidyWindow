param(
    [Parameter(Mandatory = $true)]
    [string] $Manager,
    [Parameter(Mandatory = $true)]
    [string] $PackageId,
    [int] $MaxResults = 40
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$VerbosePreference = 'SilentlyContinue'

$callerScriptPath = $PSCmdlet.MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerScriptPath)) {
    $callerScriptPath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerScriptPath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -Path $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$script:AnsiRegex = [System.Text.RegularExpressions.Regex]::new('\x1B\[[0-9;]*[A-Za-z]', [System.Text.RegularExpressions.RegexOptions]::Compiled)

function Remove-TidyAnsiSequences {
    param([AllowNull()][string] $Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $Text
    }

    return $script:AnsiRegex.Replace($Text, '').Replace("`r", '').Trim()
}

function Normalize-VersionString {
    param([AllowNull()][string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $normalized = $Value.Trim()
    $normalized = $normalized.TrimStart('v', 'V')
    $normalized = $normalized.Replace('_', '.').Replace('-', '.')
    while ($normalized.Contains('..')) {
        $normalized = $normalized.Replace('..', '.')
    }

    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return $null
    }

    return $normalized
}

function Sort-VersionList {
    param(
        [System.Collections.IEnumerable] $Values,
        [int] $Max = 40
    )

    $unique = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $numeric = [System.Collections.Generic.List[pscustomobject]]::new()
    $textual = [System.Collections.Generic.List[string]]::new()

    foreach ($value in @($Values)) {
        if ($null -eq $value) { continue }
        $text = $value.ToString()
        if ([string]::IsNullOrWhiteSpace($text)) { continue }

        $trimmed = $text.Trim()
        if (-not $unique.Add($trimmed)) { continue }

        $normalized = Normalize-VersionString -Value $trimmed
        $parsed = $null
        if ($normalized -and [version]::TryParse($normalized, [ref]$parsed)) {
            $numeric.Add([pscustomobject]@{ Original = $trimmed; Version = $parsed })
            continue
        }

        $textual.Add($trimmed)
    }

    $ordered = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in ($numeric | Sort-Object Version -Descending)) {
        $ordered.Add($entry.Original)
    }

    foreach ($entry in $textual) {
        $ordered.Add($entry)
    }

    if ($Max -gt 0 -and $ordered.Count -gt $Max) {
        return $ordered.GetRange(0, $Max).ToArray()
    }

    return $ordered.ToArray()
}

function Get-TidyScoopRootCandidates {
    $roots = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $add = {
        param([string] $Value)

        if ([string]::IsNullOrWhiteSpace($Value)) {
            return
        }

        $trimmed = $Value.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            return
        }

        try {
            $normalized = [System.IO.Path]::GetFullPath($trimmed)
        }
        catch {
            $normalized = $trimmed
        }

        if ($seen.Add($normalized)) {
            [void]$roots.Add($normalized)
        }
    }

    foreach ($candidate in @($env:SCOOP, $env:SCOOP_GLOBAL)) {
        & $add $candidate
    }

    $programData = [Environment]::GetFolderPath('CommonApplicationData')
    if (-not [string]::IsNullOrWhiteSpace($programData)) {
        & $add (Join-Path -Path $programData -ChildPath 'scoop')
    }

    $userProfile = [Environment]::GetFolderPath('UserProfile')
    if (-not [string]::IsNullOrWhiteSpace($userProfile)) {
        & $add (Join-Path -Path $userProfile -ChildPath 'scoop')
    }

    return $roots
}

function Get-WingetVersionList {
    param(
        [string] $PackageId,
        [int] $Max
    )

    $exe = Get-TidyCommandPath -CommandName 'winget'
    if (-not $exe) {
        throw 'winget CLI was not found on this machine.'
    }

    $versions = [System.Collections.Generic.List[string]]::new()
    $commands = @(
        @{ Args = @('show', '--id', $PackageId, '-e', '--versions', '--disable-interactivity', '--accept-source-agreements'); Mode = 'versions' },
        @{ Args = @('show', '--id', $PackageId, '-e', '--disable-interactivity', '--accept-source-agreements'); Mode = 'show' },
        @{ Args = @('search', '--id', $PackageId, '-e', '--disable-interactivity', '--accept-source-agreements'); Mode = 'search' }
    )

    foreach ($command in $commands) {
        try {
            $output = & $exe @($command.Args) 2>$null
        }
        catch {
            continue
        }

        if (-not $output) {
            continue
        }

        foreach ($line in @($output)) {
            if ($null -eq $line) { continue }
            $clean = Remove-TidyAnsiSequences -Text ([string]$line)
            if ([string]::IsNullOrWhiteSpace($clean)) { continue }

            switch ($command.Mode) {
                'versions' {
                    if ($clean -match '^(?i:found\s)') { continue }
                    if ($clean -match '^(?i:version)$') { continue }
                    if ($clean -match '^[-\s]+$') { continue }
                    $match = [System.Text.RegularExpressions.Regex]::Match($clean, '^([0-9][0-9A-Za-z\._\-+]*)')
                    if ($match.Success) {
                        $versions.Add($match.Groups[1].Value.Trim())
                    }
                }
                'show' {
                    if ($clean -match '^\s*(Available|Latest)\s+Version\s*:\s*(.+)$') {
                        $versions.Add($matches[2].Trim())
                        continue
                    }

                    if ($clean -match '^\s*Version\s*:\s*(.+)$') {
                        $versions.Add($matches[1].Trim())
                    }
                }
                'search' {
                    if ($clean -match '^(?i)\s*Name\s+Id\s+Version') { continue }
                    if ($clean -match '^-{3,}') { continue }

                    $trimmed = $clean.Trim()
                    $pattern = '^(?<name>.+?)\s+' + [System.Text.RegularExpressions.Regex]::Escape($PackageId) + '\s+(?<version>[^\s]+)'
                    $match = [System.Text.RegularExpressions.Regex]::Match($trimmed, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                    if ($match.Success) {
                        $versions.Add($match.Groups['version'].Value.Trim())
                    }
                }
            }
        }

        if ($versions.Count -ge $Max) {
            break
        }
    }

    return Sort-VersionList -Values $versions -Max $Max
}

function Get-ChocoVersionList {
    param(
        [string] $PackageId,
        [int] $Max
    )

    $exe = Get-TidyCommandPath -CommandName 'choco'
    if (-not $exe) {
        throw 'Chocolatey (choco) CLI was not found on this machine.'
    }

    $versions = [System.Collections.Generic.List[string]]::new()
    $queries = @(
        @('search', $PackageId, '--exact', '--all-versions', '--limit-output', '--no-color', '--no-progress'),
        @('search', $PackageId, '--exact', '--limit-output', '--no-color', '--no-progress'),
        @('info', $PackageId, '--no-color', '--no-progress')
    )

    foreach ($args in $queries) {
        try {
            $output = & $exe @args 2>$null
        }
        catch {
            continue
        }

        if (-not $output) {
            continue
        }

        foreach ($line in @($output)) {
            if ($null -eq $line) { continue }
            $clean = Remove-TidyAnsiSequences -Text ([string]$line)
            if ([string]::IsNullOrWhiteSpace($clean)) { continue }
            if ($clean -match '^(?i)Chocolatey\s') { continue }
            if ($clean -match '^\d+\s+packages?\s+(?:found|installed).*') { continue }

            $pipePattern = '^(?i)' + [System.Text.RegularExpressions.Regex]::Escape($PackageId) + '\|([^|]+)'
            $spacePattern = '^(?i)' + [System.Text.RegularExpressions.Regex]::Escape($PackageId) + '\s+([0-9][^\s]*)'

            if ($clean -match $pipePattern) {
                $versions.Add($matches[1].Trim())
                continue
            }

            if ($clean -match $spacePattern) {
                $versions.Add($matches[1].Trim())
                continue
            }

            if ($clean -match '^\s*Latest Version\s*:\s*(.+)$') {
                $versions.Add($matches[1].Trim())
                continue
            }

            if ($clean -match '^\s*Version\s*:\s*(.+)$') {
                $versions.Add($matches[1].Trim())
                continue
            }

            if ($clean -match '^\s*Available Versions?\s*:\s*(.+)$') {
                $matches[1].Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object {
                    $versions.Add($_.Trim())
                }
            }
        }

        if ($versions.Count -ge $Max) {
            break
        }
    }

    return Sort-VersionList -Values $versions -Max $Max
}

function Get-ScoopVersionInfo {
    param(
        [string] $PackageId
    )

    $manifestRoot = $null
    foreach ($candidate in Get-TidyScoopRootCandidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            $manifestRoot = $candidate
            break
        }
    }

    if (-not $manifestRoot) {
        return @()
    }

    $bucketManifests = @()
    $bucketsPath = Join-Path -Path $manifestRoot -ChildPath 'buckets'
    if (Test-Path -LiteralPath $bucketsPath) {
        try {
            $bucketManifests = Get-ChildItem -Path $bucketsPath -Recurse -Filter "$PackageId.json" -ErrorAction Stop
        }
        catch {
            $bucketManifests = @()
        }
    }

    $versions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($manifest in @($bucketManifests)) {
        try {
            $json = Get-Content -LiteralPath $manifest.FullName -Raw -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace($json)) {
                $data = ConvertFrom-Json -InputObject $json -ErrorAction Stop
                $value = $data.version
                if ($value -and [string]::IsNullOrWhiteSpace($value) -eq $false) {
                    $versions.Add($value.Trim()) | Out-Null
                }
            }
        }
        catch {
            continue
        }
    }

    if ($versions.Count -eq 0) {
        return @()
    }

    $results = [System.Collections.Generic.List[string]]::new($versions.Count)
    foreach ($value in $versions) {
        $results.Add($value) | Out-Null
    }

    return $results.ToArray()
}

function Get-PackageVersionData {
    param(
        [string] $Manager,
        [string] $PackageId,
        [int] $MaxResults
    )

    $managerKey = $Manager.ToLowerInvariant()
    switch ($managerKey) {
        'winget' {
            return [pscustomobject]@{
                Versions = Get-WingetVersionList -PackageId $PackageId -Max $MaxResults
                Error    = $null
            }
        }
        'choco' { return Get-PackageVersionData -Manager 'chocolatey' -PackageId $PackageId -MaxResults $MaxResults }
        'chocolatey' {
            return [pscustomobject]@{
                Versions = Get-ChocoVersionList -PackageId $PackageId -Max $MaxResults
                Error    = $null
            }
        }
        'scoop' {
            $versions = Get-ScoopVersionInfo -PackageId $PackageId
            if ($versions.Length -gt 0) {
                return [pscustomobject]@{
                    Versions = Sort-VersionList -Values $versions -Max $MaxResults
                    Error    = 'Scoop buckets only surface the current manifest. Historical versions may need to be installed manually.'
                }
            }

            return [pscustomobject]@{
                Versions = @()
                Error    = 'Scoop packages do not expose historical version data from the local bucket.'
            }
        }
        default {
            throw "Unsupported package manager '$Manager'."
        }
    }
}

if ($MaxResults -le 0) {
    $MaxResults = 40
}

$payload = $null
try {
    $result = Get-PackageVersionData -Manager $Manager -PackageId $PackageId -MaxResults $MaxResults
    $payload = [ordered]@{
        manager  = $Manager
        packageId = $PackageId
        succeeded = [string]::IsNullOrWhiteSpace($result.Error)
        versions = @($result.Versions)
        error    = $result.Error
    }
}
catch {
    $payload = [ordered]@{
        manager  = $Manager
        packageId = $PackageId
        succeeded = $false
        versions = @()
        error    = $_.Exception.Message
    }
}

$payload | ConvertTo-Json -Depth 6
