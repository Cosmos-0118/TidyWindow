function Remove-TidyAnsiSequences {
    # Strips ANSI/VT escape codes so UI output remains readable.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string] $Text
    )

    if (-not $env:NO_COLOR) { $env:NO_COLOR = '1' }

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $Text
    }

    $patterns = @(
        '\u001B\[[0-9;?]*[ -/]*[@-~]', # CSI sequences like ESC[31m
        '\u001B\][^\a]*\u0007',      # OSC sequences like ESC]0;...
        '\u009B[0-9;?]*[ -/]*[@-~]',   # 8-bit CSI
        '\u001B',                      # stray ESC
        '\u009B'                       # stray 8-bit CSI
    )

    $clean = $Text
    foreach ($pattern in $patterns) {
        $clean = [System.Text.RegularExpressions.Regex]::Replace($clean, $pattern, '')
    }

    return $clean
}

function Remove-TidyControlChars {
    # Removes non-printable control characters while preserving newlines and tabs for readability.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string] $Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $Text
    }

    return [System.Text.RegularExpressions.Regex]::Replace($Text, '[\x00-\x08\x0B\x0C\x0E-\x1F]', '')
}

function Limit-TidyLength {
    # Protects against runaway or verbose payloads overwhelming logs.
    [CmdletBinding()]
    param(
        [string] $Text,
        [int] $MaxLength = 4000
    )

    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    if ($Text.Length -le $MaxLength) { return $Text }
    return ($Text.Substring(0, $MaxLength) + '... [truncated]')
}

function Convert-TidyErrorRecord {
    # Produces a compact, consistent error string from ErrorRecord/Exception objects.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object] $InputObject
    )

    $parts = @()

    $errRecord = $null
    $exception = $null

    if ($InputObject -is [System.Management.Automation.ErrorRecord]) {
        $errRecord = $InputObject
        $exception = $errRecord.Exception
    }
    elseif ($InputObject -is [Exception]) {
        $exception = $InputObject
    }

    $messageText = $null
    if ($exception -and $exception.Message) {
        $messageText = $exception.Message
    }
    elseif ($errRecord -and $errRecord.ToString()) {
        $messageText = $errRecord.ToString()
    }
    elseif ($InputObject) {
        $messageText = $InputObject.ToString()
    }

    if ($messageText) {
        $parts += "Message: $messageText"
    }

    if ($exception -and $exception.GetType()) {
        $parts += ("Exception: {0}" -f $exception.GetType().FullName)
    }

    if ($errRecord) {
        if ($errRecord.FullyQualifiedErrorId) {
            $parts += ("FQID: {0}" -f $errRecord.FullyQualifiedErrorId)
        }
        if ($errRecord.CategoryInfo) {
            $parts += ("Category: {0}" -f $errRecord.CategoryInfo.ToString())
        }
        if ($errRecord.TargetObject) {
            $parts += ("Target: {0}" -f (Convert-TidyLogMessage -InputObject $errRecord.TargetObject))
        }
        if ($errRecord.InvocationInfo -and $errRecord.InvocationInfo.PositionMessage) {
            $pos = ($errRecord.InvocationInfo.PositionMessage -split [Environment]::NewLine | ForEach-Object { $_.Trim() }) -join ' '
            if (-not [string]::IsNullOrWhiteSpace($pos)) {
                $parts += ("Position: {0}" -f $pos)
            }
        }
    }

    if ($exception -and $exception.InnerException -and $exception.InnerException.Message) {
        $parts += ("Inner: {0}" -f $exception.InnerException.Message)
    }

    $text = ($parts -join ' | ').Trim()
    $text = Remove-TidyAnsiSequences -Text $text
    $text = Remove-TidyControlChars -Text $text
    return Limit-TidyLength -Text $text
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

    if ($InputObject -is [System.Management.Automation.ErrorRecord] -or $InputObject -is [Exception]) {
        return Convert-TidyErrorRecord -InputObject $InputObject
    }

    if ($InputObject -is [string]) {
        $clean = Remove-TidyAnsiSequences -Text $InputObject
        $clean = Remove-TidyControlChars -Text $clean
        return Limit-TidyLength -Text $clean
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
        $converted = Remove-TidyAnsiSequences -Text $converted
        $converted = Remove-TidyControlChars -Text $converted
        return Limit-TidyLength -Text $converted
    }
    catch {
        $fallback = ($InputObject | Out-String).TrimEnd()
        $fallback = Remove-TidyAnsiSequences -Text $fallback
        $fallback = Remove-TidyControlChars -Text $fallback
        return Limit-TidyLength -Text $fallback
    }
}

# Force plain text rendering so host does not inject ANSI color sequences into errors.
try {
    if ($PSStyle -and ($PSStyle.OutputRendering -ne 'PlainText')) {
        $PSStyle.OutputRendering = 'PlainText'
    }
}
catch {
    # Best-effort: ignore if PSStyle not available (Windows PowerShell 5.1).
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

        Write-Output "[$timestamp][$Level] $text"
    }
}

function Write-TidyInfo {
    [CmdletBinding()] param([Parameter(Mandatory=$true, ValueFromRemainingArguments=$true)][object[]] $Message)
    Write-TidyLog -Level Information -Message $Message
}

function Write-TidyWarning {
    [CmdletBinding()] param([Parameter(Mandatory=$true, ValueFromRemainingArguments=$true)][object[]] $Message)
    Write-TidyLog -Level Warning -Message $Message
}

function Write-TidyError {
    [CmdletBinding()] param([Parameter(Mandatory=$true, ValueFromRemainingArguments=$true)][object[]] $Message)
    Write-TidyLog -Level Error -Message $Message
}

