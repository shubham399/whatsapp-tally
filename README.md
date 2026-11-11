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
| `src/TallyWhatsappSender/TallyWhatsappSender.csproj` | Project file (`net48`, `AnyCPU`, strong-named, Selenium 4). |
| `src/TallyWhatsappSender/TallyWhatsappSender.snk` | Public key used for the strong name. |
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
* Internet access the first time so WhatsApp Web can be logged in and, if you do
  not bundle `chromedriver.exe`, so Selenium Manager can fetch it.

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

> `chromedriver.exe` (optional): Selenium 4 can fetch a matching driver
> automatically, but for an offline machine drop a `chromedriver.exe` that
> matches your Chrome version into the build output. The DLL prefers a local
> `chromedriver.exe` if it finds one.

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

The DLL is **strong-named** (`src/TallyWhatsappSender/TallyWhatsappSender.snk`),
so `regasm` does not print the *"Registering an unsigned assembly with
/codebase"* warning (`RA0000`). That warning is only advisory and never caused
registration to fail — signing does not affect whether Tally can load the class.

The verification report looks like this:

```
  Registry view (32-bit)   OK   ProgId=True  CLSID=True
      CodeBase: C:\Program Files\TallyPrime\TallyWhatsappSender.dll
  Registry view (64-bit)   OK   ProgId=True  CLSID=True
      CodeBase: C:\Program Files\TallyPrime\TallyWhatsappSender.dll
  Tally executable: C:\Program Files\TallyPrime\Tally.exe -> 32-bit (x86)
COM registration OK in both registry views.
```

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
* If the 5 minutes pass without a scan, the dialog says so and nothing is sent —
  save the voucher again to retry.
* If the phone has no internet or WhatsApp Web is down, the dialog reports that
  the chat list never appeared; the log at
  `%LOCALAPPDATA%\TallyWhatsAppSender\log.txt` says which stage failed.

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
| Not sure if registration took | `scripts/install.ps1` ends with a per-view report (`Registry view (32-bit) OK ProgId=True CLSID=True`, the `CodeBase` path, and the architecture of the Tally executable it found). If a view says `MISSING`, the DLL could not be loaded by a client of that architecture. |
| *"no valid 10-digit mobile number"* | The prompt only appears when the ledger's **Mobile No.** is filled, but the value is not a usable 10-digit number. Correct it on the party ledger. |
| *"invoice PDF not found"* | The export failed. Check that `WAFolder` is writable and that the voucher print format is valid. |
| *"Could not load file or assembly 'System.Text.Json, Version=8.0.0.0' … 0x80131040"* | The DLL ships `System.Text.Json` 8.0.4, whose assembly version is 8.0.0.4, while Selenium asks for 8.0.0.0. Tally.exe owns the config file, so no binding redirect can be added; versions before 1.0.6 therefore failed. Update to 1.0.6 or later, which resolves the reference from the DLL's own folder (`AssemblyResolve` in `WhatsAppSender.cs`). |
| WhatsApp Web shows a QR code | First run only. Scan it within 5 minutes; the send continues automatically and the session is stored in `%LOCALAPPDATA%\TallyWhatsAppSender\chrome-profile`. To force a new code, close Chrome/Tally and delete that folder. |
| *"WhatsApp Web is not logged in"* | The 5-minute QR window expired. Save the voucher again and scan the code in the Chrome window while the prompt is waiting. |
| *"document preview did not appear"* / attachment not sent | WhatsApp Web's page markup changed. Update the selector lists at the top of `WhatsAppSender.cs` (`AttachButtonXPaths`, `SendButtonXPaths`, `ComposeBoxXPaths`, `FindFileInput`). |
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
