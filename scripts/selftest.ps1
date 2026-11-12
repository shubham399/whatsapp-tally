# Exercises the COM add-on WITHOUT Tally, so the Chrome / WhatsApp Web part can
# be tested on its own:
#
#   powershell -ExecutionPolicy Bypass -File .\scripts\selftest.ps1 -Number 9876543210
#   powershell -ExecutionPolicy Bypass -File .\scripts\selftest.ps1 -Number 9876543210 -Pdf C:\path\invoice.pdf
#
# It calls SendSilent (the same flow as Send, without the result dialog) and
# then prints the tail of the log. No Administrator rights are needed as long
# as install.ps1 has already registered the DLL.

param(
    [Parameter(Mandatory = $true)]
    [string]$Number,

    [string]$Pdf,

    [string]$Caption = "Test invoice sent from Tally",

    [string]$Chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
)

$ErrorActionPreference = "Stop"
$progId      = "TallyWhatsappSender.WhatsAppSender"
$logFile     = Join-Path $env:LOCALAPPDATA "TallyWhatsAppSender\log.txt"

function Find-TestPdf {
    $roots = @(
        (Join-Path $env:USERPROFILE "OneDrive\Export"),
        "C:\Users\Admin\OneDrive\Export",
        (Join-Path $env:USERPROFILE "Desktop"),
        (Join-Path $env:USERPROFILE "Downloads"),
        (Join-Path $env:USERPROFILE "Documents")
    )

    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $match = Get-ChildItem -Path $root -Filter *.pdf -File -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending |
                 Select-Object -First 1
        if ($match) { return $match.FullName }
    }
    return $null
}

if (-not $Pdf) {
    $Pdf = Find-TestPdf
    if (-not $Pdf) {
        throw "No PDF found to send. Pass one explicitly: -Pdf C:\path\invoice.pdf"
    }
}

if (-not (Test-Path $Pdf)) {
    throw "PDF not found: $Pdf"
}

Write-Host "Contact : $Number"
Write-Host "PDF     : $Pdf"
Write-Host "Caption : $Caption"
Write-Host "Chrome  : $Chrome"
Write-Host ""

try {
    $sender = New-Object -ComObject $progId
} catch {
    throw ("Could not create the COM object '$progId'. Run scripts\install.ps1 first. " +
           "Original error: " + $_.Exception.Message)
}

Write-Host "Sending. Chrome will open; scan the QR code if this is the first run." -ForegroundColor Cyan

try {
    $result = $sender.SendSilent($Number, $Pdf, $Caption, $Chrome)
} catch {
    throw ("The send failed to start. If the message says the method does not exist, " +
           "the installed DLL is older than this script - run scripts\install.ps1. " +
           "Original error: " + $_.Exception.Message)
}

Write-Host ""
if ($result -like "Success*") {
    Write-Host $result -ForegroundColor Green
} else {
    Write-Host $result -ForegroundColor Yellow
}

if (Test-Path $logFile) {
    Write-Host ""
    Write-Host "Last log lines ($logFile):" -ForegroundColor Cyan
    Get-Content $logFile -Tail 30
}
