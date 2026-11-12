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
    [string]$OutputDir = "C:\Users\Admin\OneDrive\Export",
    [string]$ChromeExe = "C:\Program Files\Google\Chrome\Application\chrome.exe"
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

# Copying over a running Tally fails (loaded DLLs are locked) and can leave a
# half-updated installation. Ask for a restart instead.
if (Get-Process -Name "Tally" -ErrorAction SilentlyContinue) {
    throw "TallyPrime is running. Close it, then run the installer again so the DLLs next to Tally.exe can be replaced."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $TdlDir    | Out-Null

Write-Host "Copying build output to $TallyDir ..." -ForegroundColor Cyan
# Everything (TallyWhatsappSender.dll, WebDriver.dll, the Selenium
# dependencies and the selenium-manager folder) must sit next to TallyPrime.exe.
Copy-Item (Join-Path $build "*") $TallyDir -Recurse -Force

# Selenium's client library does not ship a driver that matches modern Chrome,
# and relying on its downloader from inside Tally is fragile. Fetch the matching
# chromedriver from the Chrome for Testing index once, here, where we are
# elevated and can write next to Tally.exe.
function Install-ChromeDriver {
    param([string]$ChromePath, [string]$Destination)

    try {
        if (-not (Test-Path $ChromePath)) {
            $candidates = @(
                (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe' -ErrorAction SilentlyContinue).'(default)',
                (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'),
                (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe')
            ) | Where-Object { $_ -and (Test-Path $_) }
            $ChromePath = $candidates | Select-Object -First 1
        }
        if (-not $ChromePath -or -not (Test-Path $ChromePath)) {
            Write-Warning "Google Chrome was not found; skipping the driver download."
            return
        }

        $fullVersion = (Get-Item $ChromePath).VersionInfo.ProductVersion -replace '\s.*$', ''
        $major = ($fullVersion -split '\.')[0]
        Write-Host "Google Chrome $fullVersion detected - finding a matching chromedriver ..." -ForegroundColor Cyan

        # Chrome and its driver must have the same major version. Chrome for
        # Testing publishes one build per Chrome release for win64/win32.
        $platform = "win64"
        try {
            $fs = [IO.File]::OpenRead($ChromePath)
            $br = New-Object IO.BinaryReader($fs)
            $fs.Seek(0x3C, 'Begin') | Out-Null
            $peOffset = $br.ReadInt32()
            $fs.Seek($peOffset + 4, 'Begin') | Out-Null
            $machine = $br.ReadUInt16()
            $br.Dispose(); $fs.Dispose()
            if ($machine -eq 0x14c) { $platform = "win32" }
        } catch {
            if (-not [Environment]::Is64BitOperatingSystem) { $platform = "win32" }
        }

        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $indexUrl = "https://googlechromelabs.github.io/chrome-for-testing/known-good-versions-with-downloads.json"
        $index = Invoke-RestMethod -Uri $indexUrl -UseBasicParsing -TimeoutSec 60

        $entry = $index.versions | Where-Object { $_.version -eq $fullVersion } | Select-Object -First 1
        if (-not $entry) {
            # Chrome's exact build may not be in the list; the same major works.
            $entry = $index.versions |
                     Where-Object { ($_.version -split '\.')[0] -eq $major } |
                     Select-Object -Last 1
        }
        if (-not $entry) {
            Write-Warning "No chromedriver release was found for Chrome $fullVersion."
            return
        }

        $download = $entry.downloads.chromedriver |
                    Where-Object { $_.platform -eq $platform } |
                    Select-Object -First 1
        if (-not $download) {
            Write-Warning "No $platform chromedriver download exists for Chrome $($entry.version)."
            return
        }

        $zip     = Join-Path $env:TEMP "chromedriver-$($entry.version)-$platform.zip"
        $extract = Join-Path $env:TEMP "chromedriver-$($entry.version)-$platform"
        Write-Host "Downloading $($download.url) ..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $download.url -OutFile $zip -UseBasicParsing -TimeoutSec 120

        if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
        Expand-Archive -Path $zip -DestinationPath $extract -Force
        $exe = Get-ChildItem -Path $extract -Recurse -Filter chromedriver.exe | Select-Object -First 1
        if (-not $exe) { throw "chromedriver.exe was not found inside the downloaded zip." }

        Copy-Item $exe.FullName (Join-Path $Destination "chromedriver.exe") -Force
        Write-Host "Installed chromedriver $($entry.version) ($platform) next to Tally.exe." -ForegroundColor Green
    } catch {
        Write-Warning ("Could not download chromedriver automatically: " + $_.Exception.Message +
                       " The add-on will try Selenium Manager and any cached driver instead.")
    }
}

Install-ChromeDriver -ChromePath $ChromeExe -Destination $TallyDir

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
