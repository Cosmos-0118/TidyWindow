Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Get-Module -Name 'TidyWindow.Automation')) {
    $modulePath = Join-Path -Path $PSScriptRoot -ChildPath '..\modules\TidyWindow.Automation.psm1'
    $modulePath = [System.IO.Path]::GetFullPath($modulePath)
    if (-not (Test-Path -Path $modulePath)) {
        throw "Automation module not found at path '$modulePath'."
    }

    Import-Module $modulePath -Force
}

$script:RegistryCmdlet = $null
$script:RegistryOperationName = 'Registry tweak'
$script:RegistryResultPath = $null
$script:RegistryOutputLines = $null
$script:RegistryErrorLines = $null
$script:RegistrySucceeded = $true
$script:RegistrySummaryWritten = $false

function Initialize-RegistryScript {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCmdlet] $Cmdlet,
        [string] $ResultPath,
        [string] $OperationName
    )

    $script:RegistryCmdlet = $Cmdlet
    $script:RegistryOperationName = if ([string]::IsNullOrWhiteSpace($OperationName)) { 'Registry tweak' } else { $OperationName }
    $script:RegistryOutputLines = [System.Collections.Generic.List[string]]::new()
    $script:RegistryErrorLines = [System.Collections.Generic.List[string]]::new()
    $script:RegistrySucceeded = $true
    $script:RegistrySummaryWritten = $false

    if ([string]::IsNullOrWhiteSpace($ResultPath)) {
        $script:RegistryResultPath = $null
    }
    else {
        $script:RegistryResultPath = [System.IO.Path]::GetFullPath($ResultPath)
    }

    Write-TidyLog -Level Information -Message "Starting $script:RegistryOperationName."
}

function Write-RegistryOutput {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    if ($null -eq $script:RegistryOutputLines) {
        $script:RegistryOutputLines = [System.Collections.Generic.List[string]]::new()
    }

    [void]$script:RegistryOutputLines.Add($text)
    Write-Output $text
}

function Write-RegistryError {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    if ($null -eq $script:RegistryErrorLines) {
        $script:RegistryErrorLines = [System.Collections.Generic.List[string]]::new()
    }

    $script:RegistrySucceeded = $false
    [void]$script:RegistryErrorLines.Add($text)
    Write-Error -Message $text
}

function Format-RegistryValue {
    param([object] $Value)

    if ($null -eq $Value) {
        return '<not set>'
    }

    if ($Value -is [System.Array]) {
        return ($Value | ForEach-Object { if ($null -eq $_) { '<null>' } else { $_.ToString() } }) -join ', '
    }

    return $Value.ToString()
}

function Set-RegistryValue {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [object] $Value,
        [ValidateSet('String', 'ExpandString', 'DWord', 'QWord', 'Binary', 'MultiString')]
        [string] $Type = 'String'
    )

    $oldValue = $null
    try {
        $existing = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop
        $oldValue = $existing.$Name
    }
    catch {
        $oldValue = $null
    }

    switch ($Type) {
        'String' { $coercedValue = [string]$Value }
        'ExpandString' { $coercedValue = [string]$Value }
        'DWord' { $coercedValue = [int]$Value }
        'QWord' { $coercedValue = [long]$Value }
        'Binary' { $coercedValue = [byte[]]$Value }
        'MultiString' {
            if ($Value -is [System.Array]) {
                $coercedValue = @()
                foreach ($item in $Value) {
                    $coercedValue += [string]$item
                }
            }
            else {
                $coercedValue = @([string]$Value)
            }
        }
        default { $coercedValue = $Value }
    }

    if ($script:RegistryCmdlet.ShouldProcess("$Path::$Name", "Set to $coercedValue")) {
        if (-not (Test-Path -LiteralPath $Path)) {
            New-Item -Path $Path -Force | Out-Null
        }

        if ($null -ne $oldValue) {
            Set-ItemProperty -LiteralPath $Path -Name $Name -Value $coercedValue -ErrorAction Stop | Out-Null
        }
        else {
            New-ItemProperty -LiteralPath $Path -Name $Name -Value $coercedValue -PropertyType $Type -Force -ErrorAction Stop | Out-Null
        }
    }

    return [pscustomobject]@{
        Path      = $Path
        Name      = $Name
        OldValue  = $oldValue
        NewValue  = $coercedValue
        ValueType = $Type
    }
}

function Remove-RegistryValue {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $oldValue = $null
    try {
        $existing = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop
        $oldValue = $existing.$Name
    }
    catch {
        return [pscustomobject]@{
            Path      = $Path
            Name      = $Name
            OldValue  = $null
            NewValue  = $null
            ValueType = $null
        }
    }

    if ($script:RegistryCmdlet.ShouldProcess("$Path::$Name", 'Remove value')) {
        Remove-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop
    }

    return [pscustomobject]@{
        Path      = $Path
        Name      = $Name
        OldValue  = $oldValue
        NewValue  = $null
        ValueType = $null
    }
}

function Register-RegistryChange {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Change,
        [string] $Description
    )

    if ($Description) {
        Write-RegistryOutput $Description
    }

    Write-RegistryOutput ("  Path : {0}" -f $Change.Path)
    Write-RegistryOutput ("  Name : {0}" -f $Change.Name)
    Write-RegistryOutput ("  From : {0}" -f (Format-RegistryValue $Change.OldValue))
    Write-RegistryOutput ("  To   : {0}" -f (Format-RegistryValue $Change.NewValue))
}

function Complete-RegistryScript {
    if ($script:RegistrySummaryWritten) {
        return
    }

    $script:RegistrySummaryWritten = $true

    $summaryLines = @()
    if ($script:RegistryOutputLines) {
        $summaryLines += $script:RegistryOutputLines
    }

    if ($script:RegistryErrorLines) {
        $summaryLines += $script:RegistryErrorLines
    }

    if ($script:RegistryResultPath) {
        $payload = [pscustomobject]@{
            Success = $script:RegistrySucceeded
            Output  = $script:RegistryOutputLines
            Errors  = $script:RegistryErrorLines
        }

        $json = $payload | ConvertTo-Json -Depth 5
        Set-Content -Path $script:RegistryResultPath -Value $json -Encoding UTF8
    }

    $messageSegments = @()
    if ($script:RegistrySucceeded) {
        $messageSegments += "$script:RegistryOperationName completed."
        if ($summaryLines) { $messageSegments += $summaryLines }
        Write-TidyLog -Level Information -Message $messageSegments
    }
    else {
        $messageSegments += "$script:RegistryOperationName completed with errors."
        if ($summaryLines) { $messageSegments += $summaryLines }
        Write-TidyLog -Level Error -Message $messageSegments
    }
}
