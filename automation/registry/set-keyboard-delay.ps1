param(
    [int]$KeyboardDelay = 0,
    [switch]$Revert
)

$path = "HKCU:\Control Panel\Keyboard"

if ($Revert) {
    # Windows default is 1 (range 0-3)
    Set-ItemProperty -Path $path -Name "KeyboardDelay" -Value "1" -Type String
    return
}

if ($KeyboardDelay -lt 0 -or $KeyboardDelay -gt 3) { throw "KeyboardDelay must be between 0 and 3." }
Set-ItemProperty -Path $path -Name "KeyboardDelay" -Value "$KeyboardDelay" -Type String
