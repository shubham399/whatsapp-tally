# Tally WhatsApp Sender (TallyPrime 2.1 · Windows 11)

Send a **Sales invoice as a PDF** to the party's **mobile number** on
**WhatsApp Web (`web.whatsapp.com`) in Google Chrome**, directly from Tally.

When you save a Sales voucher, Tally reads the party's mobile number from the
ledger. If the party has one, Tally asks whether to send the invoice on WhatsApp;
if you say yes, it exports the voucher to PDF and hands the job to a small helper
that drives Chrome on WhatsApp Web and sends the file. Parties **without** a
mobile number are skipped silently — no prompt, no export.

Originally based on
[`tejavarma-aln/TallyWhatsappSender`](https://github.com/tejavarma-aln/TallyWhatsappSender),
rebuilt for TallyPrime 2.1 / .NET Framework 4.8 **without Selenium and without
chromedriver**.

---

## How it works

```
 ┌─────────────┐   On: Form Accept   ┌───────────────────────────────────────┐
 │ TallyPrime  │ ──────────────────► │ TDL: WA_SendInvoiceOnWhatsApp         │
 │ Sales Color │                     │  1. $LedgerMobile -> phone number     │
 └─────────────┘                     │     (no number? stop, no prompt)      │
                                     │  2. Query Box  (send on WhatsApp?)    │
                                     │  3. EXPORT REPORT  -> PDF             │
                                     │  4. EXEC COM INTERFACE WhatsAppEngine │
                                     └───────────────┬───────────────────────┘
                                                     │ COM (IDispatch)
                                                     ▼
                                     ┌───────────────────────────────────────┐
                                     │ TallyWhatsappSender.dll  (C#)         │
                                     │  starts Chrome with its own profile   │
                                     │  talks DevTools protocol (CDP)        │
                                     │  web.whatsapp.com -> PDF + caption    │
                                     └───────────────────────────────────────┘
```

Why a COM DLL? Tally's TDL cannot launch programs or attach files by itself. The
documented way to reach outside Tally is a **COM Interface**
(`[COM Interface: ...]` + `EXEC COM INTERFACE`), which Tally has supported since
Tally.ERP 9 Release 4.6. The DLL does the browser automation.

### Why there is no Selenium

Chrome is driven over the **DevTools protocol** — the same remote-debugging
interface Chrome DevTools uses:

1. Chrome is started with `--remote-debugging-port=9339` and a dedicated
   `--user-data-dir`.
2. The DLL connects to the browser over `ClientWebSocket` (built into .NET).
3. It attaches to the existing tab, navigates it, clicks with
   `Input.dispatchMouseEvent`, types with `Input.insertText`, and hands the PDF
   to the page with `DOM.setFileInputFiles`.
4. Delivery is confirmed by counting the document bubbles
   (`div[data-testid='document-thumb']`) whose title contains the file name.

The result is one ~44 KB assembly with **no third-party dependencies** — no
chromedriver, no Selenium Manager, no `System.*.dll` next to Tally.exe, and no
assembly binding redirects. That matters because the DLL lives inside Tally's
process, where Tally owns the config file and a redirect cannot be added.

---

## Repository layout

| Path | Purpose |
| --- | --- |
| `TDL/WhatsAppSender.tdl` | The Tally TDL: form hook, PDF export, COM call, settings. |
| `src/TallyWhatsappSender/WhatsAppSender.cs` | The COM class: Chrome launch, numbers, archive, log. |
| `src/TallyWhatsappSender/WhatsAppAutomation.cs` | The WhatsApp Web flow and every selector. |
| `src/TallyWhatsappSender/Cdp.cs` | Tiny JSON reader + DevTools protocol client (no packages). |
| `src/TallyWhatsappSender/TallyWhatsappSender.csproj` | Project file (`net48`, `AnyCPU`, unsigned). |
| `scripts/build.ps1` | Builds the DLL. |
| `scripts/install.ps1` | Installs the DLL + TDL, registers COM, starts the QR login. |
| `scripts/login.ps1` | Opens the add-on's Chrome profile and waits for the QR scan. |
| `scripts/uninstall.ps1` | Unregisters and removes files (keeps the WhatsApp login). |
| `scripts/selftest.ps1` | Sends a PDF through the installed COM object, without Tally. |
| `test/` | Developer harness: mock WhatsApp page + live CDP test. |

---

## Requirements

* **Windows 11**
* **TallyPrime 2.1** or later (32-bit or 64-bit — the add-on handles both)
* **Google Chrome** installed
* **.NET Framework 4.8** (already present on Windows 11)
* To build: the **.NET SDK** (`dotnet`) — [download](https://dotnet.microsoft.com/download)
* Internet access so Chrome can load WhatsApp Web

There is **nothing to download** at install time (the old ChromeDriver step is
gone).

---

## Build

From the repository root (run the script with the execution policy bypassed so
it works even when scripts are blocked):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

or directly:

```powershell
dotnet build .\src\TallyWhatsappSender\TallyWhatsappSender.csproj -c Release
```

Output lands in `src\TallyWhatsappSender\bin\Release\` and contains only
`TallyWhatsappSender.dll` and its `.pdb`.

> `-ExecutionPolicy Bypass` applies to that one invocation only — it does **not**
> change the machine-wide policy.

---

## Install

From any PowerShell window:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

The script **self-elevates**: it detects that it is not running as Administrator,
prompts for UAC, and reopens itself in an elevated window automatically. You do
not need to open an Administrator window first.

> If the script is blocked with *"…cannot be loaded because running scripts is
> disabled on this system"*, the `-ExecutionPolicy Bypass` form above runs it for
> that one invocation only and does not change the machine-wide policy. From
> inside a PowerShell window you can alternatively unblock the current session
> once with `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`.

This will:

1. remove files left behind by the Selenium versions (≤ 1.0.13):
   `WebDriver.dll`, `chromedriver.exe`, the `System.*.dll` set and the
   `selenium-manager` folder next to `Tally.exe`,
2. copy `TallyWhatsappSender.dll` into the Tally working folder
   (default `C:\Program Files\TallyPrime` — change with `-TallyDir`),
3. copy the TDL into the TDL folder
   (default `C:\Users\Admin\OneDrive\TDL` — change with `-TdlDir`),
4. create the PDF export folder
   (default `C:\Users\Admin\OneDrive\Export` — change with `-OutputDir`),
5. register the COM DLL with **both** the 32-bit and 64-bit
   `regasm /codebase` (so it works whichever architecture TallyPrime is), then
   verify activation from both registry views and print the result,
6. open **`scripts\login.ps1`** in a normal (non-admin) window so you can scan
   the WhatsApp QR code right away.

> Chrome refuses to run elevated, so step 6 is started through `explorer.exe`,
> which launches it as the logged-on user.

The installation report looks like this:

```
  Tally executable  : C:\Program Files\TallyPrime\Tally.exe -> 64-bit (x64)
  COM activation (64-bit) : OK
  COM activation (32-bit) : OK
COM registration OK for the architecture Tally uses (64-bit).
  Google Chrome     : C:\Program Files\Google\Chrome\Application\chrome.exe
```

> `regasm` prints the advisory warning `RA0000` because the assembly is
> intentionally unsigned. That is harmless and not an error.

### Link WhatsApp (QR code)

The installer starts this for you. To do it later, or after WhatsApp logs the
device out:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\login.ps1
```

It opens `web.whatsapp.com` in the add-on's own Chrome profile and waits for you
to scan the code (**WhatsApp → Settings → Linked devices → Link a device**).
When the chat list appears it closes Chrome again.

Add `-KeepOpen` to leave the Chrome window open, and `-TimeoutSeconds 600` for a
longer window.

The profile is a normal, persistent folder:

```
%LOCALAPPDATA%\TallyWhatsAppSender\chrome-profile
```

so the link survives restarts and Windows updates. It is completely separate
from your everyday Chrome profile.

### Load the TDL in Tally

1. Press **F1** (Help) → **TDLs & Add-Ons** → **F4** (Manage Local TDLs).
2. Set **Load selected TDL files on startup** to **Yes**.
3. Enter the path to `C:\Users\Admin\OneDrive\TDL\WhatsAppSender.tdl` and accept.
4. **Restart TallyPrime** so it picks up the COM registration.

---

## Usage

1. Create or open a **Sales voucher**.
2. Make sure the party ledger has the mobile number:
   *Ledger → F12 → Provide Contact Details = Yes → **Mobile No.***
   Enter a **10-digit** number; `91` is added automatically. (You may put several
   comma-separated numbers to send to more than one contact.)
3. Save the voucher (**Ctrl+A**). If the party has a mobile number, Tally asks
   *"Send this invoice on WhatsApp?"*; parties without one are skipped silently.
4. The invoice is exported and Chrome opens WhatsApp Web in the dedicated
   profile. The PDF is attached, the caption is typed, Send is clicked, and the
   DLL verifies that a new document bubble appeared in the chat.

   Tally is blocked while this runs, so nothing else needs to be clicked in
   Tally — the result appears in a message box raised by the DLL.

### What the result looks like

* Success: `Success: invoice sent on WhatsApp to 919876543210`
* Failure: anything starting with `Error:` — the message names the stage
  (`WhatsApp Web is not logged in`, `the document preview did not appear`,
  `the Send click … did not produce a message`, …).

The **result is always shown in a message box raised by the DLL itself**. The
TDL has no `Msg Box` for it, because not every TallyPrime build fills
`$$LastResult` with the COM return value — an empty box made failures look like
silence.

* The send is **verified**: after the Send click, a new document bubble with the
  invoice file name must appear in the chat. If it does not, the click is
  retried once (Enter in the caption box) and then reported as an error rather
  than a false success.
* The PDF is written to `WAFolder\WhatsApp_Invoice.pdf`. The DLL keeps a dated
  copy in `WAFolder\sent\`.
* Chrome is started for the send and closed again afterwards. The WhatsApp
  login stays in the profile, so no QR scan is needed after the first time.

---

## Test without Tally

`scripts/selftest.ps1` calls the installed COM object directly, so the Chrome /
WhatsApp Web part can be tested without saving a voucher:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\selftest.ps1 -Number 9876543210
powershell -ExecutionPolicy Bypass -File .\scripts\selftest.ps1 -Number 9876543210 -Pdf C:\path\invoice.pdf
```

It prints the result and the last 30 log lines. Without `-Pdf` it picks the
newest PDF in `OneDrive\Export`, the Desktop, Downloads or Documents. It uses
`SendSilent` (same flow, no result dialog), so no Administrator rights and no
Tally restart are needed.

For development there is also a harness in `test/`:

```powershell
dotnet run --project .\test\CdpHarness -- chromium .\test\mock-whatsapp.html
```

That runs the whole attach → caption → send → verify flow against a local mock
page (headless Chromium), no WhatsApp account required. The live modes attach to
a real Chrome on port 9455:

```
CdpHarness --live <number> <pdf>      # send for real (scan the QR once)
CdpHarness --inspect <number> <pdf>   # dump the live WhatsApp DOM
CdpHarness --eval "<javascript>"      # run JavaScript in the attached tab
```

---

## Configuration

Everything you normally change lives in the `[System: Formulae]` block at the
bottom of `TDL/WhatsAppSender.tdl`:

| Formula | Meaning |
| --- | --- |
| `WAFolder` | Folder the invoice PDF is written to (default `C:\Users\Admin\OneDrive\Export`). Created automatically if missing. |
| `ChromeBinaryPath` | Full path to `chrome.exe`. If it is wrong, the DLL also checks the standard install folders, so a stale value does not break the add-on. |
| `GetContacts` | Where the number comes from (default `$LedgerMobile`). |
| `Caption` | Text sent with the PDF. |

Notes:

* A log is written to `%LOCALAPPDATA%\TallyWhatsAppSender\log.txt`, one flushed
  line per event.
* The Chrome profile used by the add-on lives in
  `%LOCALAPPDATA%\TallyWhatsAppSender\chrome-profile`.
* Chrome listens for automation on **port 9339**. If a Chrome window using the
  add-on profile is already open without that port, the DLL closes it and starts
  a clean one.

---

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Tally reports the COM call failed, or nothing happens on save | Re-run `scripts/install.ps1` (it self-elevates, registers **both** registry views and prints `COM registration OK`), then **restart TallyPrime**. |
| Not sure if registration took | `scripts/install.ps1` ends by creating the COM object from both a 64-bit and a 32-bit PowerShell and printing `COM activation (64-bit) : OK` / `COM activation (32-bit) : OK`, plus the architecture of your Tally executable. A failure prints the raw COM error under the architecture Tally uses. |
| *"Google Chrome was not found"* | Install Chrome. The add-on needs it for WhatsApp Web; then run `scripts\login.ps1`. |
| *"no valid 10-digit mobile number"* | The prompt only appears when the ledger's **Mobile No.** is filled, but the value is not a usable number. Correct it on the party ledger. |
| *"invoice PDF not found"* | The export failed. Check that `WAFolder` is writable and that the voucher print format is valid. |
| WhatsApp Web shows a QR code | The profile is not linked yet. Run `scripts\login.ps1` and scan the code; the login is stored in the dedicated profile, so this is needed only once (or after removing the device on your phone). |
| *"WhatsApp Web is not logged in"* | The 5-minute QR window expired. Save the voucher again, or run `scripts\login.ps1` first. |
| `opening https://web.whatsapp.com` is the last log line | Chrome loaded the page but the chat list never appeared. Check the machine's internet connection and whether WhatsApp Web is reachable in Chrome. |
| `Chrome started but its automation port 9339 did not open` | Another Chrome is using the add-on profile. Close it and try again; the log names the process it closed, if any. |
| `resolved pdf = … (exists = False)` | The PDF export did not produce the file. Check `WAFolder` and that the report exports (see `SVPrintFileName`/`SVExportFormat`). |
| `document preview did not appear` / attachment not sent | WhatsApp Web's page markup changed. Update the selector lists at the top of `WhatsAppAutomation.cs` (`AttachButtonXPaths`, `DocumentMenuItemXPaths`, `CaptionBoxXPaths`, `SendButtonXPaths`). The log prints the element it clicked at each step, which makes this quick. |
| `the Send click … did not produce a message in the chat` | The DLL clicked Send but no new document bubble appeared, so it reports a failure instead of a false success. The log shows whether the file input, the preview and the caption box were found; compare that with the Chrome window. |
| WhatsApp Web says the number is invalid | The normalised number is not registered on WhatsApp. The log records the number that was tried and the value from the party ledger. |
| A stray `about:blank` tab next to WhatsApp | Fixed in 1.1.0: the DLL now reuses Chrome's existing tab instead of opening a second one. Update the DLL with `scripts\install.ps1`. |
| Upgrading from 1.0.13 or older | Run the new `scripts/install.ps1`. It removes the old Selenium DLLs and `chromedriver.exe` from the Tally folder; nothing else is needed. |
| The voucher is **not saved** after the prompt | On a build where `On: Form Accept` replaces the default save, add a second handler so the form is accepted explicitly (see below). |
| TDL fails to load | Remove the line(s) the compiler complains about. The `EXPORT REPORT`/`SV*` variables are the most version-sensitive; see notes below. |

### Notes / known limitations

* `On: Form Accept` runs the send routine while the voucher is being accepted.
  On TallyPrime the voucher is still saved normally, so the function does **not**
  re-issue `FORM ACCEPT`. If a particular build leaves the voucher unsaved, add a
  second `On` handler so the default accept is restored *after* the send:

  ```tdl
  [#Form: Sales Color]
      On : Form Accept : Yes : CALL : WA_SendInvoiceOnWhatsApp
      On : Form Accept : Yes : Form Accept
  ```

  Only add the second line if you actually see the voucher not being saved —
  otherwise the form may be accepted twice.
* Browser automation of WhatsApp Web is inherently sensitive to UI changes. If a
  send stops working after a WhatsApp Web update, the selectors in
  `WhatsAppAutomation.cs` are the place to fix — nothing else needs to change.
* WhatsApp Web must stay linked on that machine's Chrome profile. Removing the
  linked device on your phone means running `scripts\login.ps1` again.
* The TDL uses `SVExportFormat`, `SVPrintFileName`, `SVOpenFileAfterExport` and
  `EXPORT REPORT` for the PDF. These are stable, but if your TallyPrime build
  rejects one of them, delete that line and export using Tally's own print
  configuration instead.

---

## Credits

Architecture inspired by
[`tejavarma-aln/TallyWhatsappSender`](https://github.com/tejavarma-aln/TallyWhatsappSender).
