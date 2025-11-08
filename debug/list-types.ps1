$asmPath = Join-Path $PSScriptRoot '..\src\TidyWindow.App\bin\Debug\net8.0-windows\TidyWindow.App.dll'
if (-not (Test-Path $asmPath)) { Write-Host "Assembly not found: $asmPath"; exit 1 }
$asm = [System.Reflection.Assembly]::LoadFrom($asmPath)
$types = $asm.GetTypes() | Where-Object { $_.FullName -like 'TidyWindow.App.ViewModels.*' }
$types | ForEach-Object { $_.FullName }
