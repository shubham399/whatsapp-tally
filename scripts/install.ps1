# Installs the add-on:
#   1. copies the built DLLs into the Tally working folder,
#   2. copies the TDL into the TDL folder,
#   3. creates the PDF output folder,
#   4. registers the COM DLL with BOTH the 32-bit and 64-bit regasm.
#
# Run it from any PowerShell window; it self-elevates (UAC prompt):
#   powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1

param(
    [string]$TallyDir  = "C:\Program Files\TallyPrime",
    [string]$TdlDir    = "C:\Users\Admin\OneDrive\TDL",
    [string]$OutputDir = "C:\Users\Admin\OneDrive\Export"
)

# --- self-elevate -----------------------------------------------------------
# Registering the COM DLL writes to HKLM, which needs Administrator. If we are
# not elevated, relaunch this script in an elevated PowerShell (UAC prompt),
# forwarding the arguments we were given. -NoExit keeps the window open so you
# can read the result.
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

# Register with BOTH regasm flavours. The DLL is built as AnyCPU, so it works
# from either process type: the 32-bit regasm (Framework) writes the 32-bit
# registry view, the 64-bit one (Framework64) writes the 64-bit view. Doing both
# means you never have to know whether TallyPrime is 32- or 64-bit.
$regasms = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\RegAsm.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe")
)

foreach ($regasm in $regasms) {
    if (-not (Test-Path $regasm)) {
        Write-Warning "regasm not found, skipping: $regasm"
        continue
    }
    Write-Host "Registering COM DLL with $regasm ..." -ForegroundColor Cyan
    Write-Host "  $installedDll"
    & $regasm $installedDll /codebase
    if ($LASTEXITCODE -ne 0) { throw "regasm failed with exit code $LASTEXITCODE ($regasm)." }
}

# Confirm the registration actually landed.
#
# reg.exe is used on purpose: it reads the 32-bit and 64-bit registry views
# explicitly (/reg:32, /reg:64). The PowerShell registry *provider* applies its
# own view redirection, which made the previous version of this check report
# "MISSING" for keys that regasm had just created.
$progId  = "TallyWhatsappSender.WhatsAppSender"
$classId = "9f8e7d6c-5b4a-4938-8271-6a5b4c3d2e1f"   # [Guid] on the COM class

function Test-RegKey {
    param([string]$Key, [string]$View)
    & reg query $Key /reg:$View *> $null
    return ($LASTEXITCODE -eq 0)
}

function Get-RegValue {
    param([string]$Key, [string]$Name, [string]$View)
    $out = & reg query $Key /v $Name /reg:$View 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    foreach ($line in $out) {
        if ($line -match "^\s+$([regex]::Escape($Name))\s+REG_\w+\s+(.+)$") {
            return $Matches[1].Trim()
        }
    }
    return $null
}

$inprocKey = "HKLM\SOFTWARE\Classes\CLSID\$classId\InprocServer32"

$allOk = $true
foreach ($view in @("32", "64")) {
    $progOk   = Test-RegKey "HKLM\SOFTWARE\Classes\$progId" $view
    $clsidOk  = Test-RegKey $inprocKey $view
    $codeBase = Get-RegValue $inprocKey "CodeBase" $view
    if (-not ($progOk -and $clsidOk)) { $allOk = $false }

    $label = "Registry view ($view-bit)"
    $state = if ($progOk -and $clsidOk) { "OK" } else { "MISSING" }
    $color = if ($progOk -and $clsidOk) { "Green" } else { "Yellow" }
    Write-Host ("  {0,-24} {1,-4} ProgId={2}  CLSID={3}" -f $label, $state, $progOk, $clsidOk) -ForegroundColor $color
    if ($codeBase) { Write-Host "      CodeBase: $codeBase" -ForegroundColor DarkGray }
}

# Report which architecture the Tally executable in $TallyDir is. The DLL is
# AnyCPU, so either answer works - this is here so a support question about it
# never needs a separate diagnostic run.
$tallyExe = Get-ChildItem $TallyDir -Filter "Tally*.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1
if ($tallyExe) {
    try {
        $fs = [IO.File]::OpenRead($tallyExe.FullName)
        $br = New-Object IO.BinaryReader($fs)
        $fs.Seek(0x3C, 'Begin') | Out-Null
        $peOffset = $br.ReadInt32()
        $fs.Seek($peOffset + 4, 'Begin') | Out-Null
        $machine = $br.ReadUInt16()
        $br.Dispose(); $fs.Dispose()
        $arch = switch ($machine) { 0x14c { "32-bit (x86)" } 0x8664 { "64-bit (x64)" } default { "unknown" } }
        Write-Host "  Tally executable: $($tallyExe.FullName) -> $arch" -ForegroundColor Cyan
    } catch {
        Write-Host "  Tally executable: $($tallyExe.FullName) -> architecture not readable" -ForegroundColor Yellow
    }
} else {
    Write-Host "  No Tally*.exe found in $TallyDir" -ForegroundColor Yellow
}

if ($allOk) {
    Write-Host "COM registration OK in both registry views." -ForegroundColor Green
} else {
    Write-Warning "A registry view is missing the registration - clients of that architecture cannot load the DLL."
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Next: in TallyPrime press F1 > TDLs & Add-Ons > F4, point to" -ForegroundColor Green
Write-Host "      $(Join-Path $TdlDir 'WhatsAppSender.tdl')" -ForegroundColor Green
Write-Host "and set 'Load selected TDL files on startup' to Yes." -ForegroundColor Green
Write-Host "Then RESTART TallyPrime so it picks up the COM registration." -ForegroundColor Green
