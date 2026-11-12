# Links WhatsApp Web to the Chrome profile the add-on uses.
#
# The add-on drives its own Chrome profile so it never touches your normal
# browsing profile. That profile has to be linked to WhatsApp once:
#
#   powershell -ExecutionPolicy Bypass -File .\scripts\login.ps1
#
# The installer runs this for you after registering the DLL. Run it again only
# when WhatsApp Web logs itself out (or after deleting the profile folder).
#
# It opens web.whatsapp.com in Chrome, waits for you to scan the QR code with
# your phone (WhatsApp > Settings > Linked devices > Link a device) and closes
# Chrome again once the chat list appears. Use -KeepOpen to leave Chrome open.
#
# The profile is %LOCALAPPDATA%\TallyWhatsAppSender\chrome-profile - a normal,
# persistent folder, so the link survives restarts and Windows updates.

param(
    [string]$ChromeExe      = "",
    [string]$ProfileDir     = (Join-Path $env:LOCALAPPDATA "TallyWhatsAppSender\chrome-profile"),
    [int]   $Port           = 9339,
    [int]   $TimeoutSeconds = 300,
    [switch]$KeepOpen
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$Message) {
    Write-Host $Message -ForegroundColor Cyan
}

# --- Chrome -----------------------------------------------------------------

function Resolve-Chrome {
    param([string]$Hint)

    if ($Hint -and (Test-Path $Hint)) { return (Resolve-Path $Hint).Path }

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

# --- tiny CDP client --------------------------------------------------------
# Only two things are needed here: "is the chat list on screen?" and "close the
# browser". Everything else lives in the C# add-on.

$script:CdpId = 0

function Test-Port {
    param([int]$Port)

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $pending = $client.BeginConnect("127.0.0.1", $Port, $null, $null)
        if (-not $pending.AsyncWaitHandle.WaitOne(500)) { return $false }
        $client.EndConnect($pending)
        return $true
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Send-CdpCommand {
    param($Socket, [string]$Method, [string]$Params)

    $script:CdpId++
    $id = $script:CdpId

    $json  = '{"id":' + $id + ',"method":"' + $Method + '","params":' + $Params + '}'
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $segment = New-Object 'System.ArraySegment[byte]' -ArgumentList @(,$bytes)

    $Socket.SendAsync($segment,
                      [System.Net.WebSockets.WebSocketMessageType]::Text,
                      $true,
                      [System.Threading.CancellationToken]::None).Wait(10000) | Out-Null

    return $id
}

function Receive-CdpText {
    param($Socket, [int]$TimeoutMs = 15000)

    $buffer  = New-Object byte[] 131072
    $segment = New-Object 'System.ArraySegment[byte]' -ArgumentList @(,$buffer)
    $text    = New-Object System.Text.StringBuilder
    $cancel  = New-Object System.Threading.CancellationTokenSource $TimeoutMs

    try {
        do {
            $pending = $Socket.ReceiveAsync($segment, $cancel.Token)
            if (-not $pending.Wait($TimeoutMs)) { return $null }

            $result = $pending.Result
            if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                return $null
            }

            [void]$text.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
        } while (-not $result.EndOfMessage)
    } catch {
        return $null
    } finally {
        $cancel.Dispose()
    }

    return $text.ToString()
}

function Invoke-CdpEvaluate {
    param($Socket, [string]$Expression)

    $params = '{"expression":' + (ConvertTo-Json $Expression -Compress) + ',"returnByValue":true}'
    $id     = Send-CdpCommand -Socket $Socket -Method "Runtime.evaluate" -Params $params

    # Ignore protocol events and wait for the reply with our id.
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $message = Receive-CdpText -Socket $Socket
        if (-not $message) { return $null }

        $reply = $message | ConvertFrom-Json
        if ($reply.id -eq $id) {
            if ($reply.result.exceptionDetails) { return $null }
            return $reply.result.result.value
        }
    }

    return $null
}

function Get-PageSocketUrl {
    param([int]$Port)

    try {
        $targets = Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/json" -f $Port) `
                                     -UseBasicParsing -TimeoutSec 5
    } catch {
        return $null
    }

    $pages = @($targets) | Where-Object { $_.type -eq "page" -and $_.webSocketDebuggerUrl }

    # Prefer the tab that already shows WhatsApp Web; if there is none, the
    # first page is reused and navigated below (never a new tab).
    $page = $pages | Where-Object { $_.url -like "*web.whatsapp.com*" } | Select-Object -First 1
    if (-not $page) { $page = $pages | Select-Object -First 1 }

    if ($page) { return $page.webSocketDebuggerUrl }
    return $null
}

function Stop-ProfileChrome {
    param([string]$ProfileDir)

    $killed = 0
    try {
        Get-CimInstance Win32_Process -Filter "Name = 'chrome.exe'" -ErrorAction Stop |
            ForEach-Object {
                if ($_.CommandLine -and
                    $_.CommandLine.IndexOf($ProfileDir, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop; $killed++ } catch { }
                }
            }
    } catch {
        # WMI/CIM can be unavailable; not fatal, the port check below decides.
    }

    if ($killed -gt 0) {
        Write-Host "Closed $killed Chrome process(es) that were using the add-on profile." -ForegroundColor Yellow
        Start-Sleep -Milliseconds 800
    }
}

# --- run --------------------------------------------------------------------

$ProfileDir = [IO.Path]::GetFullPath($ProfileDir)

