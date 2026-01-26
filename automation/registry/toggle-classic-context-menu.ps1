param(
    [switch]$Enable,
    [switch]$Disable
)

$clsidPath = "HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"
$inprocPath = "$clsidPath\InprocServer32"

if ($Enable) {
    New-Item -Path $inprocPath -Force | Out-Null
    Set-ItemProperty -Path $inprocPath -Name "" -Type String -Value ""
    return
}

if ($Disable) {
    Remove-Item -Path $clsidPath -Recurse -ErrorAction SilentlyContinue
    return
}

throw "Specify -Enable or -Disable."