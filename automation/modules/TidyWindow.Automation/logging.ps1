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
            $key = '<unnamed>'
            if (-not [string]::IsNullOrEmpty($prop.Name)) {
                $key = $prop.Name
            }
            $value = Convert-TidyLogMessage -InputObject $prop.Value
            "$key=$value"
        }

        return $pairs -join '; '
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        $pairs = @()
        foreach ($entry in $InputObject.GetEnumerator()) {
            $key = '<null>'
            if ($null -ne $entry.Key) {
                $key = $entry.Key.ToString()
            }
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

