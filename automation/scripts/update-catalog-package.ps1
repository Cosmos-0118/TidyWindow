param(param(

    [Parameter(Mandatory = $true)]    [string]$Manager,

    [string] $Manager,    [string]$PackageId

    [Parameter(Mandatory = $true)])

    [string] $PackageId,

    [string] $DisplayName,Set-StrictMode -Version Latest

    [switch] $RequiresAdmin,$ErrorActionPreference = 'Stop'

    [switch] $Elevated,

    [string] $ResultPathif ([string]::IsNullOrWhiteSpace($Manager)) {

)    throw 'Package manager must be provided.'

}

Set-StrictMode -Version Latest

$ErrorActionPreference = 'Stop'if ([string]::IsNullOrWhiteSpace($PackageId)) {

    throw 'Package identifier must be provided.'

if ([string]::IsNullOrWhiteSpace($DisplayName)) {}

    $DisplayName = $PackageId

}$normalizedManager = $Manager.Trim()

$managerKey = $normalizedManager.ToLowerInvariant()

$normalizedManager = $Manager.Trim()

$managerKey = $normalizedManager.ToLowerInvariant()$wingetCommand = Get-Command -Name 'winget' -ErrorAction SilentlyContinue

$needsElevation = $RequiresAdmin.IsPresent -or $managerKey -in @('winget', 'choco', 'chocolatey')$chocoCommand = Get-Command -Name 'choco' -ErrorAction SilentlyContinue

$scoopCommand = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue

$script:TidyOutput = [System.Collections.Generic.List[string]]::new()

$script:TidyErrors = [System.Collections.Generic.List[string]]::new()function Resolve-ManagerExecutable {

$script:ResultPayload = $null    param(

$script:UsingResultFile = -not [string]::IsNullOrWhiteSpace($ResultPath)        [string]$Key

    )

if ($script:UsingResultFile) {

    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)    switch ($Key) {

}        'winget' {

            if (-not $wingetCommand) {

function Add-TidyOutput {                throw 'winget CLI was not found on this machine.'

    param([string] $Message)            }

            return if ($wingetCommand.Source) { $wingetCommand.Source } else { 'winget' }

    if (-not [string]::IsNullOrWhiteSpace($Message)) {        }

        [void]$script:TidyOutput.Add($Message)        'choco' {

    }            if (-not $chocoCommand) {

}                throw 'Chocolatey (choco) CLI was not found on this machine.'

            }

function Add-TidyError {            return if ($chocoCommand.Source) { $chocoCommand.Source } else { 'choco' }

    param([string] $Message)        }

        'chocolatey' {

    if (-not [string]::IsNullOrWhiteSpace($Message)) {            if (-not $chocoCommand) {

        [void]$script:TidyErrors.Add($Message)                throw 'Chocolatey (choco) CLI was not found on this machine.'

    }            }

}            return if ($chocoCommand.Source) { $chocoCommand.Source } else { 'choco' }

        }

function Save-TidyResult {        'scoop' {

    if (-not $script:UsingResultFile) {            if (-not $scoopCommand) {

        return                throw 'Scoop CLI was not found on this machine.'

    }            }

            return if ($scoopCommand.Source) { $scoopCommand.Source } else { 'scoop' }

    if ($null -eq $script:ResultPayload) {        }

        return        default {

    }            throw "Unsupported package manager '$Key'."

        }

    $json = $script:ResultPayload | ConvertTo-Json -Depth 6    }

    Set-Content -LiteralPath $ResultPath -Value $json -Encoding UTF8}

}

function Normalize-VersionString {

function Test-TidyAdmin {    param(

    return [bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)        [string]$Value

}    )



function Get-TidyPowerShellExecutable {    if ([string]::IsNullOrWhiteSpace($Value)) {

    if ($PSVersionTable.PSEdition -eq 'Core') {        return $null

        $pwsh = Get-Command -Name 'pwsh' -ErrorAction SilentlyContinue    }

        if ($pwsh) { return $pwsh.Source }

    }    $trimmed = $Value.Trim()



    $legacy = Get-Command -Name 'powershell.exe' -ErrorAction SilentlyContinue    if ($trimmed -match '([0-9]+(?:\.[0-9]+)*)') {

    if ($legacy) { return $legacy.Source }        return $matches[1]

    }

    throw 'Unable to locate a PowerShell executable to request elevation.'

}    return $trimmed

}

