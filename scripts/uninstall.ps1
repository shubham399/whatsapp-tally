# Unregisters the COM DLL and removes the files copied into the Tally folder.
# Run from an ELEVATED (Administrator) PowerShell window.

param(
    [string]$TallyDir = "C:\Program Files\TallyPrime",
    [string]$TdlDir   = "C:\Users\Admin\OneDrive\TDL"
)

$ErrorActionPreference = "Stop"

$dll    = Join-Path $TallyDir "TallyWhatsappSender.dll"
$regasm = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\RegAsm.exe"

if (Test-Path $dll) {
    Write-Host "Unregistering $dll ..." -ForegroundColor Cyan
    & $regasm $dll /unregister
    Remove-Item $dll -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $TallyDir "WebDriver.dll") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $TallyDir "selenium-manager") -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Note: the Selenium dependency DLLs (System.*.dll) were left in place." -ForegroundColor Yellow
}

$tdl = Join-Path $TdlDir "WhatsAppSender.tdl"
if (Test-Path $tdl) {
    Remove-Item $tdl -Force -ErrorAction SilentlyContinue
    Write-Host "Removed $tdl" -ForegroundColor Cyan
}

Write-Host "Done." -ForegroundColor Green
