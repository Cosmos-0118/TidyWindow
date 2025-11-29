[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

function Invoke-PowerCfgCommand {
    param([Parameter(Mandatory = $true)][string] $Arguments)

    $result = Invoke-TidyCommandLine -CommandLine ("powercfg {0}" -f $Arguments)
    if ($result.exitCode -ne 0) {
        $errorText = if (-not [string]::IsNullOrWhiteSpace($result.errors)) { $result.errors } elseif (-not [string]::IsNullOrWhiteSpace($result.output)) { $result.output } else { 'Unknown error' }
        throw ("powercfg {0} failed: {1}" -f $Arguments, $errorText)
    }

    if (-not [string]::IsNullOrWhiteSpace($result.output)) {
        Write-RegistryOutput $result.output.Trim()
    }
}

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'USB selective suspend policy'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $value = if ($apply) { 0 } else { 1 }
    $subGroup = '2a737441-1930-4402-8d77-b2bebba308a3'
    $setting = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'

    Invoke-PowerCfgCommand "-setacvalueindex scheme_current $subGroup $setting $value"
    Invoke-PowerCfgCommand "-setdcvalueindex scheme_current $subGroup $setting $value"
    Invoke-PowerCfgCommand '-setactive scheme_current'

    if ($apply) {
        Write-RegistryOutput 'USB selective suspend disabled for active power plan.'
    }
    else {
        Write-RegistryOutput 'USB selective suspend restored to system default.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
