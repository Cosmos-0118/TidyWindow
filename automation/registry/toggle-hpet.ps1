[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

function Invoke-BcdEditCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Arguments,
        [switch] $IgnoreMissing
    )

    $result = Invoke-TidyCommandLine -CommandLine ("bcdedit {0}" -f $Arguments)
    if ($result.exitCode -ne 0) {
        $errorSegment = if (-not [string]::IsNullOrWhiteSpace($result.errors)) { $result.errors } elseif (-not [string]::IsNullOrWhiteSpace($result.output)) { $result.output } else { 'Unknown error' }
        $combined = $errorSegment.Trim()
        if ($IgnoreMissing.IsPresent -and $combined -match 'does not exist|not found|not recognized') {
            Write-RegistryOutput ("bcdedit {0} skipped: {1}" -f $Arguments, $combined)
        }
        else {
            throw ("bcdedit {0} failed: {1}" -f $Arguments, $combined)
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($result.output)) {
        Write-RegistryOutput $result.output.Trim()
    }
}

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'HPET scheduling policy'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent

    if ($apply) {
        Invoke-BcdEditCommand '/deletevalue useplatformclock' -IgnoreMissing
        Invoke-BcdEditCommand '/set disabledynamictick yes'
        Write-RegistryOutput 'HPET usage cleared and dynamic tick disabled.'
    }
    else {
        Invoke-BcdEditCommand '/set useplatformclock true'
        Invoke-BcdEditCommand '/deletevalue disabledynamictick' -IgnoreMissing
        Write-RegistryOutput 'HPET scheduling restored to Windows defaults.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
