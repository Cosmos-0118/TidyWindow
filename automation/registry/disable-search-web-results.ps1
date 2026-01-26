param(
    [switch]$Enable,
    [switch]$Disable
)

$explorerPath = "HKCU:\Software\Policies\Microsoft\Windows\Explorer"
$searchPath = "HKCU:\Software\Policies\Microsoft\Windows\Windows Search"

if ($Enable) {
    New-Item -Path $explorerPath -Force | Out-Null
    New-Item -Path $searchPath -Force | Out-Null
    Set-ItemProperty -Path $explorerPath -Name "DisableSearchBoxSuggestions" -Type DWord -Value 1
    Set-ItemProperty -Path $searchPath -Name "EnableDynamicContentInWSB" -Type DWord -Value 0
    return
}

if ($Disable) {
    Remove-ItemProperty -Path $explorerPath -Name "DisableSearchBoxSuggestions" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $searchPath -Name "EnableDynamicContentInWSB" -ErrorAction SilentlyContinue
    return
}

throw "Specify -Enable or -Disable."