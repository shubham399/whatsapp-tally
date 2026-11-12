# Installs the add-on:
#   1. removes files left behind by older (Selenium based) versions,
#   2. copies TallyWhatsappSender.dll next to Tally.exe,
#   3. copies the TDL into the TDL folder,
#   4. creates the PDF output folder,
#   5. registers the COM DLL with BOTH the 32-bit and 64-bit regasm,
#   6. opens the WhatsApp QR login step in a normal (non-admin) window.
#
# Run it from any PowerShell window; it self-elevates (UAC prompt):
#   powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
#
# Add -SkipLogin to leave the QR login for later:
#   powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -SkipLogin

param(
    [string]$TallyDir  = "C:\Program Files\TallyPrime",
    [string]$TdlDir    = "C:\Users\Admin\OneDrive\TDL",
    [string]$OutputDir = "C:\Users\Admin\OneDrive\Export",
    [switch]$SkipLogin
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
        $value = $PSBoundParameters[$k]
        if ($value -is [switch]) {
            if ($value.IsPresent) { $argString += " -$k" }
        } else {
            $argString += " -$k `"$value`""
        }
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

# Copying over a running Tally fails (loaded DLLs are locked) and can leave a
# half-updated installation. Ask for a restart instead.
if (Get-Process -Name "Tally" -ErrorAction SilentlyContinue) {
    throw "TallyPrime is running. Close it, then run the installer again so the DLLs next to Tally.exe can be replaced."
}

function Resolve-Chrome {
    $candidates = New-Object System.Collections.Generic.List[string]

    $registered = Get-ItemProperty `
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe' `
        -ErrorAction SilentlyContinue
    if ($registered) { $candidates.Add($registered.'(default)') }

    foreach ($variable in @("ProgramFiles", "ProgramFiles(x86)", "LocalAppData")) {
        $root = [Environment]::GetEnvironmentVariable($variable)
        if ($root) { $candidates.Add((Join-Path $root "Google\Chrome\Application\chrome.exe")) }
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return (Resolve-Path $candidate).Path }
    }

    return $null
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $TdlDir    | Out-Null

# === remove what older versions installed ===================================
# Up to 1.0.13 the add-on used Selenium and copied a driver plus a pile of
# System.*.dll files next to Tally.exe. 1.1.0 talks to Chrome over DevTools and
# has no dependencies at all, so those files are dead weight - and worse, Tally
# can end up loading a stale copy of System.Text.Json instead of its own.
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
    Write-Host ("Removed old Selenium files: " + ($removed -join ", ")) -ForegroundColor Yellow
}

# === copy the add-on ========================================================
Write-Host "Copying the add-on to $TallyDir ..." -ForegroundColor Cyan
Copy-Item $dll $TallyDir -Force

# The .pdb is optional; it only makes an exception's line numbers readable.
$pdb = Join-Path $build "TallyWhatsappSender.pdb"
if (Test-Path $pdb) { Copy-Item $pdb $TallyDir -Force }

# Copy the TDL into the TDL folder; this is the file you load in Tally.
Write-Host "Copying TDL to $TdlDir ..." -ForegroundColor Cyan
Copy-Item (Join-Path $root "TDL\WhatsAppSender.tdl") $TdlDir -Force

# === register COM ===========================================================
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

    # The DLL is intentionally unsigned (see the .csproj), so regasm prints the
    # advisory warning RA0000. Under $ErrorActionPreference='Stop' any stderr
    # output becomes a terminating error, so relax it for this call and judge by
    # the exit code instead.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $regasm $installedDll /codebase 2>&1 | ForEach-Object { Write-Host "  $_" }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) { throw "regasm failed with exit code $code ($regasm)." }
}


# === verify the install ======================================================
# Registry keys turned out to be a poor check here: HKLM\SOFTWARE\Classes is a
# shared key, so what a probe sees depends on the host process, and earlier
# versions reported false "MISSING" results for keys regasm had just written.
# What actually matters is what Tally does when it calls the add-on: create the
# COM object. So do exactly that - once from a 64-bit PowerShell and once from a
# 32-bit one - and report the exception if it fails.

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)

    # A native command writing to stderr becomes a terminating error while
    # $ErrorActionPreference is 'Stop', so relax it for the call and let the
    # exit code decide what happened.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>&1
        $code   = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }

    return [pscustomobject]@{
        ExitCode = $code
        Lines    = @($output | Where-Object { $_ -is [string] })
    }
}

# --- which architecture is the Tally executable? -----------------------------
$tallyBits = $null
$tallyExe  = Get-ChildItem $TallyDir -Filter "Tally*.exe" -ErrorAction SilentlyContinue |
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
        switch ($machine) {
            0x14c  { $tallyBits = "32"; $arch = "32-bit (x86)" }
            0x8664 { $tallyBits = "64"; $arch = "64-bit (x64)" }
            default { $arch = ("unknown (0x{0:x4})" -f $machine) }
        }
        Write-Host "  Tally executable  : $($tallyExe.FullName) -> $arch" -ForegroundColor Cyan
    } catch {
        Write-Host "  Tally executable  : $($tallyExe.FullName) -> architecture not readable" -ForegroundColor Yellow
    }
} else {
    Write-Host "  Tally executable  : no Tally*.exe in $TallyDir" -ForegroundColor Yellow
}

