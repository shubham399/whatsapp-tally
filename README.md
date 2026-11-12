# Tally WhatsApp Sender (TallyPrime 2.1 · Windows 11)

Send a **Sales invoice as a PDF** to the customer's **primary mobile number** on
**WhatsApp Web (`web.whatsapp.com`) in Google Chrome**, directly from Tally.

When you save a Sales voucher, Tally reads the party's mobile number from the
ledger. If the party has one, Tally asks whether to send the invoice on WhatsApp;
if you say yes, it exports the voucher to PDF and hands the job to a small helper
that drives Chrome on WhatsApp Web and sends the file. Parties **without** a
mobile number are skipped silently — no prompt, no export.

This is a modernised rebuild of
[`tejavarma-aln/TallyWhatsappSender`](https://github.com/tejavarma-aln/TallyWhatsappSender)
(built for TallyPrime 2.1, .NET Framework 4.8 and Selenium 4).

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
                                     │  Selenium 4 + chromedriver            │
                                     │  opens Chrome -> web.whatsapp.com     │
                                     │  attaches PDF -> sends                │
                                     └───────────────────────────────────────┘
```

Why a COM DLL? Tally's TDL cannot launch programs or attach files by itself. The
documented way to reach outside Tally is a **COM Interface**
(`[COM Interface: ...]` + `EXEC COM INTERFACE`), which Tally has supported since
Tally.ERP 9 Release 4.6. The DLL does the browser automation.

---

## Repository layout

| Path | Purpose |
| --- | --- |
| `TDL/WhatsAppSender.tdl` | The Tally TDL: form hook, PDF export, COM call, settings. |
| `src/TallyWhatsappSender/WhatsAppSender.cs` | The COM class that drives Chrome / WhatsApp Web. |
| `src/TallyWhatsappSender/TallyWhatsappSender.csproj` | Project file (`net48`, `AnyCPU`, unsigned, Selenium 4). |
| `scripts/build.ps1` | Builds the DLL. |
| `scripts/install.ps1` | Copies the DLLs to the Tally folder, copies the TDL to the TDL folder, registers COM, creates the export folder. |
| `scripts/uninstall.ps1` | Unregisters and removes files. |

---

## Requirements

* **Windows 11**
* **TallyPrime 2.1** or later (32-bit or 64-bit — the add-on handles both)
* **Google Chrome** installed
* **.NET Framework 4.8** (already present on Windows 11)
* To build: the **.NET SDK** (`dotnet`) — [download](https://dotnet.microsoft.com/download)
* Internet access the first time so WhatsApp Web can be logged in and so the
  installer can download the ChromeDriver that matches your Chrome.

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

Output lands in `src\TallyWhatsappSender\bin\Release\`.

> `-ExecutionPolicy Bypass` applies to that one invocation only — it does **not**
> change the machine-wide policy.

> `chromedriver.exe`: the installer downloads the build that matches the Chrome
> it finds and places it next to `Tally.exe`. The DLL also looks for drivers in
> Selenium's cache (`%USERPROFILE%\.cache\selenium\chromedriver`) and only then
> falls back to `selenium-manager.exe`. To refresh the driver after a Chrome
> update, re-run `scripts\install.ps1`.

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

1. copy the built DLLs into the Tally working folder
   (default `C:\Program Files\TallyPrime` — change with `-TallyDir`),
2. copy the TDL into the TDL folder
   (default `C:\Users\Admin\OneDrive\TDL` — change with `-TdlDir`),
3. create the PDF export folder
   (default `C:\Users\Admin\OneDrive\Export` — change with `-OutputDir`),
4. register the COM DLL with **both** the 32-bit and 64-bit
   `regasm /codebase` (so it works whichever architecture TallyPrime is), then
   verify both registry views and print the result.

The DLL is **not strong-named on purpose**. Selenium's `WebDriver.dll` has no
strong name, and a strong-named assembly cannot reference it: the CLR fails with
`HRESULT 0x80131044` (*"A strongly-named assembly is required"*) as soon as a
Selenium type is used, before the send flow can log anything. `regasm` prints
the advisory warning `RA0000` for an unsigned assembly, which is harmless.

The verification report creates the COM object the same way Tally does — once
from a 64-bit PowerShell and once from a 32-bit one — because the success or
failure of a registry-key check depends on which view the probing process sees:

```
  Tally executable  : C:\Program Files\TallyPrime\Tally.exe -> 64-bit (x64)
  COM activation (64-bit) : OK
  COM activation (32-bit) : OK
COM registration OK for the architecture Tally uses (64-bit).
```

If activation fails, the message is the exact COM error (for example
`80040154 Class not registered`), and it is reported against the architecture
your Tally executable actually uses.

Then load the TDL in Tally:

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
4. The first time, Chrome opens WhatsApp Web and shows a **QR code** — scan it
   with your phone (**WhatsApp → Linked devices → Link a device**). The send
   continues by itself once the chat list appears; you have **5 minutes** to
   scan before it gives up. The login is stored in a dedicated Chrome profile,
   so every later send goes straight through.

   Tally is blocked while this runs, so nothing else needs to be clicked in
   Tally — just scan the code in the Chrome window that opens on top.

### The login (QR) flow

```
 save voucher ─► Chrome starts with its own profile
                     │
                     ├─ already linked ──► chat opens ──► PDF + caption ──► sent
                     │
                     └─ first run / logged out
                              │
                              ▼
                        QR code shown  ──► you scan it (5 min window)
                              │
                              ▼
                        chat opens ──► PDF + caption ──► sent
```

* The profile lives in `%LOCALAPPDATA%\TallyWhatsAppSender\chrome-profile`, so
  it is per Windows user and separate from your normal Chrome profile.
* WhatsApp keeps the link until you remove it on your phone or delete that
  folder. To force a fresh QR code, close Tally and delete the folder.
* **The result is always shown in a message box raised by the DLL itself.** The
  TDL has no `Msg Box` for it, because not every TallyPrime build fills
  `$$LastResult` with the COM return value — an empty box made failures look
  like silence. Success reads `Success: invoice sent on WhatsApp to …`; anything
  else starts with `Error:` and names the stage.
* The send is **verified**: after the Send click, a new document bubble with the
  invoice file name must appear in the chat. If it does not, the click is
  retried once (Enter in the caption box) and then reported as an error rather
  than a false success.
* If the 5 minutes pass without a scan, the dialog says so and nothing is sent —
  save the voucher again to retry.
* If the phone has no internet or WhatsApp Web is down, the dialog reports that
  the chat list never appeared; the log at
  `%LOCALAPPDATA%\TallyWhatsAppSender\log.txt` says which stage failed.

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

---

## Configuration

Everything you normally change lives in the `[System: Formulae]` block at the
bottom of `TDL/WhatsAppSender.tdl`:

| Formula | Meaning |
| --- | --- |
| `WAFolder` | Folder the invoice PDF is written to (default `C:\Users\Admin\OneDrive\Export`). Created automatically if missing. |
| `ChromeBinaryPath` | Full path to `chrome.exe`. |
| `GetContacts` | Where the number comes from (default `$LedgerMobile`). |
| `Caption` | Text sent with the PDF. |

Notes:

* The PDF is written to `WAFolder\WhatsApp_Invoice.pdf`. The DLL keeps a dated
  copy in `WAFolder\sent\`.
* A log is written to `%LOCALAPPDATA%\TallyWhatsAppSender\log.txt`.
* The Chrome profile used by the add-on lives in
  `%LOCALAPPDATA%\TallyWhatsAppSender\chrome-profile`.

---

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Tally reports the COM call failed, or nothing happens on save | Re-run `scripts/install.ps1` (it self-elevates, registers **both** registry views and prints `COM registration OK`), then **restart TallyPrime**. The TDL now calls the COM interface directly and shows Tally's own error instead of a generic "not available" message. |
| Not sure if registration took | `scripts/install.ps1` ends by creating the COM object from both a 64-bit and a 32-bit PowerShell and printing `COM activation (64-bit) : OK` / `COM activation (32-bit) : OK`, plus the architecture of your Tally executable. A failure prints the raw COM error under the architecture Tally uses. Registry-key checks are not used: whether they pass depends on the probing process's view. |
| *"no valid 10-digit mobile number"* | The prompt only appears when the ledger's **Mobile No.** is filled, but the value is not a usable 10-digit number. Correct it on the party ledger. |
| *"invoice PDF not found"* | The export failed. Check that `WAFolder` is writable and that the voucher print format is valid. |
| `Could not load file or assembly 'WebDriver … PublicKeyToken=null' … 0x80131044` | The DLL was strong-named while Selenium's `WebDriver.dll` is not. A strong-named assembly cannot reference a weak-named one, so the send flow died before logging anything — which looked like "no error, nothing sent". Update to 1.0.11 or later (the assembly is unsigned). |
| *"Could not load file or assembly 'System.Text.Json, Version=8.0.0.0' …"* | Selenium 4.25 depends on `System.Text.Json`, whose shipped assembly version (8.0.0.4) does not match the reference (8.0.0.0), and Tally.exe owns the config file so no binding redirect can be applied. Versions before 1.0.13 therefore failed when the first Selenium type was used. 1.0.13 uses Selenium 4.9, which has no `System.Text.Json` dependency. |
| WhatsApp Web shows a QR code | First run only. Scan it within 5 minutes; the send continues automatically and the session is stored in `%LOCALAPPDATA%\TallyWhatsAppSender\chrome-profile`. To force a new code, close Chrome/Tally and delete that folder. |
| *"WhatsApp Web is not logged in"* | The 5-minute QR window expired. Save the voucher again and scan the code in the Chrome window while the prompt is waiting. |
| The log stops after `chrome = …` | It no longer can: every line is flushed to the file as it happens, so the last line names the stage that failed. Older builds buffered the whole block until the end. |
| `resolved pdf = … (exists = False)` | The PDF export did not produce the file. Check `WAFolder` and that the voucher report exports (see `SVPrintFileName`/`SVExportFormat`). |
| `starting Chrome ...` is the last line | Chrome or the driver did not start. The log lists each driver tried (`bundled driver failed: …`, then cached drivers, then `selenium-manager = …`). Re-run `scripts\install.ps1` to download a `chromedriver.exe` matching the current Chrome; it is placed next to `Tally.exe`. |
| `The type initializer for 'OpenQA.Selenium.Internal.SeleniumManager' threw an exception` | Older builds let Selenium locate its manager through `AppContext.BaseDirectory`, which points at the host process (Tally or PowerShell) instead of the DLL. Update to 1.0.12 or later, which runs the bundled `selenium-manager.exe` itself and uses the `driver_path` it prints. |
| `document preview did not appear` / attachment not sent | WhatsApp Web's page markup changed. Update the selector lists at the top of `WhatsAppSender.cs` (`AttachButtonXPaths`, `DocumentMenuItemXPaths`, `CaptionBoxXPaths`, `SendButtonXPaths`, `FindFileInput`). |
| `the Send click … did not produce a message in the chat` | The DLL clicked Send but no new document bubble appeared, so it reports a failure instead of a false success. The log shows whether the file input, the preview and the caption box were found; compare that with the Chrome window. |
| WhatsApp Web says the number is invalid | The normalised number is not registered on WhatsApp. The log records the number that was tried and the value from the party ledger. |
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
  send stops working after a WhatsApp Web update, the selectors are the place to
  fix.
* WhatsApp Web must stay logged in on that machine's Chrome profile.
* The TDL uses `SVExportFormat`, `SVPrintFileName`, `SVOpenFileAfterExport` and
  `EXPORT REPORT` for the PDF. These are stable, but if your TallyPrime build
  rejects one of them, delete that line and export using Tally's own print
  configuration instead.

---

## Credits

Architecture inspired by
[`tejavarma-aln/TallyWhatsappSender`](https://github.com/tejavarma-aln/TallyWhatsappSender).
