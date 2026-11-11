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
            & $regasm $dll /unregister
        }
    }
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