function ConvertTo-TidyArgument {

    param([Parameter(Mandatory = $true)][string] $Value)function Get-Status {

    param(

    $escaped = $Value -replace '"', '""'        [string]$Installed,

    return "`"$escaped`""        [string]$Latest

}    )



function Request-TidyElevation {    $normalizedInstalled = Normalize-VersionString -Value $Installed

    param(    $normalizedLatest = Normalize-VersionString -Value $Latest

        [Parameter(Mandatory = $true)][string] $ScriptPath,

        [Parameter(Mandatory = $true)][string] $Manager,    if ([string]::IsNullOrWhiteSpace($normalizedInstalled)) {

        [Parameter(Mandatory = $true)][string] $PackageId,        return 'NotInstalled'

        [Parameter(Mandatory = $true)][string] $DisplayName    }

    )

    if ([string]::IsNullOrWhiteSpace($normalizedLatest) -or $normalizedLatest.Trim().ToLowerInvariant() -eq 'unknown') {

    $resultTemp = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "tidywindow-update-" + ([System.Guid]::NewGuid().ToString('N')) + '.json')        return 'Unknown'

    $shellPath = Get-TidyPowerShellExecutable    }



    $arguments = @(    $installedVersion = $null

        '-NoProfile',    $latestVersion = $null

        '-ExecutionPolicy', 'Bypass',    if ([version]::TryParse($normalizedInstalled, [ref]$installedVersion) -and [version]::TryParse($normalizedLatest, [ref]$latestVersion)) {

        '-File', (ConvertTo-TidyArgument -Value $ScriptPath),        if ($installedVersion -lt $latestVersion) {

        '-Manager', (ConvertTo-TidyArgument -Value $Manager),            return 'UpdateAvailable'

        '-PackageId', (ConvertTo-TidyArgument -Value $PackageId),        }

        '-DisplayName', (ConvertTo-TidyArgument -Value $DisplayName),        return 'UpToDate'

        '-RequiresAdmin',    }

        '-Elevated',

        '-ResultPath', (ConvertTo-TidyArgument -Value $resultTemp)    if ($normalizedInstalled -eq $normalizedLatest) {

    )        return 'UpToDate'

    }

    try {

        Start-Process -FilePath $shellPath -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait | Out-Null    return 'UpdateAvailable'

    }}

    catch {

        throw 'Administrator approval was denied or the request was cancelled.'function Get-WingetInstalledVersion {

    }    param(

        [string]$PackageId

    if (-not (Test-Path -LiteralPath $resultTemp)) {    )

        throw 'Administrator approval was denied before the update could start.'

    }    if (-not $wingetCommand) {

        return $null

    try {    }

        $json = Get-Content -LiteralPath $resultTemp -Raw -ErrorAction Stop

        return ConvertFrom-Json -InputObject $json -ErrorAction Stop    $exe = if ($wingetCommand.Source) { $wingetCommand.Source } else { 'winget' }

    }    $arguments = @('list', '--id', $PackageId, '-e', '--disable-interactivity', '--accept-source-agreements', '--output', 'json')

    finally {

        Remove-Item -LiteralPath $resultTemp -ErrorAction SilentlyContinue    try {

    }        $output = & $exe @arguments 2>$null

}        if ($LASTEXITCODE -eq 0 -and $output) {

            $json = ($output -join [Environment]::NewLine)

if ($needsElevation -and -not $Elevated.IsPresent -and -not (Test-TidyAdmin)) {            $data = ConvertFrom-Json -InputObject $json -ErrorAction Stop

    $scriptPath = $PSCommandPath            if ($data -and $data.InstalledPackages -and $data.InstalledPackages.Count -gt 0) {

    if ([string]::IsNullOrWhiteSpace($scriptPath)) {                $package = $data.InstalledPackages | Select-Object -First 1

        $scriptPath = $MyInvocation.MyCommand.Path                if ($package.Version) {

    }                    return $package.Version.Trim()

                }

    if ([string]::IsNullOrWhiteSpace($scriptPath)) {            }

        throw 'Unable to determine script path for elevation.'        }

    }    }

    catch {

    $result = Request-TidyElevation -ScriptPath $scriptPath -Manager $normalizedManager -PackageId $PackageId -DisplayName $DisplayName        # fall back to text parsing below

    $result | ConvertTo-Json -Depth 6    }

    return

}    try {

        $fallback = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null

function Resolve-ManagerExecutable {        foreach ($line in $fallback) {

    param([string] $Key)            if ($line -match '\s+' + [Regex]::Escape($PackageId) + '\s+([\w\.\-]+)') {

                return $matches[1].Trim()

    switch ($Key) {            }

        'winget' {        }

            $cmd = Get-Command -Name 'winget' -ErrorAction SilentlyContinue    }

            if (-not $cmd) { throw 'winget CLI was not found on this machine.' }    catch {

            return if ($cmd.Source) { $cmd.Source } else { 'winget' }        return $null

        }    }

        'choco' {

            $cmd = Get-Command -Name 'choco' -ErrorAction SilentlyContinue    return $null

            if (-not $cmd) { throw 'Chocolatey CLI was not found on this machine.' }}

            return if ($cmd.Source) { $cmd.Source } else { 'choco' }

        }function Get-WingetAvailableVersion {

        'chocolatey' {    param(

            $cmd = Get-Command -Name 'choco' -ErrorAction SilentlyContinue        [string]$PackageId

            if (-not $cmd) { throw 'Chocolatey CLI was not found on this machine.' }    )

            return if ($cmd.Source) { $cmd.Source } else { 'choco' }

        }    if (-not $wingetCommand) {

        'scoop' {        return $null

            $cmd = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue    }

            if (-not $cmd) { throw 'Scoop CLI was not found on this machine.' }

            return if ($cmd.Source) { $cmd.Source } else { 'scoop' }    $exe = if ($wingetCommand.Source) { $wingetCommand.Source } else { 'winget' }

        }    $arguments = @('show', '--id', $PackageId, '-e', '--disable-interactivity', '--accept-source-agreements', '--output', 'json')

        default { throw "Unsupported package manager '$Key'." }

    }    try {

}        $output = & $exe @arguments 2>$null

        if ($LASTEXITCODE -eq 0 -and $output) {

function Normalize-VersionString {            $json = ($output -join [Environment]::NewLine)

    param([string] $Value)            $data = ConvertFrom-Json -InputObject $json -ErrorAction Stop

            if ($data -and $data.Versions -and $data.Versions.Count -gt 0) {

    if ([string]::IsNullOrWhiteSpace($Value)) {                $latest = $data.Versions | Select-Object -First 1

        return $null                if ($latest.Version) {

    }                    return $latest.Version.Trim()

                }

    $trimmed = $Value.Trim()            }

    if ($trimmed -match '([0-9]+(?:\.[0-9]+)*)') {            elseif ($data -and $data.Version) {

        return $matches[1]                return $data.Version.Trim()

    }            }

        }

    return $trimmed    }

}    catch {

        # fall back to text parsing below

function Get-Status {    }

    param([string] $Installed, [string] $Latest)

    try {

    $normalizedInstalled = Normalize-VersionString -Value $Installed        $fallback = & $exe 'show' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null

    $normalizedLatest = Normalize-VersionString -Value $Latest        foreach ($line in $fallback) {

            if ($line -match '^\s*Version\s*:\s*(.+)$') {

    if ([string]::IsNullOrWhiteSpace($normalizedInstalled)) {                return $matches[1].Trim()

        return 'NotInstalled'            }

    }        }

    }

    if ([string]::IsNullOrWhiteSpace($normalizedLatest) -or $normalizedLatest.Trim().ToLowerInvariant() -eq 'unknown') {    catch {

        return 'Unknown'        return $null

    }    }



    $installedVersion = $null    return $null

    $latestVersion = $null}

    if ([version]::TryParse($normalizedInstalled, [ref]$installedVersion) -and [version]::TryParse($normalizedLatest, [ref]$latestVersion)) {

        if ($installedVersion -lt $latestVersion) {function Get-ChocoInstalledVersion {

            return 'UpdateAvailable'    param(

        }        [string]$PackageId

        return 'UpToDate'    )

    }

    if (-not $chocoCommand) {

    if ($normalizedInstalled -eq $normalizedLatest) {        return $null

        return 'UpToDate'    }

    }

    $exe = if ($chocoCommand.Source) { $chocoCommand.Source } else { 'choco' }

    return 'UpdateAvailable'

}    try {

        $output = & $exe 'list' $PackageId '--local-only' '--exact' '--limit-output' 2>$null

function Get-WingetInstalledVersion {        foreach ($line in $output) {

    param([string] $PackageId)            if ($line -match '^\s*' + [Regex]::Escape($PackageId) + '\|(.+)$') {

                return $matches[1].Trim()

    $command = Get-Command -Name 'winget' -ErrorAction SilentlyContinue            }

    if (-not $command) { return $null }        }

    }

    $exe = if ($command.Source) { $command.Source } else { 'winget' }    catch {

        return $null

    try {    }

        $jsonOutput = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' '--output' 'json' 2>$null

        if ($LASTEXITCODE -eq 0 -and $jsonOutput) {    return $null

            $data = ConvertFrom-Json -InputObject ($jsonOutput -join [Environment]::NewLine) -ErrorAction Stop}

            if ($data -and $data.InstalledPackages -and $data.InstalledPackages.Count -gt 0) {

                $package = $data.InstalledPackages | Select-Object -First 1function Get-ChocoAvailableVersion {

                if ($package.Version) { return $package.Version.Trim() }    param(

            }        [string]$PackageId

        }    )

    }

    catch { }    if (-not $chocoCommand) {

        return $null

    try {    }

        $fallback = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null

        foreach ($line in $fallback) {    $exe = if ($chocoCommand.Source) { $chocoCommand.Source } else { 'choco' }

            if ($line -match '\s+' + [System.Text.RegularExpressions.Regex]::Escape($PackageId) + '\s+([\w\.\-]+)') {

                return $matches[1].Trim()    try {

            }        $output = & $exe 'search' $PackageId '--exact' '--limit-output' 2>$null

        }        foreach ($line in $output) {

    }            if ($line -match '^\s*' + [Regex]::Escape($PackageId) + '\|(.+)$') {

    catch { }                return $matches[1].Trim()

            }

    return $null        }

}    }

    catch {

function Get-WingetAvailableVersion {        return $null

    param([string] $PackageId)    }



    $command = Get-Command -Name 'winget' -ErrorAction SilentlyContinue    return $null

    if (-not $command) { return $null }}



    $exe = if ($command.Source) { $command.Source } else { 'winget' }function Get-ScoopInstalledVersion {

    param(

    try {        [string]$PackageId

        $jsonOutput = & $exe 'show' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' '--output' 'json' 2>$null    )

        if ($LASTEXITCODE -eq 0 -and $jsonOutput) {

            $data = ConvertFrom-Json -InputObject ($jsonOutput -join [Environment]::NewLine) -ErrorAction Stop    if (-not $scoopCommand) {

            if ($data -and $data.Versions -and $data.Versions.Count -gt 0) {        return $null

                $latest = $data.Versions | Select-Object -First 1    }

                if ($latest.Version) { return $latest.Version.Trim() }

            }    $exe = if ($scoopCommand.Source) { $scoopCommand.Source } else { 'scoop' }

            elseif ($data -and $data.Version) {

                return $data.Version.Trim()    try {

            }        $output = & $exe 'list' '--json' 2>$null

        }        if ($LASTEXITCODE -ne 0 -or -not $output) {

    }            return $null

    catch { }        }



    try {        $json = ($output -join [Environment]::NewLine)

        $fallback = & $exe 'show' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null        $apps = ConvertFrom-Json -InputObject $json -ErrorAction Stop

        foreach ($line in $fallback) {        foreach ($app in $apps) {

            if ($line -match '^\s*Version\s*:\s*(.+)$') {            if ($app.Name -and ($app.Name -eq $PackageId)) {

                return $matches[1].Trim()                return $app.Version

            }            }

        }            if ($app.name -and ($app.name -eq $PackageId)) {

    }                return $app.version

    catch { }            }

        }

    return $null    }

}    catch {

        return $null

function Get-ChocoInstalledVersion {    }

    param([string] $PackageId)

    return $null

    $command = Get-Command -Name 'choco' -ErrorAction SilentlyContinue}

    if (-not $command) { return $null }

function Get-ScoopAvailableVersion {

    $exe = if ($command.Source) { $command.Source } else { 'choco' }    param(

        [string]$PackageId

    try {    )

        $output = & $exe 'list' $PackageId '--local-only' '--exact' '--limit-output' 2>$null

        foreach ($line in $output) {    if (-not $scoopCommand) {

            if ($line -match '^\s*' + [System.Text.RegularExpressions.Regex]::Escape($PackageId) + '\|(.+)$') {        return $null

                return $matches[1].Trim()    }

            }

        }    $exe = if ($scoopCommand.Source) { $scoopCommand.Source } else { 'scoop' }

    }

    catch { }    try {

        $output = & $exe 'info' $PackageId '--json' 2>$null

    return $null        if ($LASTEXITCODE -ne 0 -or -not $output) {

}            return $null

        }

function Get-ChocoAvailableVersion {

    param([string] $PackageId)        $json = ($output -join [Environment]::NewLine)

        $info = ConvertFrom-Json -InputObject $json -ErrorAction Stop

    $command = Get-Command -Name 'choco' -ErrorAction SilentlyContinue

    if (-not $command) { return $null }        if ($info -is [System.Collections.IDictionary]) {

            if ($info.App) {

    $exe = if ($command.Source) { $command.Source } else { 'choco' }                if ($info.App.Version) { return ($info.App.Version).ToString().Trim() }

                if ($info.App.'Latest Version') { return ($info.App.'Latest Version').ToString().Trim() }

    try {            }

        $output = & $exe 'search' $PackageId '--exact' '--limit-output' 2>$null

        foreach ($line in $output) {            if ($info.Version) { return $info.Version.ToString().Trim() }

            if ($line -match '^\s*' + [System.Text.RegularExpressions.Regex]::Escape($PackageId) + '\|(.+)$') {            if ($info.version) { return $info.version.ToString().Trim() }

                return $matches[1].Trim()        }

            }        elseif ($info -and $info.Version) {

        }            return $info.Version.ToString().Trim()

    }        }

    catch { }    }

    catch {

    return $null        # fall back to text parsing

}    }



function Get-ScoopInstalledVersion {    try {

    param([string] $PackageId)        $fallback = & $exe 'info' $PackageId 2>$null

        foreach ($line in $fallback) {

    $command = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue            if ($line -match '^\s*Latest Version\s*:\s*(.+)$') {

    if (-not $command) { return $null }                return $matches[1].Trim()

            }

    $exe = if ($command.Source) { $command.Source } else { 'scoop' }            if ($line -match '^\s*Version\s*:\s*(.+)$') {

                return $matches[1].Trim()

    try {            }

        $output = & $exe 'list' '--json' 2>$null        }

        if ($LASTEXITCODE -ne 0 -or -not $output) { return $null }    }

        $apps = ConvertFrom-Json -InputObject ($output -join [Environment]::NewLine) -ErrorAction Stop    catch {

        foreach ($app in $apps) {        return $null

            if ($app.Name -and ($app.Name -eq $PackageId)) { return $app.Version }    }

            if ($app.name -and ($app.name -eq $PackageId)) { return $app.version }

        }    return $null

    }}

    catch { }

function Get-ManagerInstalledVersion {

    return $null    param(

}        [string]$ManagerKey,

        [string]$PackageId

function Get-ScoopAvailableVersion {    )

    param([string] $PackageId)

    switch ($ManagerKey) {

    $command = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue        'winget' { return Get-WingetInstalledVersion -PackageId $PackageId }

    if (-not $command) { return $null }        'choco' { return Get-ChocoInstalledVersion -PackageId $PackageId }

        'chocolatey' { return Get-ChocoInstalledVersion -PackageId $PackageId }

    $exe = if ($command.Source) { $command.Source } else { 'scoop' }        'scoop' { return Get-ScoopInstalledVersion -PackageId $PackageId }

        default { return $null }

    try {    }

        $output = & $exe 'info' $PackageId '--json' 2>$null}

        if ($LASTEXITCODE -ne 0 -or -not $output) { return $null }

function Get-ManagerAvailableVersion {

        $info = ConvertFrom-Json -InputObject ($output -join [Environment]::NewLine) -ErrorAction Stop    param(

        if ($info -is [System.Collections.IDictionary]) {        [string]$ManagerKey,

            if ($info.App) {        [string]$PackageId

                if ($info.App.Version) { return ($info.App.Version).ToString().Trim() }    )

                if ($info.App.'Latest Version') { return ($info.App.'Latest Version').ToString().Trim() }

            }    switch ($ManagerKey) {

        'winget' { return Get-WingetAvailableVersion -PackageId $PackageId }

            if ($info.Version) { return $info.Version.ToString().Trim() }        'choco' { return Get-ChocoAvailableVersion -PackageId $PackageId }

            if ($info.version) { return $info.version.ToString().Trim() }        'chocolatey' { return Get-ChocoAvailableVersion -PackageId $PackageId }

        }        'scoop' { return Get-ScoopAvailableVersion -PackageId $PackageId }

        elseif ($info -and $info.Version) {        default { return $null }

            return $info.Version.ToString().Trim()    }

        }}

    }

    catch { }function Invoke-ManagerUpdate {

    param(

    try {        [string]$ManagerKey,

        $fallback = & $exe 'info' $PackageId 2>$null        [string]$PackageId

        foreach ($line in $fallback) {    )

            if ($line -match '^\s*Latest Version\s*:\s*(.+)$') { return $matches[1].Trim() }

            if ($line -match '^\s*Version\s*:\s*(.+)$') { return $matches[1].Trim() }    $exe = Resolve-ManagerExecutable -Key $ManagerKey

        }

    }    switch ($ManagerKey) {

    catch { }        'winget' {

            $arguments = @('upgrade', '--id', $PackageId, '-e', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')

    return $null        }

}        'choco' { $arguments = @('upgrade', $PackageId, '-y', '--no-progress') }

        'chocolatey' { $arguments = @('upgrade', $PackageId, '-y', '--no-progress') }

function Get-ManagerInstalledVersion {        'scoop' { $arguments = @('update', $PackageId) }

    param([string] $Key, [string] $PackageId)        default { throw "Unsupported package manager '$ManagerKey' for update." }

    }

    switch ($Key) {

        'winget' { return Get-WingetInstalledVersion -PackageId $PackageId }    $output = & $exe @arguments 2>&1

        'choco' { return Get-ChocoInstalledVersion -PackageId $PackageId }    $exitCode = $LASTEXITCODE

        'chocolatey' { return Get-ChocoInstalledVersion -PackageId $PackageId }

        'scoop' { return Get-ScoopInstalledVersion -PackageId $PackageId }    return [pscustomobject]@{

        default { return $null }        ExitCode = $exitCode

    }        Output = @($output)

}        Executable = $exe

        Arguments = $arguments

function Get-ManagerAvailableVersion {    }

    param([string] $Key, [string] $PackageId)}



    switch ($Key) {$installedBefore = Get-ManagerInstalledVersion -ManagerKey $managerKey -PackageId $PackageId

        'winget' { return Get-WingetAvailableVersion -PackageId $PackageId }$latestBefore = Get-ManagerAvailableVersion -ManagerKey $managerKey -PackageId $PackageId

        'choco' { return Get-ChocoAvailableVersion -PackageId $PackageId }if ([string]::IsNullOrWhiteSpace($latestBefore)) {

        'chocolatey' { return Get-ChocoAvailableVersion -PackageId $PackageId }    $latestBefore = 'Unknown'

        'scoop' { return Get-ScoopAvailableVersion -PackageId $PackageId }}

        default { return $null }

    }$statusBefore = Get-Status -Installed $installedBefore -Latest $latestBefore

}

$updateAttempted = $false

function Invoke-Update {$commandExitCode = 0

    param([string] $Key, [string] $PackageId)$commandOutput = @()

$commandExecutable = $null

    $exe = Resolve-ManagerExecutable -Key $Key$commandArguments = @()

    $arguments = switch ($Key) {

        'winget' { @('upgrade', '--id', $PackageId, '-e', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity') }if ($statusBefore -eq 'UpdateAvailable') {

        'choco' { @('upgrade', $PackageId, '-y', '--no-progress') }    $updateAttempted = $true

        'chocolatey' { @('upgrade', $PackageId, '-y', '--no-progress') }    $execution = Invoke-ManagerUpdate -ManagerKey $managerKey -PackageId $PackageId

        'scoop' { @('update', $PackageId) }    $commandExitCode = $execution.ExitCode

        default { throw "Unsupported package manager '$Key' for update." }    $commandOutput = $execution.Output

    }    $commandExecutable = $execution.Executable

    $commandArguments = $execution.Arguments

    $output = & $exe @arguments 2>&1}

    $exitCode = $LASTEXITCODEelse {

    $commandOutput = @('No update attempted because the package is not reporting an available update.')

    $logs = [System.Collections.Generic.List[string]]::new()}

    $errors = [System.Collections.Generic.List[string]]::new()

$installedAfter = Get-ManagerInstalledVersion -ManagerKey $managerKey -PackageId $PackageId

    foreach ($entry in @($output)) {$latestAfter = Get-ManagerAvailableVersion -ManagerKey $managerKey -PackageId $PackageId

        if ($null -eq $entry) { continue }if ([string]::IsNullOrWhiteSpace($latestAfter)) {

        if ($entry -is [System.Management.Automation.ErrorRecord]) {    $latestAfter = 'Unknown'

            $message = [string]$entry}

            if (-not [string]::IsNullOrWhiteSpace($message)) { [void]$errors.Add($message) }

        }$statusAfter = Get-Status -Installed $installedAfter -Latest $latestAfter

        else {

            $message = [string]$entry$result = [pscustomobject]@{

            if (-not [string]::IsNullOrWhiteSpace($message)) { [void]$logs.Add($message) }    Manager = $normalizedManager

        }    ManagerKey = $managerKey

    }    PackageId = $PackageId

    StatusBefore = $statusBefore

    $summary = if ($exitCode -eq 0) { 'Update command completed.' } else { "Update command exited with code $exitCode." }    StatusAfter = $statusAfter

    InstalledVersion = if ($installedAfter) { $installedAfter } else { $null }

    return [pscustomobject]@{    LatestVersion = $latestAfter

        Attempted = $true    UpdateAttempted = $updateAttempted

        ExitCode = $exitCode    ExitCode = $commandExitCode

        Output = $logs.ToArray()    Output = $commandOutput

        Errors = $errors.ToArray()    Executable = $commandExecutable

        Summary = $summary    Arguments = $commandArguments

    }}

}

$result | ConvertTo-Json -Depth 6

$installedBefore = Get-ManagerInstalledVersion -Key $managerKey -PackageId $PackageId
$latestBefore = Get-ManagerAvailableVersion -Key $managerKey -PackageId $PackageId
if (-not $latestBefore -and $installedBefore) {
    $latestBefore = $installedBefore
}

$statusBefore = Get-Status -Installed $installedBefore -Latest $latestBefore
$attempted = $false
$exitCode = 0
$operationSucceeded = $false
$summary = $null

try {
    if ($statusBefore -eq 'UpdateAvailable') {
        $attempt = Invoke-Update -Key $managerKey -PackageId $PackageId
        $attempted = $attempt.Attempted
        $exitCode = $attempt.ExitCode
        foreach ($line in $attempt.Output) { Add-TidyOutput -Message $line }
        foreach ($line in $attempt.Errors) { Add-TidyError -Message $line }
        if (-not [string]::IsNullOrWhiteSpace($attempt.Summary)) { $summary = $attempt.Summary }
        $operationSucceeded = $exitCode -eq 0
    }
    elseif ($statusBefore -eq 'UpToDate') {
        $summary = "Package '$DisplayName' is already up to date."
        $operationSucceeded = $true
    }
    elseif ($statusBefore -eq 'NotInstalled') {
        $summary = "Package '$DisplayName' is not installed."
        $operationSucceeded = $true
    }
    else {
        $summary = "No update attempted for '$DisplayName'."
        $operationSucceeded = $true
    }
}
catch {
    $operationSucceeded = $false
    $message = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($message)) { $message = $_.ToString() }
    Add-TidyError -Message $message
    if (-not $summary) { $summary = $message }
}

$installedAfter = Get-ManagerInstalledVersion -Key $managerKey -PackageId $PackageId
$latestAfter = Get-ManagerAvailableVersion -Key $managerKey -PackageId $PackageId
if (-not $latestAfter -and $installedAfter) {
    $latestAfter = $installedAfter
}

$statusAfter = Get-Status -Installed $installedAfter -Latest $latestAfter

if ($statusBefore -eq 'UpdateAvailable') {
    if ($statusAfter -eq 'UpToDate' -and $exitCode -eq 0) {
        $operationSucceeded = $true
        if (-not $summary) { $summary = "Package '$DisplayName' updated." }
    }
    elseif ($statusAfter -eq 'UpToDate' -and $exitCode -ne 0) {
        $summary = "Package '$DisplayName' reports updated but exit code $exitCode indicates issues."
        $operationSucceeded = $false
    }
    elseif ($statusAfter -eq 'UpdateAvailable') {
        if (-not $summary) { $summary = "Package '$DisplayName' still has an update available." }
        $operationSucceeded = $false
    }
}

if ([string]::IsNullOrWhiteSpace($summary)) {
    $summary = if ($operationSucceeded) { "Update completed for '$DisplayName'." } else { "Update failed for '$DisplayName'." }
}

$installedResult = if ([string]::IsNullOrWhiteSpace($installedAfter)) { $installedBefore } else { $installedAfter }
$latestResult = if ([string]::IsNullOrWhiteSpace($latestAfter)) { $latestBefore } else { $latestAfter }
if ([string]::IsNullOrWhiteSpace($installedResult)) { $installedResult = $null }
if ([string]::IsNullOrWhiteSpace($latestResult)) { $latestResult = 'Unknown' }

$script:ResultPayload = [pscustomobject]@{
    operation = 'update'
    manager = $normalizedManager
    packageId = $PackageId
    displayName = $DisplayName
    requiresAdmin = $needsElevation
    statusBefore = $statusBefore
    statusAfter = $statusAfter
    installedVersion = $installedResult
    latestVersion = $latestResult
    updateAttempted = [bool]$attempted
    exitCode = [int]$exitCode
    succeeded = [bool]$operationSucceeded
    summary = $summary
    output = $script:TidyOutput
    errors = $script:TidyErrors
}

try {
    Save-TidyResult
}
finally {
    $script:ResultPayload | ConvertTo-Json -Depth 6
}
