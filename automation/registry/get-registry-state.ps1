[CmdletBinding(SupportsShouldProcess = $false)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $RegistryPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ValueName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ValueType,

    [Parameter(Mandatory = $false)]
    [AllowEmptyString()]
    [string] $RecommendedValue,

    [Parameter(Mandatory = $false)]
    [bool] $SupportsCustomValue = $false,

    [Parameter(Mandatory = $false)]
    [string] $ResultPath,

    [Parameter(Mandatory = $false)]
    [AllowEmptyString()]
    [string] $LookupValueName,

    [Parameter(Mandatory = $false)]
    [AllowEmptyString()]
    [string] $UserSid
)

. "$PSScriptRoot\registry-common.ps1"

    function Resolve-ComparableValue {
        param(
            [Parameter(Mandatory = $false)]
            [object] $Value,

            [Parameter(Mandatory = $true)]
            [ValidateNotNullOrEmpty()]
            [string] $ValueType
        )

        if ($null -eq $Value) {
            return $null
        }

        switch ($ValueType.ToLowerInvariant()) {
            'dword'      { return [int]$Value }
            'qword'      { return [long]$Value }
            'binary'     { return $Value }
            'multistring' {
                if ($Value -is [System.Array]) { return @($Value) }
                return @([string]$Value)
            }
            default      { return [string]$Value }
        }
    }

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Registry state discovery'

try {
    $resolvedPath = Resolve-RegistryUserPath -Path $RegistryPath -UserSid $UserSid

    $itemPath = if ($resolvedPath.StartsWith('HK', [System.StringComparison]::OrdinalIgnoreCase) -or $resolvedPath.StartsWith('Registry::', [System.StringComparison]::OrdinalIgnoreCase)) {
        $resolvedPath
    }
    else {
        throw "RegistryPath must include the hive prefix (e.g. 'HKCU:\\...')."
    }

    $effectiveValueName = if ([string]::IsNullOrWhiteSpace($LookupValueName)) { $ValueName } else { $LookupValueName }

    $pathsToQuery = @()
    if ($itemPath.EndsWith('\*')) {
        $basePath = $itemPath.Substring(0, $itemPath.Length - 2)
        if (Test-Path -LiteralPath $basePath) {
            try {
                $pathsToQuery = Get-ChildItem -LiteralPath $basePath -ErrorAction Stop | ForEach-Object { Join-Path -Path $basePath -ChildPath $_.PSChildName }
            }
            catch {
                $pathsToQuery = @()
            }
        }

        if (-not $pathsToQuery) {
            Write-RegistryOutput "No registry subkeys found beneath '$basePath'."
        }
    }
    elseif ($itemPath.Contains('*')) {
        try {
            $pathsToQuery = Get-ChildItem -Path $itemPath -ErrorAction Stop | ForEach-Object { $_.PSPath -replace '^Microsoft\.PowerShell\.Core\\Registry::', '' }
        }
        catch {
            $pathsToQuery = @()
        }

        if (-not $pathsToQuery) {
            Write-RegistryOutput "No registry paths matched wildcard '$itemPath'."
        }
    }
    else {
        $pathsToQuery = @($itemPath)
    }

    $collected = @()
    foreach ($path in $pathsToQuery) {
        try {
            $property = Get-ItemProperty -LiteralPath $path -Name $effectiveValueName -ErrorAction Stop
            $rawValue = $property.$effectiveValueName
            $value = Resolve-ComparableValue -Value $rawValue -ValueType $ValueType
            $display = Format-RegistryValue $value
            $collected += [pscustomobject]@{
                Path    = $path
                Value   = $value
                Display = $display
            }
            Write-RegistryOutput ("{0}::{1} = {2}" -f $path, $effectiveValueName, $display)
        }
        catch {
            $display = Format-RegistryValue $null
            $collected += [pscustomobject]@{
                Path    = $path
                Value   = $null
                Display = $display
            }
            Write-RegistryOutput ("{0}::{1} is not set." -f $path, $effectiveValueName)
        }
    }

    if (-not $collected) {
        $collected = @([pscustomobject]@{ Path = $itemPath; Value = $null; Display = Format-RegistryValue $null })
    }

    $hasSingleValue = $collected.Count -eq 1
    $currentValue = if ($hasSingleValue) { $collected[0].Value } else { @($collected | ForEach-Object { $_.Value }) }
    $currentDisplay = if ($hasSingleValue) { $collected[0].Display } else { @($collected | ForEach-Object { $_.Display }) }

    $expectedComparable = $null
    $expectedDisplay = $null
    $matchesRecommendation = $null
    if ($PSBoundParameters.ContainsKey('RecommendedValue')) {
        $expectedComparable = Resolve-ComparableValue -Value $RecommendedValue -ValueType $ValueType
        $expectedDisplay = Format-RegistryValue $expectedComparable

        $matchesRecommendation = $true
        foreach ($entry in $collected) {
            if (Format-RegistryValue $entry.Value -ne $expectedDisplay) {
                $matchesRecommendation = $false
                break
            }
        }
    }

    $resultModel = [pscustomobject]@{
        Path                = $itemPath
        ValueName           = $ValueName
        LookupValueName     = $effectiveValueName
        ValueType           = $ValueType
        SupportsCustomValue = $SupportsCustomValue
        CurrentValue        = $currentValue
        CurrentDisplay      = $currentDisplay
        Values              = $collected
        RecommendedValue    = $expectedComparable
        RecommendedDisplay  = $expectedDisplay
        IsRecommendedState  = $matchesRecommendation
    }

    if ($hasSingleValue) {
        Write-RegistryOutput ("Current value    : {0}" -f $currentDisplay)
    }
    else {
        Write-RegistryOutput 'Current values:'
        foreach ($entry in $collected) {
            Write-RegistryOutput ("  {0} :: {1}" -f $entry.Path, $entry.Display)
        }
    }
    if ($PSBoundParameters.ContainsKey('RecommendedValue')) {
        $display = if ($null -ne $expectedDisplay) { $expectedDisplay } else { $RecommendedValue }
        Write-RegistryOutput ("Recommended value: {0}" -f $display)
        if ($matchesRecommendation -eq $true) {
            Write-RegistryOutput 'State matches recommendation.'
        }
        elseif ($matchesRecommendation -eq $false) {
            Write-RegistryOutput 'State differs from recommendation.'
        }
    }

    $payload = $resultModel | ConvertTo-Json -Depth 5
    if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
        Set-Content -Path $ResultPath -Value $payload -Encoding UTF8
    }
    else {
        $payload
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