# --- COM activation test, once per architecture ------------------------------
$progId = "TallyWhatsappSender.WhatsAppSender"
$probe  = "try { New-Object -ComObject '$progId' | Out-Null; 'OK' } catch { 'FAILED: ' + `$_.Exception.Message }"

$ps32 = Join-Path $env:WINDIR "SysWOW64\WindowsPowerShell\v1.0\powershell.exe"
$ps64 = if ([Environment]::Is64BitProcess) {
    Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
} else {
    # From a 32-bit host, System32 is redirected to SysWOW64; Sysnative is the
    # alias that reaches the real System32.
    Join-Path $env:WINDIR "Sysnative\WindowsPowerShell\v1.0\powershell.exe"
}

$activation = [ordered]@{}
foreach ($entry in @(@("64", $ps64), @("32", $ps32))) {
    $bits  = $entry[0]
    $psExe = $entry[1]

    if (-not (Test-Path $psExe)) {
        $activation[$bits] = "host not available"
        continue
    }

    $run  = Invoke-Native $psExe @("-NoProfile", "-NonInteractive", "-Command", $probe)
    $text = (($run.Lines -join " ") -replace '\s+', ' ').Trim()

    if     ($text -match '^OK')                { $activation[$bits] = "OK" }
    elseif ($text -match 'FAILED:\s*(.*)')     { $activation[$bits] = "FAILED - " + $Matches[1].Trim() }
    else                                       { $activation[$bits] = "FAILED (no output, exit $($run.ExitCode))" }
}

foreach ($bits in $activation.Keys) {
    $isOk  = ($activation[$bits] -eq "OK")
    $color = if ($isOk) { "Green" } else { "Yellow" }
    Write-Host ("  COM activation ({0}-bit) : {1}" -f $bits, $activation[$bits]) -ForegroundColor $color
}

# Success is judged on the architecture Tally actually runs as - the other view
# is only a safety net.
$status = if ($tallyBits) { $activation[$tallyBits] } else { $null }
if ($status -eq "OK") {
    Write-Host "COM registration OK for the architecture Tally uses ($tallyBits-bit)." -ForegroundColor Green
} elseif (-not $tallyBits -and (($activation.Values) -contains "OK")) {
    Write-Host "COM registration OK (Tally's architecture could not be determined)." -ForegroundColor Green
} else {
    Write-Warning "COM activation failed for the architecture Tally uses - Tally will not be able to call the add-on."
}

# === Chrome =================================================================
$chrome = Resolve-Chrome
if ($chrome) {
    Write-Host "  Google Chrome     : $chrome" -ForegroundColor Cyan
} else {
    Write-Warning ("Google Chrome was not found. The add-on needs Chrome for WhatsApp Web. " +
                   "Install it, then run scripts\login.ps1.")
}

# === WhatsApp login =========================================================
# Chrome refuses to run elevated, and this script is elevated, so the login
# helper is started through explorer.exe, which launches it as the logged-on
# user. It opens the add-on's own Chrome profile and waits for the QR scan.
if ($SkipLogin) {
    Write-Host ""
    Write-Host "Skipping the WhatsApp login step (-SkipLogin). Run scripts\login.ps1 when ready."
} elseif (-not $chrome) {
    Write-Host ""
    Write-Host "After installing Chrome, run scripts\login.ps1 to link WhatsApp."
} else {
    $login = Join-Path $PSScriptRoot "login.ps1"
    if (-not (Test-Path $login)) {
        Write-Warning "login.ps1 was not found next to install.ps1. Run it from the repository folder."
    } else {
        Write-Host ""
        Write-Host "Starting the WhatsApp login step in a normal (non-admin) window ..." -ForegroundColor Cyan
        Write-Host "Scan the QR code with your phone: WhatsApp > Settings > Linked devices > Link a device."

        $cmdPath = Join-Path $env:TEMP "tally-whatsapp-login.cmd"
        $lines = @(
            "@echo off"
            "title WhatsApp login for Tally"
            "powershell -NoProfile -ExecutionPolicy Bypass -File `"$login`""
            "echo."
            "pause"
        )
        Set-Content -Path $cmdPath -Value ($lines -join "`r`n") -Encoding ASCII

        try {
            Start-Process -FilePath "explorer.exe" -ArgumentList "`"$cmdPath`"" -ErrorAction Stop
        } catch {
            Write-Warning ("Could not open the login window automatically: " + $_.Exception.Message)
            Write-Host "Run it yourself in a normal PowerShell window:"
            Write-Host "  powershell -ExecutionPolicy Bypass -File `"$login`""
        }
    }
}

# === next steps =============================================================
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Load the TDL in Tally: F1 > TDLs & Add-Ons > F4 (Manage Local TDLs),"
Write-Host "     enable 'Load selected TDL files on startup' and add"
Write-Host "     $TdlDir\WhatsAppSender.tdl"
Write-Host "  2. Restart TallyPrime so it picks up the COM registration."
Write-Host "  3. Save a Sales voucher; the QR login is already handled by step 6."
