using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TallyWhatsappSender
{
    /// <summary>
    /// COM-visible bridge called from Tally TDL
    /// (see [COM Interface: WhatsAppEngine] in TDL\WhatsAppSender.tdl).
    ///
    /// Chrome is driven over its own DevTools protocol (see Cdp.cs), so this
    /// assembly has no third-party dependencies at all. That matters because it
    /// lives inside Tally's process, where Tally owns the config file and no
    /// assembly binding redirect can be added.
    /// </summary>
    [ComVisible(true)]
    [Guid("9f8e7d6c-5b4a-4938-8271-6a5b4c3d2e1f")]
    [ProgId("TallyWhatsappSender.WhatsAppSender")]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class WhatsAppSender
    {
        // A single local port is enough: one Tally user sends one invoice at a
        // time, and reusing the browser keeps the WhatsApp session warm.
        private const int DebugPort = 9339;

        // How long to wait for WhatsApp Web to become usable. This covers the
        // one-time QR-code login: Chrome opens, the user scans the code with
        // their phone, and the send continues by itself.
        private const int LoginTimeoutSeconds = 300;

        /// <summary>
        /// Called from TDL:
        ///   EXEC COM INTERFACE : WhatsAppEngine : contacts : pdfPath : caption : chromeBinary
        ///
        /// The outcome is displayed from here as well as returned. Not every
        /// TallyPrime build puts the COM return value into $$LastResult, and a
        /// missing dialog makes a failed send look like nothing happened at all.
        /// </summary>
        public string Send(string contacts, string pdfPath, string caption, string chromeBinary)
        {
            string result = Run(contacts, pdfPath, caption, chromeBinary);

            try
            {
                MessageBox.Show(
                    result,
                    "WhatsApp",
                    MessageBoxButtons.OK,
                    result.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                        ? MessageBoxIcon.Warning
                        : MessageBoxIcon.Information);
            }
            catch
            {
                // No interactive desktop: the log and the return value still
                // carry the result.
            }

            return result;
        }

        /// <summary>
        /// Same as Send, but without the dialog. Used by scripts\selftest.ps1 so
        /// the flow can be exercised outside Tally without a box to click.
        /// </summary>
        public string SendSilent(string contacts, string pdfPath, string caption, string chromeBinary)
        {
            return Run(contacts, pdfPath, caption, chromeBinary);
        }

        private string Run(string contacts, string pdfPath, string caption, string chromeBinary)
        {
            try
            {
                return SendCore(contacts, pdfPath, caption, chromeBinary);
            }
            catch (Exception ex)
            {
                return "Error: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private string SendCore(string contacts, string pdfPath, string caption, string chromeBinary)
        {
            var log = new StringBuilder();
            ChromeSession session = null;

            try
            {
                LogFile.Line(log, "Send() called");
                LogFile.Line(log, "contacts = " + contacts);
                LogFile.Line(log, "pdf      = " + pdfPath);
                LogFile.Line(log, "caption  = " + caption);
                LogFile.Line(log, "chrome   = " + chromeBinary);
                LogFile.Line(log, "process  = " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") +
                                  ", CLR " + Environment.Version);
                LogFile.Line(log, "basedir  = " + InstallDirectory());
                LogFile.Line(log, "host dir = " + AppDomain.CurrentDomain.BaseDirectory);

                if (string.IsNullOrWhiteSpace(pdfPath))
                {
                    LogFile.Line(log, "no PDF path was supplied - aborting");
                    return "Error: no invoice PDF path was supplied.";
                }

                pdfPath = ResolvePdfPath(pdfPath);
                LogFile.Line(log, "resolved pdf = " + pdfPath + " (exists = " + File.Exists(pdfPath) + ")");

                if (!File.Exists(pdfPath))
                {
                    LogFile.Line(log, "PDF not found - aborting");
                    return "Error: the invoice PDF was not found at " + pdfPath +
                           ". The export may have failed.";
                }

                List<string> numbers = NormalizeNumbers(contacts);
                LogFile.Line(log, "numbers  = " + string.Join(", ", numbers.ToArray()));

                if (numbers.Count == 0)
                {
                    LogFile.Line(log, "no usable number - aborting");
                    return "Error: no valid 10-digit mobile number was found for this party. " +
                           "Please enter a 10-digit mobile number in the party ledger.";
                }

                string profileDir = ProfileDirectory();
                bool launched;
                EnsureBrowser(chromeBinary, profileDir, log, out launched);

                LogFile.Line(log, "profile   = " + profileDir);
                LogFile.Line(log, "connecting to Chrome on port " + DebugPort + " ...");
                session = ChromeSession.Attach(DebugPort, launched);

                var automation = new WhatsAppAutomation(session, log);

                if (!automation.EnsureLoggedIn(LoginTimeoutSeconds))
                {
                    return "Error: WhatsApp Web is not logged in. The Chrome window should be showing " +
                           "a QR code - scan it with your phone (WhatsApp > Linked devices) and then " +
                           "save the voucher again. The login is remembered, so this is needed only once.";
                }

                string archivePath = ArchiveCopy(pdfPath);
                LogFile.Line(log, "archived = " + archivePath);

                foreach (string number in numbers)
                {
                    LogFile.Line(log, "sending to " + number);

                    string error = automation.SendOne(number, pdfPath, caption);
                    if (error != null) return error;
                }

                return "Success: invoice sent on WhatsApp to " + string.Join(", ", numbers.ToArray());
            }
            catch (Exception ex)
            {
                LogFile.Line(log, "ERROR " + ex.GetType().Name + ": " + ex.Message);
                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    LogFile.Line(log, ex.StackTrace);
                }
                return "Error: " + ex.Message;
            }
            finally
            {
                try { if (session != null) session.Dispose(); } catch { }
                LogFile.Line(log, "done");
                LogFile.Separator();
            }
        }

        // ----- Chrome ----------------------------------------------------------

        /// <summary>
        /// Makes sure a Chrome that this add-on can drive is running. Chrome is
        /// started with a dedicated, persistent profile so the WhatsApp Web
        /// login (the QR scan) survives restarts and the user's own Chrome
        /// profile is never touched.
        /// </summary>
        private static void EnsureBrowser(string chromeBinary, string profileDir,
                                          StringBuilder log, out bool launched)
        {
            if (ChromeSession.IsListening(DebugPort))
            {
                LogFile.Line(log, "Chrome is already listening on port " + DebugPort + " - reusing it");
                launched = false;
                return;
            }

            // A Chrome using the same profile but without the debug port would
            // swallow the new window (Chrome allows one process per profile),
            // and the port would never open. Such a window belongs to this
            // add-on, so close it and start cleanly.
            KillStaleChrome(profileDir, log);

            string chrome = ResolveChrome(chromeBinary);
            if (chrome == null)
            {
                throw new InvalidOperationException(
                    "Google Chrome was not found. Install Chrome, or correct ChromeBinaryPath in the TDL.");
            }

            Directory.CreateDirectory(profileDir);

            var start = new ProcessStartInfo(chrome);
            start.UseShellExecute = false;
            start.Arguments =
                "--remote-debugging-port=" + DebugPort.ToString(CultureInfo.InvariantCulture) +
                " --user-data-dir=\"" + profileDir + "\"" +
                " --no-first-run --no-default-browser-check" +
                " --disable-popup-blocking --disable-notifications --start-maximized" +
                " about:blank";

            LogFile.Line(log, "starting " + chrome);
            Process.Start(start);

            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (ChromeSession.IsListening(DebugPort))
                {
                    launched = true;
                    LogFile.Line(log, "Chrome is ready");
                    return;
                }
                Thread.Sleep(300);
            }

            throw new InvalidOperationException(
                "Chrome started but its automation port " + DebugPort + " did not open. " +
                "If a Chrome window using the add-on's profile is already open, close it and try again.");
        }

        private static void KillStaleChrome(string profileDir, StringBuilder log)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'chrome.exe'"))
                using (ManagementObjectCollection processes = searcher.Get())
                {
                    foreach (ManagementBaseObject item in processes)
                    {
                        string commandLine =
                            Convert.ToString(item["CommandLine"], CultureInfo.InvariantCulture) ?? "";

                        if (commandLine.IndexOf(profileDir, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        int pid;
                        try { pid = Convert.ToInt32(item["ProcessId"], CultureInfo.InvariantCulture); }
                        catch { continue; }

                        try
                        {
                            Process.GetProcessById(pid).Kill();
                            LogFile.Line(log, "closed a previous Chrome using the add-on's profile (pid " +
                                              pid.ToString(CultureInfo.InvariantCulture) + ")");
                        }
                        catch { }
                    }
                }

                Thread.Sleep(800);
            }
            catch (Exception ex)
            {
                LogFile.Line(log, "could not check for stale Chrome processes: " + ex.Message);
            }
        }

        /// <summary>
        /// Chrome's location from the TDL if it is valid, otherwise from the
        /// usual install folders / the "App Paths" registry entry. A stale path
        /// in the TDL therefore cannot break the add-on.
        /// </summary>
        private static string ResolveChrome(string chromeBinary)
        {
            if (!string.IsNullOrWhiteSpace(chromeBinary) && File.Exists(chromeBinary))
            {
                return chromeBinary;
            }

            var candidates = new List<string>();
            foreach (string variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "LocalAppData" })
            {
                string root = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
            }

            try
            {
                object registered = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
                    "",
                    null);
                string path = registered as string;
                if (!string.IsNullOrEmpty(path)) candidates.Add(path);
            }
            catch { }

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        private static string ProfileDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TallyWhatsAppSender", "chrome-profile");
        }

        // ----- phone numbers ---------------------------------------------------

        /// <summary>
        /// Accepts one or more comma separated numbers and returns WhatsApp-ready
        /// numbers. A plain 10-digit Indian mobile number gets the 91 country
        /// code prefixed automatically.
        /// </summary>
        private static List<string> NormalizeNumbers(string raw)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            foreach (string part in raw.Split(new[] { ',', ';', '/', '|', '\n' },
                                              StringSplitOptions.RemoveEmptyEntries))
            {
                string digits = Regex.Replace(part, @"\D", "");
                if (digits.Length == 0) continue;

                string normalized;
                if (digits.Length == 10)
                    normalized = "91" + digits;                     // 10-digit local number
                else if (digits.Length == 11 && digits[0] == '0')
                    normalized = "91" + digits.Substring(1);        // 0 + 10-digit
                else if (digits.Length == 12 && digits.StartsWith("91"))
                    normalized = digits;                            // already 91 + 10-digit
                else if (digits.Length == 13 && digits.StartsWith("091"))
                    normalized = "91" + digits.Substring(3);
                else if (digits.Length > 10)
                    normalized = digits;                            // assume a country code is present
                else
                    continue;                                       // too short to be valid

                if (!result.Contains(normalized)) result.Add(normalized);
            }
            return result;
        }

        // ----- misc ------------------------------------------------------------

        private static string InstallDirectory()
        {
            try
            {
                string location = typeof(WhatsAppSender).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    string folder = Path.GetDirectoryName(location);
                    if (!string.IsNullOrEmpty(folder)) return folder;
                }
            }
            catch { }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// Tally can hand us either an absolute path or one relative to its own
        /// working directory. If the file is not found as given, resolve it
        /// against this DLL's folder (the installer puts the DLL in the Tally
        /// working directory) and, as a last resort, by file name alone.
        /// </summary>
        private static string ResolvePdfPath(string pdfPath)
        {
            if (string.IsNullOrWhiteSpace(pdfPath)) return pdfPath;
            if (File.Exists(pdfPath)) return pdfPath;

            string[] roots =
            {
                SafeDirectory(() => Path.GetDirectoryName(typeof(WhatsAppSender).Assembly.Location)),
                SafeDirectory(() => AppDomain.CurrentDomain.BaseDirectory)
            };

            string fileName = Path.GetFileName(pdfPath);
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;

                if (!Path.IsPathRooted(pdfPath))
                {
                    string combined = Path.Combine(root, pdfPath);
                    if (File.Exists(combined)) return combined;
                }

                string byName = Path.Combine(root, fileName);
                if (File.Exists(byName)) return byName;
            }

            return pdfPath;
        }

        private static string SafeDirectory(Func<string> factory)
        {
            try { return factory(); } catch { return null; }
        }

        private static string ArchiveCopy(string pdfPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(pdfPath);
                string archiveDir = Path.Combine(dir, "sent");
                Directory.CreateDirectory(archiveDir);
                string name = "Invoice_" +
                              DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) +
                              ".pdf";
                string target = Path.Combine(archiveDir, name);
                File.Copy(pdfPath, target, true);
                return target;
            }
            catch { return ""; }
        }
    }
}
