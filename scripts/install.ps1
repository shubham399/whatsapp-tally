# Installs the add-on:
#   1. copies the built DLLs into the Tally working folder,
#   2. copies the TDL into the TDL folder,
#   3. creates the PDF output folder,
#   4. registers the COM DLL using the 32-bit regasm (TallyPrime is 32-bit).
#
# Run this from an ELEVATED (Administrator) PowerShell window:
#   powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1

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

# Register the copy that now lives in the Tally folder (NOT the build output),
# so the /codebase path points next to TallyPrime.exe and survives the build
# folder being moved or deleted.
$installedDll = Join-Path $TallyDir "TallyWhatsappSender.dll"

# TallyPrime is 32-bit, so it must be the 32-bit regasm (Framework, not Framework64).
$regasm = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\RegAsm.exe"
if (-not (Test-Path $regasm)) { throw "regasm not found at $regasm" }

Write-Host "Registering COM DLL with $regasm ..." -ForegroundColor Cyan
Write-Host "  $installedDll"
& $regasm $installedDll /codebase
if ($LASTEXITCODE -ne 0) { throw "regasm failed with exit code $LASTEXITCODE." }

# Confirm the registration actually landed.
$progId = "TallyWhatsappSender.WhatsAppSender"
$progIdKey = "HKLM:\SOFTWARE\Classes\$progId"
if (Test-Path $progIdKey) {
    Write-Host "COM registration OK ($progId)." -ForegroundColor Green
} else {
    Write-Warning "Could not find $progIdKey. The DLL may not be registered correctly."
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Next: in TallyPrime press F1 > TDLs & Add-Ons > F4, point to" -ForegroundColor Green
Write-Host "      $(Join-Path $TdlDir 'WhatsAppSender.tdl')" -ForegroundColor Green
Write-Host "and set 'Load selected TDL files on startup' to Yes." -ForegroundColor Green
