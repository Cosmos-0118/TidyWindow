Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-TidyArray {
    param([object] $Input)

    if ($null -eq $Input) {
        return @()
    }

    if ($Input -is [string]) {
        return ,$Input
    }

    if ($Input -is [System.Collections.IEnumerator]) {
        $enumerator = $Input
        $buffer = [System.Collections.Generic.List[object]]::new()
        try {
            while ($enumerator.MoveNext()) {
                $buffer.Add($enumerator.Current)
            }
        }
        finally {
            if ($enumerator -is [System.IDisposable]) {
                $enumerator.Dispose()
            }
        }

        return $buffer.ToArray()
    }

    if ($Input -is [System.Collections.IEnumerable]) {
        $buffer = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $Input) {
            $buffer.Add($item)
        }

        return $buffer.ToArray()
    }

    return ,$Input
}

function Normalize-HardwareIds {
    param([object] $Input)

    $results = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in (ConvertTo-TidyArray -Input $Input)) {
        if ($null -eq $candidate) { continue }
        $text = $candidate.ToString().Trim()
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            [void]$results.Add($text)
        }
    }

    return $results.ToArray()
}

function Get-InstalledDriverInventory {
    $lookup = [System.Collections.Generic.Dictionary[string, psobject]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $inventory = [System.Collections.Generic.List[psobject]]::new()

    try {
        $installed = Get-CimInstance -ClassName Win32_PnPSignedDriver -ErrorAction Stop
        Write-Host ("Raw CIM type: {0}" -f ($installed.GetType().FullName))
        Write-Host ("Is enumerable: {0}" -f ($installed -is [System.Collections.IEnumerable]))
        if ($installed -is [System.Collections.IEnumerable]) {
            $count = 0
            foreach ($item in $installed) { $count++ }
            Write-Host ("Enumerable reported {0} item(s)." -f $count)
        }
    }
    catch {
        Write-Error -Message ('Unable to inventory installed drivers: {0}' -f $_.Exception.Message)
        return [pscustomobject]@{ Lookup = $lookup; Inventory = $inventory }
    }

    try {
        $convertedInstalled = ConvertTo-TidyArray -Input $installed
    }
    catch {
        Write-Error -Message ("ConvertTo-TidyArray failed: {0}" -f $_.Exception.Message)
        return
    }

    if ($null -eq $convertedInstalled) {
        Write-Host 'Converted result is null.'
    }
    else {
        Write-Host ("Converted type: {0}" -f $convertedInstalled.GetType().FullName)
        $convertedCount = if ($convertedInstalled -is [System.Collections.ICollection]) { $convertedInstalled.Count } elseif ($convertedInstalled -is [System.Array]) { $convertedInstalled.Length } else { ($convertedInstalled | Measure-Object).Count }
        Write-Host ("Converted count: {0}" -f $convertedCount)
    }
    $processed = 0
    foreach ($entry in $convertedInstalled) {
        if ($null -eq $entry) { continue }
        $processed++

        $hardwareIds = Normalize-HardwareIds -Input $entry.HardwareID
        $driverDate = $entry.DriverDate
        $installDate = $null
        if ($entry.PSObject.Properties['DriverDate']) {
            $installDate = $driverDate
        }
        if ($entry.PSObject.Properties['Date']) {
            $possible = $entry.Date
            if ($possible) { $installDate = $possible }
        }

        $status = 'Unknown'
        $problemCode = $null
        if ($entry.PSObject.Properties['DeviceProblemCode']) {
            $problemCode = $entry.DeviceProblemCode
        }
        if ($null -ne $problemCode) {
            if ($problemCode -eq 0) { $status = 'Working' } else { $status = "ProblemCode $problemCode" }
        }

        $detail = [pscustomobject]@{
            DeviceName    = $entry.DeviceName
            FriendlyName  = $entry.FriendlyName
            Manufacturer  = $entry.Manufacturer
            DriverVersion = $entry.DriverVersion
            DriverDate    = $entry.DriverDate
            InfName       = $entry.InfName
        }

        foreach ($hardwareId in $hardwareIds) {
            if (-not $lookup.ContainsKey($hardwareId)) {
                $lookup[$hardwareId] = $detail
            }
        }

        $inventory.Add([pscustomobject]@{
                deviceName        = if ([string]::IsNullOrWhiteSpace($entry.FriendlyName)) { $entry.DeviceName } else { $entry.FriendlyName }
                manufacturer      = $entry.Manufacturer
                provider          = $entry.DriverProviderName
                driverVersion     = $entry.DriverVersion
                driverDate        = $driverDate
                installDate       = $installDate
                classGuid         = if ($entry.ClassGuid) { $entry.ClassGuid.ToString() } else { $null }
                driverDescription = $entry.Description
                hardwareIds       = $hardwareIds
                signed            = if ($entry.PSObject.Properties['IsSigned']) { [bool]$entry.IsSigned } else { $null }
                infName           = $entry.InfName
                deviceId          = $entry.DeviceID
                problemCode       = $problemCode
                status            = $status
            }) | Out-Null
    }

            Write-Host ("Processed entries: {0}" -f $processed)
            Write-Host ("Inventory after loop: {0}" -f $inventory.Count)

    return [pscustomobject]@{
        Lookup    = $lookup
        Inventory = $inventory
    }
}

$inventory = Get-InstalledDriverInventory
Write-Host ("Inventory count: {0}" -f $inventory.Inventory.Count)
$first = $inventory.Inventory | Select-Object -First 1
if ($first) {
    Write-Host 'Sample entry:'
    $first | Format-List * | Out-String | Write-Host
}
