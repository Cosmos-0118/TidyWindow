param(
    [Parameter(Mandatory = $true)]
    [string] $Manager,
    [switch] $Elevated,
    [string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
        [string] $Message
    )

    if ([string]::IsNullOrWhiteSpace($Message)) {
        return
    }

    [void]$script:TidyOutputLines.Add($Message)
    Write-Output $Message
}

function Write-TidyError {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if ([string]::IsNullOrWhiteSpace($Message)) {
        return
    }

    [void]$script:TidyErrorLines.Add($Message)
    Write-Error -Message $Message
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

function Test-TidyCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommandName
    )

    $null -ne (Get-Command -Name $CommandName -ErrorAction SilentlyContinue)
}

function Invoke-ScoopBootstrap {
    if (Test-TidyCommand -CommandName 'scoop') {
        Write-TidyLog -Level Information -Message 'Scoop detected. Running update to repair installation.'
        scoop update
        return 'Scoop update command completed.'
    }

    Write-TidyLog -Level Information -Message 'Installing Scoop for the current user.'
    Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force
    Invoke-RestMethod -Uri 'https://get.scoop.sh' | Invoke-Expression
    return 'Scoop installation command completed.'
}

function Test-TidyAdmin {
    return [bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-TidyPowerShellExecutable {
    if ($PSVersionTable.PSEdition -eq 'Core') {
        $pwsh = Get-Command -Name 'pwsh' -ErrorAction SilentlyContinue
        if ($pwsh) {
            return $pwsh.Source
        }
    }

    $legacy = Get-Command -Name 'powershell.exe' -ErrorAction SilentlyContinue
    if ($legacy) {
        return $legacy.Source
    }

    throw 'Unable to locate a PowerShell executable to request elevation.'
}

function Request-ChocolateyElevation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ManagerName
    )

    $tempPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "tidywindow-choco-" + ([System.Guid]::NewGuid().ToString('N')) + '.json')

    $shellPath = Get-TidyPowerShellExecutable
    $escapedScript = $callerModulePath -replace "'", "''"
    $escapedManager = $ManagerName -replace "'", "''"
    $escapedResult = $tempPath -replace "'", "''"
    $command = "& '$escapedScript' -Manager '$escapedManager' -Elevated -ResultPath '$escapedResult'"

    Write-TidyLog -Level Information -Message 'Requesting administrator approval for Chocolatey install or repair.'
    Write-TidyOutput -Message 'Requesting administrator approval. Windows may prompt for permission.'

    try {
        $process = Start-Process -FilePath $shellPath -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $command) -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    }
    catch {
        throw 'Administrator approval was denied or cancelled.'
    }

    if (-not (Test-Path -Path $tempPath)) {
        throw 'Administrator approval was denied before the operation could start.'
    }

    try {
        $json = Get-Content -Path $tempPath -Raw -ErrorAction Stop
        $result = ConvertFrom-Json -InputObject $json -ErrorAction Stop
    }
    finally {
        Remove-Item -Path $tempPath -ErrorAction SilentlyContinue
    }

    return $result
}

function Invoke-ChocolateyBootstrap {
    if ((-not (Test-TidyAdmin)) -and (-not $Elevated.IsPresent)) {
        $elevationResult = Request-ChocolateyElevation -ManagerName $Manager

        if ($null -eq $elevationResult) {
            throw 'Failed to capture the elevated Chocolatey result.'
        }

        $outputLines = @($elevationResult.Output)
        $errorLines = @($elevationResult.Errors)

        if ($outputLines) {
            foreach ($line in $outputLines) {
                Write-TidyOutput -Message $line
            }
        }

        if ($errorLines) {
            foreach ($line in $errorLines) {
                Write-TidyError -Message $line
            }
        }

        if (-not $elevationResult.Success) {
            throw 'Chocolatey install or repair failed when running with administrator privileges.'
        }

        if ($outputLines.Count -gt 0) {
            return $outputLines[$outputLines.Count - 1]
        }

        return 'Chocolatey operation completed with administrator privileges.'
    }

    if (Test-TidyCommand -CommandName 'choco') {
        Write-TidyLog -Level Information -Message 'Chocolatey detected. Running upgrade to repair installation.'
        choco upgrade chocolatey -y
        return 'Chocolatey upgrade command completed.'
    }

    Write-TidyLog -Level Information -Message 'Installing Chocolatey. This process can take a few minutes.'
    Set-ExecutionPolicy Bypass -Scope Process -Force
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
    iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
    return 'Chocolatey installation command completed.'
}

$normalized = ($Manager ?? '').Trim().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($normalized)) {
    throw 'Manager name must be provided.'
}

try {
    switch ($normalized) {
        'scoop' {
            $message = Invoke-ScoopBootstrap
            Write-TidyOutput -Message $message
        }
        'scoop package manager' {
            $message = Invoke-ScoopBootstrap
            Write-TidyOutput -Message $message
        }
        'choco' {
            $message = Invoke-ChocolateyBootstrap
            Write-TidyOutput -Message $message
        }
        'chocolatey' {
            $message = Invoke-ChocolateyBootstrap
            Write-TidyOutput -Message $message
        }
        'chocolatey cli' {
            $message = Invoke-ChocolateyBootstrap
            Write-TidyOutput -Message $message
        }
        'winget' {
            Write-TidyLog -Level Warning -Message 'winget is distributed by Microsoft and cannot be installed via automation. Visit the Store to reinstall if required.'
            Write-TidyOutput -Message 'winget is managed by Windows. Use the Microsoft Store to repair or reinstall the App Installer package.'
        }
        default {
            throw "Package manager '$Manager' is not supported by the installer."
        }
    }
}
catch {
    $script:OperationSucceeded = $false
    $script:TidyErrorLines.Add($_.Exception.Message)
    if (-not $script:UsingResultFile) {
        throw
    }
}
finally {
    Save-TidyResult
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
