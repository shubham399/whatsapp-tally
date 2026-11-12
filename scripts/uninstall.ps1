# Unregisters the COM DLL and removes the files copied into the Tally folder.
# Run it from any PowerShell window; it self-elevates (UAC prompt):
#   powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1

param(
    [string]$TallyDir = "C:\Program Files\TallyPrime",
    [string]$TdlDir   = "C:\Users\Admin\OneDrive\TDL"
)

# --- self-elevate -----------------------------------------------------------
# Unregistering writes to HKLM, which needs Administrator. Relaunch elevated
# (UAC prompt) when we are not already an Administrator, forwarding arguments.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Administrator rights are required - requesting elevation ..." -ForegroundColor Yellow
    $scriptPath = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
    $argString  = "-NoProfile -NoExit -ExecutionPolicy Bypass -File `"$scriptPath`""
    foreach ($k in $PSBoundParameters.Keys) {
        $argString += " -$k `"$($PSBoundParameters[$k])`""
    }
    try {
        Start-Process powershell -Verb RunAs -ArgumentList $argString -ErrorAction Stop
    } catch {
        Write-Warning "Elevation was cancelled or failed: $($_.Exception.Message)"
        exit 1
    }
    exit
}

$ErrorActionPreference = "Stop"

$dll = Join-Path $TallyDir "TallyWhatsappSender.dll"

# install.ps1 registers with both regasm flavours, so unregister with both.
$regasms = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\RegAsm.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe")
)

if (Test-Path $dll) {
    foreach ($regasm in $regasms) {
        if (Test-Path $regasm) {
            Write-Host "Unregistering $dll with $regasm ..." -ForegroundColor Cyan
            $previous = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try { & $regasm $dll /unregister 2>&1 | ForEach-Object { Write-Host "  $_" } }
            finally { $ErrorActionPreference = $previous }
        }
    }
    Remove-Item $dll -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $TallyDir "TallyWhatsappSender.pdb") -Force -ErrorAction SilentlyContinue
}

# Remove the leftovers of the Selenium based versions (up to 1.0.13). They are
# not used by 1.1.0 and only risk being picked up by Tally's own loader.
$legacyFiles = @(
    "Selenium.WebDriver.dll",
    "WebDriver.dll",
    "chromedriver.exe",
    "System.Text.Json.dll",
    "System.Text.Encodings.Web.dll",
    "System.Threading.Tasks.Extensions.dll",
    "System.ValueTuple.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",
    "System.Buffers.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll"
)

$removed = @()
foreach ($name in $legacyFiles) {
    $path = Join-Path $TallyDir $name
    if (Test-Path $path) {
        Remove-Item $path -Force -ErrorAction SilentlyContinue
        $removed += $name
    }
}

$driverCache = Join-Path $TallyDir "selenium-manager"
if (Test-Path $driverCache) {
    Remove-Item $driverCache -Recurse -Force -ErrorAction SilentlyContinue
    $removed += "selenium-manager\"
}

if ($removed.Count -gt 0) {
    Write-Host ("Removed old Selenium files: " + ($removed -join ", ")) -ForegroundColor Cyan
}

$tdl = Join-Path $TdlDir "WhatsAppSender.tdl"
if (Test-Path $tdl) {
    Remove-Item $tdl -Force -ErrorAction SilentlyContinue
    Write-Host "Removed $tdl" -ForegroundColor Cyan
}

# The Chrome profile is deliberately left alone: it holds the WhatsApp Web
# login, which belongs to the user and is expensive to recreate (a new QR scan).
$profileDir = Join-Path $env:LOCALAPPDATA "TallyWhatsAppSender\chrome-profile"
if (Test-Path $profileDir) {
    Write-Host ""
    Write-Host "The WhatsApp login was kept in:" -ForegroundColor Yellow
    Write-Host "  $profileDir"
    Write-Host "Delete that folder (and %LOCALAPPDATA%\TallyWhatsAppSender\log.txt) to remove it completely."
}

Write-Host "Done." -ForegroundColor Green
