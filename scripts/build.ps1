# Builds TallyWhatsappSender.dll (the COM DLL) in Release.
# Requires the .NET SDK (dotnet). Run from any PowerShell.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\TallyWhatsappSender\TallyWhatsappSender.csproj"

Write-Host "Building $proj ..." -ForegroundColor Cyan
dotnet build $proj -c Release

$out = Join-Path $root "src\TallyWhatsappSender\bin\Release"
Write-Host ""
Write-Host "Build output: $out" -ForegroundColor Green
Get-ChildItem $out -Filter *.dll | Select-Object Name, Length | Format-Table -AutoSize
