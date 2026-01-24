param(
    [Parameter(Mandatory = $true)]
    [string[]]$SourcePaths,

    [Parameter(Mandatory = $false)]
    [string]$TargetPath = (Join-Path $env:TEMP "reset-rescue-staging"),

    [string[]]$RegistryKeys,

    [switch]$CreateSnapshot,

    [ValidateSet('Backup','Restore')]
    [string]$Mode = 'Backup',

    [string]$RestoreRoot,

    [ValidateSet('Overwrite','Rename','Skip','BackupExisting')]
    [string]$Conflict = 'Rename',

    [string]$LogPath = (Join-Path $env:TEMP "reset-rescue.log")
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$logDir = Split-Path -Path $LogPath -Parent
if (-not [string]::IsNullOrWhiteSpace($logDir) -and -not (Test-Path -LiteralPath $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

function Write-Log {
    param(
        [string]$Message,
        [string]$Level = 'INFO'
    )
    $line = "[$(Get-Date -Format o)] [$Level] $Message"
    Add-Content -Path $LogPath -Value $line
}

$result = [ordered]@{
    mode = $Mode
    status = 'unknown'
    snapshotId = $null
    snapshotPath = $null
    copied = @()
    skipped = @()
    registryExports = @()
    errors = @()
    logPath = $LogPath
}

if (-not (Test-Path -LiteralPath $TargetPath)) {
    New-Item -ItemType Directory -Path $TargetPath -Force | Out-Null
}

$shadowDevice = $null
if ($CreateSnapshot) {
    try {
        $class = Get-WmiObject -List Win32_ShadowCopy -ErrorAction Stop
        $create = $class.Create('C:\\', 'ClientAccessible')
        if ($create.ReturnValue -eq 0) {
            $shadowId = $create.ShadowID
            $shadow = Get-WmiObject -Query "SELECT * FROM Win32_ShadowCopy WHERE ID='$shadowId'" -ErrorAction Stop
            $shadowDevice = $shadow.DeviceObject
            $result.snapshotId = $shadowId
            $result.snapshotPath = $shadowDevice
            Write-Log "Created shadow copy $shadowId at $shadowDevice"
        }
        else {
            Write-Log "Shadow copy creation returned $($create.ReturnValue)" 'WARN'
        }
    }
    catch {
        Write-Log "Shadow copy creation failed: $_" 'WARN'
    }
}

function Resolve-SourcePath {
    param([string]$Path)
    try {
        $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).ProviderPath
        if ($null -ne $shadowDevice -and $resolved.Length -ge 2 -and $resolved[1] -eq ':') {
            $relative = $resolved.Substring(2)
            return "\\?\GLOBALROOT$shadowDevice$relative"
        }
        return $resolved
    }
    catch {
        return $null
    }
}

function Copy-DirectorySafe {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    $args = @(
        '"{0}"' -f $Source,
        '"{0}"' -f $Destination,
        '/E','/COPY:DAT','/R:2','/W:2','/NFL','/NDL','/NP','/MT:8'
    )

    $proc = Start-Process -FilePath robocopy.exe -ArgumentList $args -NoNewWindow -Wait -PassThru -ErrorAction Stop
    if ($proc.ExitCode -ge 8) {
        throw "Robocopy failed with code $($proc.ExitCode)"
    }
}

function Copy-FileSafe {
    param(
        [string]$Source,
        [string]$Destination
    )

    $destDir = Split-Path -Path $Destination -Parent
    if (-not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }

    try {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
    }
    catch {
        Write-Log "Copy-Item failed for $Source: $_" 'WARN'
        $args = @(
            '"{0}"' -f (Split-Path -Path $Source -Parent),
            '"{0}"' -f $destDir,
            (Split-Path -Path $Source -Leaf),
            '/R:2','/W:2','/NFL','/NDL','/NP','/MT:4','/XO'
        )
        $proc = Start-Process -FilePath robocopy.exe -ArgumentList $args -NoNewWindow -Wait -PassThru -ErrorAction Stop
        if ($proc.ExitCode -ge 8) {
            throw "Robocopy fallback failed for $Source with code $($proc.ExitCode)"
        }
    }
}

function Export-RegistryKey {
    param(
        [string]$KeyPath,
        [string]$Destination
    )

    $destDir = Split-Path -Path $Destination -Parent
    if (-not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }

    $process = Start-Process -FilePath reg.exe -ArgumentList @('export', $KeyPath, $Destination, '/y') -NoNewWindow -Wait -PassThru -ErrorAction Stop
    if ($process.ExitCode -ne 0) {
        throw "reg export failed for $KeyPath with code $($process.ExitCode)"
    }
}

try {
    if ($Mode -eq 'Backup') {
        foreach ($source in $SourcePaths) {
            $resolved = Resolve-SourcePath -Path $source
            if ([string]::IsNullOrWhiteSpace($resolved)) {
                $result.skipped += $source
                Write-Log "Skip invalid path: $source" 'WARN'
                continue
            }

            if (Test-Path -LiteralPath $resolved -PathType Leaf) {
                $dest = Join-Path $TargetPath (Split-Path -Path $resolved -Leaf)
                Copy-FileSafe -Source $resolved -Destination $dest
                $result.copied += $resolved
                continue
            }

            if (Test-Path -LiteralPath $resolved -PathType Container) {
                $dest = Join-Path $TargetPath (Split-Path -Path $resolved -Leaf)
                Copy-DirectorySafe -Source $resolved -Destination $dest
                $result.copied += $resolved
                continue
            }

            $result.skipped += $resolved
            Write-Log "Skip missing path: $resolved" 'WARN'
        }

        foreach ($key in $RegistryKeys) {
            if ([string]::IsNullOrWhiteSpace($key)) { continue }
            try {
                $safeName = ($key -replace '[^a-zA-Z0-9_-]', '_')
                $dest = Join-Path $TargetPath (Join-Path 'meta' ("registry_$safeName.reg"))
                Export-RegistryKey -KeyPath $key -Destination $dest
                $result.registryExports += $dest
            }
            catch {
                $result.errors += "Registry export failed for $key: $_"
                Write-Log "Registry export failed for $key: $_" 'WARN'
            }
        }

        $result.status = 'ok'
    }
    else {
        if ([string]::IsNullOrWhiteSpace($RestoreRoot)) {
            throw "RestoreRoot is required when Mode is Restore."
        }

        foreach ($source in $SourcePaths) {
            $resolved = Resolve-SourcePath -Path $source
            if ([string]::IsNullOrWhiteSpace($resolved)) {
                $result.skipped += $source
                continue
            }

            if (Test-Path -LiteralPath $resolved -PathType Container) {
                $dest = Join-Path $RestoreRoot (Split-Path -Path $resolved -Leaf)
                Copy-DirectorySafe -Source $resolved -Destination $dest
                $result.copied += $resolved
                continue
            }

            if (Test-Path -LiteralPath $resolved -PathType Leaf) {
                $dest = Join-Path $RestoreRoot (Split-Path -Path $resolved -Leaf)
                if (Test-Path -LiteralPath $dest) {
                    switch ($Conflict) {
                        'Overwrite' { Remove-Item -LiteralPath $dest -Force }
                        'Skip' { $result.skipped += $resolved; continue }
                        'BackupExisting' { Rename-Item -LiteralPath $dest -NewName ((Split-Path -Path $dest -Leaf) + '.bak') -Force }
                        default { Rename-Item -LiteralPath $dest -NewName ((Split-Path -Path $dest -Leaf) + '-backup') -Force }
                    }
                }

                Copy-FileSafe -Source $resolved -Destination $dest
                $result.copied += $resolved
                continue
            }

            $result.skipped += $resolved
        }

        $result.status = 'ok'
    }
}
catch {
    $result.status = 'error'
    $result.errors += $_.ToString()
    Write-Log "Fatal: $_" 'ERROR'
}

$result | ConvertTo-Json -Depth 6 -Compress
