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

