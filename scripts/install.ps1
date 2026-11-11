# Installs the add-on:
#   1. copies the built DLLs into the Tally working folder,
#   2. copies the TDL into the TDL folder,
#   3. creates the PDF output folder,
#   4. registers the COM DLL using the 32-bit regasm (TallyPrime is 32-bit).
#
# Run this from an ELEVATED (Administrator) PowerShell window.

param(
    [string]$TallyDir  = "C:\Program Files\TallyPrime",
    [string]$TdlDir    = "C:\Users\Admin\OneDrive\TDL",
    [string]$OutputDir = "C:\Users\Admin\OneDrive\Export"
)

$ErrorActionPreference = "Stop"

$root  = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root "src\TallyWhatsappSender\bin\Release"
$dll   = Join-Path $build "TallyWhatsappSender.dll"

if (-not (Test-Path $dll))   { throw "Build first - '$dll' not found. Run scripts\build.ps1." }
if (-not (Test-Path $TallyDir)) { throw "Tally folder not found: '$TallyDir'. Pass the correct -TallyDir." }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $TdlDir    | Out-Null

Write-Host "Copying build output to $TallyDir ..." -ForegroundColor Cyan
# Everything (TallyWhatsappSender.dll, WebDriver.dll, the Selenium
# dependencies and the selenium-manager folder) must sit next to TallyPrime.exe.
Copy-Item (Join-Path $build "*") $TallyDir -Recurse -Force

# Copy the TDL into the TDL folder; this is the file you load in Tally.
Write-Host "Copying TDL to $TdlDir ..." -ForegroundColor Cyan
Copy-Item (Join-Path $root "TDL\WhatsAppSender.tdl") $TdlDir -Force

$regasm = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\RegAsm.exe"
if (-not (Test-Path $regasm)) { throw "regasm not found at $regasm" }

Write-Host "Registering COM DLL with $regasm ..." -ForegroundColor Cyan
& $regasm $dll /codebase

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Next: in TallyPrime press F1 > TDLs & Add-Ons > F4, point to" -ForegroundColor Green
Write-Host "      $(Join-Path $TdlDir 'WhatsAppSender.tdl')" -ForegroundColor Green
Write-Host "and set 'Load selected TDL files on startup' to Yes." -ForegroundColor Green