$chrome = Resolve-Chrome $ChromeExe
if (-not $chrome) {
    Write-Host "Google Chrome was not found." -ForegroundColor Red
    Write-Host "Install Chrome, or pass its location: -ChromeExe `"C:\path\to\chrome.exe`""
    exit 1
}

Write-Step "Chrome          : $chrome"
Write-Host   "Add-on profile  : $ProfileDir"
Write-Host   ""

$reused = $false

if (Test-Port $Port) {
    # The add-on (or an earlier login.ps1 run) already has a browser open on
    # this profile. Just check whether it is linked instead of starting another.
    Write-Host "The add-on's Chrome is already running on port $Port - reusing it."
    $reused = $true
} else {
    Stop-ProfileChrome $ProfileDir
    New-Item -ItemType Directory -Force -Path $ProfileDir | Out-Null

    Write-Step "Opening WhatsApp Web in Chrome ..."

    # Same flags as the add-on (see WhatsAppSender.EnsureBrowser), so this is
    # exactly the browser session a send will later reuse.
    $arguments = @(
        "--remote-debugging-port=$Port"
        "--user-data-dir=`"$ProfileDir`""
        "--no-first-run"
        "--no-default-browser-check"
        "--disable-popup-blocking"
        "--disable-notifications"
        "--start-maximized"
        "https://web.whatsapp.com"
    )

    Start-Process -FilePath $chrome -ArgumentList $arguments | Out-Null

    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Port $Port)) {
        if ((Get-Date) -gt $deadline) {
            Write-Host "Chrome started but its automation port $Port never opened." -ForegroundColor Red
            Write-Host "Close the Chrome window using this profile and try again."
            exit 1
        }
        Start-Sleep -Milliseconds 300
    }
}

$socketUrl = $null
$deadline  = (Get-Date).AddSeconds(30)
while (-not $socketUrl) {
    $socketUrl = Get-PageSocketUrl $Port
    if (-not $socketUrl) {
        if ((Get-Date) -gt $deadline) {
            Write-Host "Chrome did not expose a page to talk to." -ForegroundColor Red
            exit 1
        }
        Start-Sleep -Milliseconds 500
    }
}

$socket = New-Object System.Net.WebSockets.ClientWebSocket
try {
    $socket.ConnectAsync([Uri]$socketUrl, [System.Threading.CancellationToken]::None).Wait(15000) | Out-Null
} catch {
    Write-Host ("Could not connect to Chrome: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}

try {
    Write-Host ""
    Write-Host "On your phone:" -ForegroundColor White
    Write-Host "  WhatsApp > Settings > Linked devices > Link a device"
    Write-Host "  then scan the QR code shown in the Chrome window."
    Write-Host ""
    Write-Step ("Waiting for the login ... (up to {0} minutes)" -f [Math]::Round($TimeoutSeconds / 60))

    # Reusing an existing Chrome: its single tab may still be about:blank, so
    # point that tab at WhatsApp Web instead of waiting on the wrong page.
    $href = Invoke-CdpEvaluate -Socket $socket -Expression "location.href"
    if (-not ($href -like "*web.whatsapp.com*")) {
        Send-CdpCommand -Socket $socket -Method "Page.navigate" `
                        -Params '{"url":"https://web.whatsapp.com"}' | Out-Null
    }

    $deadline  = (Get-Date).AddSeconds($TimeoutSeconds)
    $qrSeen    = $false
    $loggedIn  = $false

    while ((Get-Date) -lt $deadline) {
        $state = Invoke-CdpEvaluate -Socket $socket -Expression @"
(function(){
  if (document.querySelector('#pane-side') ||
      document.querySelector("div[data-testid='chat-list']")) return 'in';
  if (document.querySelector('canvas[aria-label]') ||
      document.querySelector('div[data-ref]')) return 'qr';
  return 'wait';
})()
"@

        if ($state -eq "in")   { $loggedIn = $true; break }
        if ($state -eq "qr" -and -not $qrSeen) {
            $qrSeen = $true
            Write-Host "QR code is on screen - scan it with your phone." -ForegroundColor Yellow
        }

        Start-Sleep -Seconds 1
    }
} finally {
    try { $socket.Dispose() } catch { }
}

if (-not $loggedIn) {
    Write-Host ""
    Write-Host ("WhatsApp Web was not linked within {0} minutes." -f [Math]::Round($TimeoutSeconds / 60)) -ForegroundColor Red
    Write-Host "Chrome is left open - scan the code, then run this script again to confirm."
    exit 1
}

Write-Host ""
Write-Host "WhatsApp Web is linked to the add-on's Chrome profile." -ForegroundColor Green
Write-Host "The login is stored in $ProfileDir and survives restarts."

if ($KeepOpen) {
    Write-Host "Chrome is left open (-KeepOpen)."
    exit 0
}

# Give WhatsApp a moment to flush the session to disk, then close the browser
# gracefully. The next send starts Chrome again on the same profile and finds
# the session already linked.
Start-Sleep -Seconds 2

try {
    $version = Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/json/version" -f $Port) `
                                 -UseBasicParsing -TimeoutSec 5
    $browser = New-Object System.Net.WebSockets.ClientWebSocket
    try {
        $browser.ConnectAsync([Uri]$version.webSocketDebuggerUrl,
                              [System.Threading.CancellationToken]::None).Wait(10000) | Out-Null
        Send-CdpCommand -Socket $browser -Method "Browser.close" -Params "{}" | Out-Null
        Start-Sleep -Seconds 1
    } finally {
        try { $browser.Dispose() } catch { }
    }
} catch {
    # Closing is a convenience; the login is already stored.
}

# Do not report success until the port is really gone, otherwise a send that
# starts right now could attach to a browser that is shutting down.
$deadline = (Get-Date).AddSeconds(10)
while ((Test-Port $Port) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }

Write-Host "Chrome closed. You can now save a Sales voucher."
exit 0
