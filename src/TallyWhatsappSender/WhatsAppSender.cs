using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace TallyWhatsappSender
{
    /// <summary>
    /// COM-visible bridge called from Tally TDL
    /// (see [COM Interface: WhatsAppEngine] in TDL\WhatsAppSender.tdl).
    ///
    /// It drives Google Chrome over the WhatsApp Web site and sends an invoice
    /// PDF that Tally has already exported.
    /// </summary>
    [ComVisible(true)]
    [Guid("9f8e7d6c-5b4a-4938-8271-6a5b4c3d2e1f")]
    [ProgId("TallyWhatsappSender.WhatsAppSender")]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class WhatsAppSender
    {
        /// <summary>
        /// Tally.exe is the process that loads us, so it owns the AppDomain and
        /// its configuration file - which means we cannot add assembly binding
        /// redirects to it. When a dependency's assembly version differs from
        /// the version Selenium references (for example System.Text.Json
        /// 8.0.0.4 on disk vs 8.0.0.0 in WebDriver.dll), binding fails with
        /// HRESULT 0x80131040 before our code can run. This handler resolves
        /// such a reference from the DLL's own folder.
        /// </summary>
        static WhatsAppSender()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromInstallFolder;
        }

        private static Assembly ResolveFromInstallFolder(object sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name);
                var folder = Path.GetDirectoryName(typeof(WhatsAppSender).Assembly.Location);
                if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(requested.Name))
                {
                    return null;
                }

                var candidate = Path.Combine(folder, requested.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return Assembly.LoadFrom(candidate);
                }
            }
            catch
            {
                // Fall through: let the default probing continue / report.
            }

            return null;
        }
        // WhatsApp Web's markup changes from time to time. Every selector is
        // collected here so they can be updated without touching the flow.

        private static readonly string[] ComposeBoxXPaths =
        {
            "//footer//div[@contenteditable='true']",
            "//footer//div[@role='textbox']",
            "//div[@id='main']//footer//div[@contenteditable='true']"
        };

        // The caption field of the attachment preview. It is identified by its
        // aria-label or by being a contenteditable inside #main but outside the
        // footer (which rules out the normal compose box and the chat-list
        // search field).
        private static readonly string[] CaptionBoxXPaths =
        {
            "//div[@aria-label='Add a caption']",
            "//div[contains(@aria-label,'aption')]",
            "//div[@id='main']//div[@contenteditable='true'][not(ancestor::footer)]",
            "//div[@id='main']//div[@role='textbox'][@contenteditable='true'][not(ancestor::footer)]"
        };

        private static readonly string[] SendButtonXPaths =
        {
            "//span[@data-icon='send']",
            "//span[@data-icon='wds-ic-send-filled']",
            "//span[@data-icon='send-filled']",
            "//span[@data-icon='wds-ic-send']",
            "//button[@aria-label='Send']",
            "//div[@role='button'][@aria-label='Send']",
            "//div[@aria-label='Send']",
            "//button[@data-testid='send']"
        };

        private static readonly string[] AttachButtonXPaths =
        {
            "//span[@data-icon='plus']",
            "//span[@data-icon='plus-rounded']",
            "//span[@data-icon='wds-ic-add']",
            "//span[@data-icon='clip']",
            "//span[@data-icon='attach']",
            "//button[@aria-label='Attach']",
            "//div[@role='button'][@aria-label='Attach']",
            "//div[@title='Attach']",
            "//button[@title='Attach']",
            "//div[@role='button'][.//span[@data-icon='plus']]"
        };

        // Clicked only when no file input exists after the attach menu opens.
        private static readonly string[] DocumentMenuItemXPaths =
        {
            "//*[@aria-label='Document']",
            "//button[@aria-label='Document']",
            "//div[@role='menuitem'][contains(.,'Document')]",
            "//li[contains(.,'Document')]",
            "//*[normalize-space(text())='Document']"
        };

        // The chat list only exists once WhatsApp Web is logged in.
        private static readonly string[] LoggedInXPaths =
        {
            "//div[@id='pane-side']",
            "//div[@aria-label='Chat list']",
            "//div[@data-testid='chat-list']"
        };

        // The "scan this with your phone" screen, shown until the device is linked.
        private static readonly string[] QrCodeXPaths =
        {
            "//canvas[@aria-label]",
            "//div[@data-ref]",
            "//*[contains(translate(text(),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'link with phone number')]"
        };

        // How long to wait for WhatsApp Web to become usable. This covers the
        // one-time QR-code login: Chrome opens, the user scans the code with
        // their phone, and the send continues by itself.
        private const int LoginTimeoutSeconds = 300;
        private const int ElementTimeoutSeconds = 60;

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
            IWebDriver driver = null;
            try
            {
                Log(log, "Send() called");
                Log(log, "contacts = " + contacts);
                Log(log, "pdf      = " + pdfPath);
                Log(log, "caption  = " + caption);
                Log(log, "chrome   = " + chromeBinary);
                Log(log, "process  = " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") +
                         ", CLR " + Environment.Version);
                Log(log, "basedir  = " + InstallDirectory());
                Log(log, "host dir = " + AppDomain.CurrentDomain.BaseDirectory);

                if (string.IsNullOrWhiteSpace(pdfPath))
                {
                    Log(log, "no PDF path was supplied - aborting");
                    return "Error: no invoice PDF path was supplied.";
                }

                pdfPath = ResolvePdfPath(pdfPath);
                Log(log, "resolved pdf = " + pdfPath + " (exists = " + File.Exists(pdfPath) + ")");

                if (!File.Exists(pdfPath))
                {
                    Log(log, "PDF not found - aborting");
                    return "Error: the invoice PDF was not found at " + pdfPath +
                           ". The export may have failed.";
                }

                List<string> numbers = NormalizeNumbers(contacts);
                Log(log, "numbers  = " + string.Join(", ", numbers.ToArray()));

                if (numbers.Count == 0)
                {
                    Log(log, "no usable number - aborting");
                    return "Error: no valid 10-digit mobile number was found for this party. " +
                           "Please enter a 10-digit mobile number in the party ledger.";
                }

                // Resolve everything from the folder that holds this DLL, not
                // from AppDomain.BaseDirectory. When the add-on is called from
                // Tally the two are the same, but the self-test runs inside
                // powershell.exe, whose base directory has none of our files.
                string baseDir = InstallDirectory();
                string driverExe = Path.Combine(baseDir, "chromedriver.exe");

                ChromeOptions options = BuildChromeOptions(chromeBinary);
                Log(log, "profile   = " + ProfileDirectory());
                Log(log, "driver    = " + (File.Exists(driverExe) ? driverExe : "none, asking Selenium Manager"));
                Log(log, "starting Chrome ...");

                driver = CreateDriver(baseDir, driverExe, options, log);
                Log(log, "Chrome started, browser version = " + SafeBrowserVersion(driver));

                if (!EnsureLoggedIn(driver, log))
                {
                    return "Error: WhatsApp Web is not logged in. The Chrome window should be showing " +
                           "a QR code - scan it with your phone (WhatsApp > Linked devices) and then " +
                           "save the voucher again. The login is remembered, so this is needed only once.";
                }

                string archivePath = ArchiveCopy(pdfPath);
                Log(log, "archived = " + archivePath);

                foreach (string number in numbers)
                {
                    Log(log, "sending to " + number);

                    driver.Navigate().GoToUrl("https://web.whatsapp.com/send?phone=" + number);
                    DismissAlert(driver);

                    if (!WaitUntil(() => FindVisible(driver, ComposeBoxXPaths) != null,
                                   TimeSpan.FromSeconds(ElementTimeoutSeconds)))
                    {
                        if (FindText(driver, "invalid") != null)
                        {
                            Log(log, "WhatsApp Web reports the number as invalid");
                            return "Error: WhatsApp Web says " + number + " is not a valid WhatsApp " +
                                   "number. Check the Mobile No. in the party ledger.";
                        }
                        return "Error: WhatsApp Web did not open a chat for " + number + ". " +
                               "Check that the number is registered on WhatsApp.";
                    }

                    // Let any pending history render before counting, so a late
                    // load cannot look like a newly sent message.
                    Thread.Sleep(2000);

                    string fileBase = Path.GetFileNameWithoutExtension(pdfPath);
                    int messagesBefore = OutgoingMessageCount(driver);
                    int documentsBefore = DocumentMessageCount(driver, fileBase);
                    Log(log, "chat ready: " + messagesBefore + " outgoing message(s), " +
                             documentsBefore + " with this file name");

                    string attachError = AttachDocument(driver, pdfPath, log);
                    if (attachError != null)
                        return attachError;

                    Log(log, "handed " + Path.GetFileName(pdfPath) + " to the page - waiting for the preview");

                    if (!WaitUntil(() => FindVisible(driver, CaptionBoxXPaths) != null,
                                   TimeSpan.FromSeconds(ElementTimeoutSeconds)))
                    {
                        return "Error: the document preview did not appear for " + number + ". " +
                               "The file was handed to WhatsApp Web but it did not show the preview.";
                    }

                    if (!string.IsNullOrEmpty(caption))
                    {
                        IWebElement box = FindVisible(driver, CaptionBoxXPaths);
                        if (box != null)
                        {
                            try
                            {
                                ClickElement(driver, box);
                                box.SendKeys(Flatten(caption));
                                Log(log, "typed the caption");
                            }
                            catch (Exception ex) { Log(log, "caption failed: " + ex.Message); }
                        }
                        else
                        {
                            Log(log, "caption box not found - sending without a caption");
                        }
                    }

                    IWebElement send = FindVisible(driver, SendButtonXPaths);
                    if (send == null)
                        return "Error: could not find the Send button for " + number + ".";

                    ClickElement(driver, send);
                    Log(log, "clicked send for " + number);

                    // The document bubble only appears once WhatsApp accepts the
                    // attachment. Counting it (rather than trusting the click)
                    // is what turns "no error, but nothing arrived" into a real
                    // answer for the user.
                    bool delivered = WaitUntil(
                        () => Sent(driver, fileBase, documentsBefore, messagesBefore),
                        TimeSpan.FromSeconds(ElementTimeoutSeconds));

                    if (!delivered)
                    {
                        // Enter in the caption box sends the pending attachment;
                        // try it once if the Send click did not take.
                        Log(log, "no new message appeared - pressing Enter as a fallback");
                        IWebElement box = FindVisible(driver, CaptionBoxXPaths);
                        if (box != null)
                        {
                            try
                            {
                                ClickElement(driver, box);
                                box.SendKeys(OpenQA.Selenium.Keys.Enter);
                            }
                            catch { }
                            delivered = WaitUntil(
                                () => Sent(driver, fileBase, documentsBefore, messagesBefore),
                                TimeSpan.FromSeconds(20));
                        }
                    }

                    if (!delivered)
                    {
                        return "Error: the Send click for " + number + " did not produce a message in the chat. " +
                               "Check the Chrome window; the log records which step found what.";
                    }

                    Log(log, "message appears in the chat for " + number);
                    Thread.Sleep(5000); // let the upload finish before the next contact
                }

                return "Success: invoice sent on WhatsApp to " + string.Join(", ", numbers.ToArray());
            }
            catch (Exception ex)
            {
                Log(log, "ERROR " + ex.GetType().Name + ": " + ex.Message);
                for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
                {
                    Log(log, "  caused by " + inner.GetType().Name + ": " + inner.Message);
                }
                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    Log(log, ex.StackTrace);
                }
                return "Error: " + ex.Message;
            }
            finally
            {
                try { if (driver != null) driver.Quit(); } catch { }
                Log(log, "done");
                WriteLog(log);
            }
        }

        // ----- Chrome / driver -------------------------------------------------

        private static ChromeOptions BuildChromeOptions(string chromeBinary)
        {
            var options = new ChromeOptions();
            if (!string.IsNullOrWhiteSpace(chromeBinary) && File.Exists(chromeBinary))
                options.BinaryLocation = chromeBinary;

            // A dedicated, persistent profile keeps the WhatsApp Web login
            // (the QR scan) between sessions and does not touch the user's own
            // Chrome profile.
            string profileDir = ProfileDirectory();
            Directory.CreateDirectory(profileDir);

            options.AddArgument("--user-data-dir=" + profileDir);
            options.AddArgument("--no-first-run");
            options.AddArgument("--no-default-browser-check");
            options.AddArgument("--disable-popup-blocking");
            options.AddArgument("--disable-notifications");
            options.AddArgument("--start-maximized");
            return options;
        }

        private static string ProfileDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TallyWhatsAppSender", "chrome-profile");
        }

        private static string SafeBrowserVersion(IWebDriver driver)
        {
            try
            {
                var chrome = driver as ChromeDriver;
                if (chrome == null) return "unknown";
                object version = chrome.Capabilities.GetCapability("browserVersion");
                return version != null ? version.ToString() : "unknown";
            }
            catch { return "unknown"; }
        }

        private static IWebDriver CreateDriver(string baseDir, string driverExe, ChromeOptions options,
                                                StringBuilder log)
        {
            if (File.Exists(driverExe))
            {
                IWebDriver created = TryStartChrome(driverExe, options, log);
                if (created != null) return created;
                Log(log, "the bundled driver did not work - trying a cached Selenium driver");
            }

            foreach (string cached in CachedDriverPaths())
            {
                IWebDriver created = TryStartChrome(cached, options, log);
                if (created != null)
                {
                    Log(log, "using cached driver " + cached);
                    return created;
                }
            }

            // Ask the bundled selenium-manager.exe directly. Selenium's
            // SeleniumManager wrapper locates the binary through
            // AppContext.BaseDirectory, which inside powershell.exe (or any
            // host other than Tally) points at the host's folder - that is why
            // the wrapper failed with a type-initializer exception.
            string managerExe = FindSeleniumManager(baseDir);
            if (managerExe != null)
            {
                string managedDriver = RunSeleniumManager(managerExe, log);
                if (!string.IsNullOrEmpty(managedDriver) && File.Exists(managedDriver))
                {
                    IWebDriver created = TryStartChrome(managedDriver, options, log);
                    if (created != null) return created;
                }
            }

            // Last resort: Selenium's own manager. It only works when the host's
            // base directory happens to contain the selenium-manager folder, but
            // try it so nothing is lost.
            try
            {
                return new ChromeDriver(options);
            }
            catch (Exception ex)
            {
                string hint = managerExe != null
                    ? ""
                    : " The bundled Selenium driver manager was not found under " + baseDir + ".";
                throw new InvalidOperationException(
                    "Chrome did not start. No working chromedriver.exe was found next to the DLL or in " +
                    "the Selenium cache, and Selenium Manager could not provide one." + hint +
                    " Re-run scripts\\install.ps1 (it downloads the driver that matches Chrome); check " +
                    "that Google Chrome is installed and that this machine has internet access. " +
                    "Original error: " + RootMessage(ex), ex);
            }
        }

        private static IWebDriver TryStartChrome(string driverExe, ChromeOptions options, StringBuilder log)
        {
            try
            {
                ChromeDriverService service = ChromeDriverService.CreateDefaultService(
                    Path.GetDirectoryName(driverExe), Path.GetFileName(driverExe));
                service.HideCommandPromptWindow = true;
                return new ChromeDriver(service, options, TimeSpan.FromSeconds(LoginTimeoutSeconds));
            }
            catch (Exception ex)
            {
                Log(log, "driver " + driverExe + " failed: " + RootMessage(ex));
                return null;
            }
        }

        /// <summary>
        /// Drivers downloaded earlier by Selenium Manager live under
        /// %USERPROFILE%\.cache\selenium\chromedriver\win64\&lt;version&gt;.
        /// Trying them means a Chrome update no longer needs a fresh install:
        /// if one of the newer cached drivers matches, it is used.
        /// </summary>
        private static IEnumerable<string> CachedDriverPaths()
        {
            string root;
            try
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cache", "selenium", "chromedriver");
            }
            catch { yield break; }

            foreach (string platform in new[] { "win64", "win32" })
            {
                string platformDir = Path.Combine(root, platform);
                string[] versions;
                try
                {
                    if (!Directory.Exists(platformDir)) continue;
                    versions = Directory.GetDirectories(platformDir);
                }
                catch { continue; }

                Array.Sort(versions);
                Array.Reverse(versions);

                foreach (string versionDir in versions)
                {
                    string exe = Path.Combine(versionDir, "chromedriver.exe");
                    if (File.Exists(exe)) yield return exe;
                }
            }
        }

        private static string FindSeleniumManager(string baseDir)
        {
            string[] candidates =
            {
                Path.Combine(baseDir, "selenium-manager", "windows", "selenium-manager.exe"),
                Path.Combine(baseDir, "selenium-manager", "selenium-manager.exe")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Runs selenium-manager.exe and returns the driver path it reports, or
        /// null. The tool caches the driver and re-downloads only when Chrome has
        /// moved to a new version, so this also survives Chrome updates.
        /// </summary>
        private static string RunSeleniumManager(string managerExe, StringBuilder log)
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = managerExe,
                    Arguments = "--browser chrome --output json",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                Log(log, "selenium-manager = " + managerExe);

                using (Process process = Process.Start(start))
                {
                    // Read both streams asynchronously: reading one to the end
                    // while the other fills its buffer can deadlock.
                    System.Threading.Tasks.Task<string> outTask = process.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> errTask = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(120000))
                    {
                        try { process.Kill(); } catch { }
                        Log(log, "selenium-manager timed out");
                        return null;
                    }

                    string stdout = outTask.Result;
                    string stderr = errTask.Result;

                    Log(log, "selenium-manager exit = " + process.ExitCode);
                    if (!string.IsNullOrEmpty(stdout)) Log(log, "selenium-manager out: " + Shorten(stdout, 600));
                    if (!string.IsNullOrEmpty(stderr)) Log(log, "selenium-manager err: " + Shorten(stderr, 600));

                    string driverPath = ExtractJsonString(stdout, "driver_path");
                    if (!string.IsNullOrEmpty(driverPath))
                        Log(log, "Selenium Manager driver = " + driverPath);
                    return driverPath;
                }
            }
            catch (Exception ex)
            {
                Log(log, "selenium-manager failed: " + RootMessage(ex));
                return null;
            }
        }

        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                Match match = Regex.Match(
                    json,
                    "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (!match.Success) return null;

                // JSON escapes backslashes in the Windows path.
                return Regex.Unescape(match.Groups[1].Value);
            }
            catch { return null; }
        }

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
        /// The message of the innermost exception: that is where the useful
        /// detail is (missing assembly, driver error, and so on).
        /// </summary>
        private static string RootMessage(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static string Shorten(string text, int max)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        /// <summary>
        /// Makes sure the Chrome profile is linked to a WhatsApp account.
        ///
        /// On the very first run the dedicated profile has no session, so
        /// WhatsApp Web shows the QR code. Chrome is already in the foreground;
        /// the user scans the code with their phone (WhatsApp > Linked devices)
        /// and the loop below notices the chat list and continues. WhatsApp
        /// keeps the session in the profile folder, so later sends skip this.
        /// </summary>
        private static bool EnsureLoggedIn(IWebDriver driver, StringBuilder log)
        {
            driver.Navigate().GoToUrl("https://web.whatsapp.com");
            DismissAlert(driver);

            DateTime end = DateTime.UtcNow.AddSeconds(LoginTimeoutSeconds);
            bool qrSeen = false;

            while (DateTime.UtcNow < end)
            {
                if (FindVisible(driver, LoggedInXPaths) != null)
                {
                    Log(log, "WhatsApp Web is logged in");
                    return true;
                }

                if (!qrSeen && FindVisible(driver, QrCodeXPaths) != null)
                {
                    qrSeen = true;
                    Log(log, "QR code displayed - waiting for the phone to link this device");
                }

                Thread.Sleep(1000);
            }

            Log(log, qrSeen
                ? "still on the QR screen after " + LoginTimeoutSeconds + "s; the code was not scanned"
                : "chat list did not appear after " + LoginTimeoutSeconds + "s");
            return false;
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

        // ----- web helpers -----------------------------------------------------

        private static IWebElement FindVisible(IWebDriver driver, string[] xpaths)
        {
            foreach (string xp in xpaths)
            {
                try
                {
                    IReadOnlyCollection<IWebElement> elements = driver.FindElements(By.XPath(xp));
                    foreach (IWebElement e in elements)
                    {
                        try { if (e.Displayed && e.Enabled) return e; } catch { }
                    }
                }
                catch { }
            }
            return null;
        }

        private static IWebElement FindFileInput(IWebDriver driver)
        {
            try
            {
                IReadOnlyCollection<IWebElement> inputs =
                    driver.FindElements(By.CssSelector("input[type='file']"));

                IWebElement first = null;
                foreach (IWebElement input in inputs)
                {
                    if (first == null) first = input;
                    string accept = "";
                    try { accept = (input.GetAttribute("accept") ?? "").ToLowerInvariant(); } catch { }

                    // The document input accepts PDFs/application files; the
                    // photo and video entries explicitly limit themselves.
                    if (accept.Contains("pdf") || accept.Contains("application"))
                        return input;
                    if (!accept.Contains("image") && !accept.Contains("video"))
                        return input;
                }
                return first;
            }
            catch { return null; }
        }

        private static IWebElement WaitForFileInput(IWebDriver driver, TimeSpan timeout)
        {
            IWebElement found = null;
            WaitUntil(() => (found = FindFileInput(driver)) != null, timeout);
            return found;
        }

        /// <summary>
        /// Hands the PDF to WhatsApp Web and returns null on success, or the
        /// error text to show. The file input usually exists as soon as the
        /// attach menu has been opened; the Document menu entry is only clicked
        /// when no input can be found.
        /// </summary>
        private static string AttachDocument(IWebDriver driver, string pdfPath, StringBuilder log)
        {
            IWebElement input = WaitForFileInput(driver, TimeSpan.FromSeconds(3));

            if (input == null)
            {
                Log(log, "no file input yet - opening the attach menu");
                IWebElement attach = FindVisible(driver, AttachButtonXPaths);
                if (attach == null)
                {
                    return "Error: could not find the WhatsApp Web attach button. " +
                           "The page layout may have changed; open WhatsApp Web in Chrome and check the log.";
                }

                ClickElement(driver, attach);
                input = WaitForFileInput(driver, TimeSpan.FromSeconds(10));
            }

            if (input == null)
            {
                Log(log, "still no file input - clicking the Document entry");
                IWebElement document = FindVisible(driver, DocumentMenuItemXPaths);
                if (document != null)
                {
                    ClickElement(driver, document);
                    input = WaitForFileInput(driver, TimeSpan.FromSeconds(5));
                }
            }

            if (input == null)
            {
                return "Error: could not find the WhatsApp Web attachment control. " +
                       "The page layout may have changed; open WhatsApp Web in Chrome and check the log.";
            }

            Log(log, "file input found - setting " + Path.GetFileName(pdfPath));
            input.SendKeys(pdfPath);
            return null;
        }

        private static void ClickElement(IWebDriver driver, IWebElement element)
        {
            try { element.Click(); return; } catch { }

            var js = driver as IJavaScriptExecutor;
            if (js == null) return;

            try { js.ExecuteScript("arguments[0].scrollIntoView({block:'center'});", element); } catch { }
            try { js.ExecuteScript("arguments[0].click();", element); return; } catch { }

            // The clickable target is often a parent button, not the icon span
            // Selenium found.
            try
            {
                IWebElement parent = element.FindElement(
                    By.XPath("ancestor::button[1] | ancestor::div[@role='button'][1]"));
                js.ExecuteScript("arguments[0].click();", parent);
            }
            catch { }
        }

        /// <summary>
        /// WhatsApp Web shows the new document bubble as soon as it accepts the
        /// attachment. A new bubble is the proof that the send really happened;
        /// the previous "preview closed" check could pass on a page that never
        /// had a preview in the first place.
        /// </summary>
        private static bool Sent(IWebDriver driver, string fileBase, int documentsBefore, int messagesBefore)
        {
            if (DocumentMessageCount(driver, fileBase) > documentsBefore) return true;

            // Some WhatsApp Web versions render the file name in a way the text
            // search cannot see. Fall back to "a new outgoing message appeared
            // AND the attachment preview is gone".
            return OutgoingMessageCount(driver) > messagesBefore &&
                   FindVisible(driver, CaptionBoxXPaths) == null;
        }

        private static int OutgoingMessageCount(IWebDriver driver)
        {
            int best = 0;
            string[] selectors =
            {
                "div.message-out",
                "div[class*='message-out']",
                "div[data-testid='msg-container'].message-out"
            };

            foreach (string selector in selectors)
            {
                try
                {
                    int count = driver.FindElements(By.CssSelector(selector)).Count;
                    if (count > best) best = count;
                }
                catch { }
            }
            return best;
        }

        /// <summary>
        /// Counts sent bubbles that show this file name. The search is limited to
        /// outgoing messages so the attachment preview (which also shows the file
        /// name) is not mistaken for a delivered message.
        /// </summary>
        private static int DocumentMessageCount(IWebDriver driver, string fileBase)
        {
            if (string.IsNullOrEmpty(fileBase)) return 0;
            try
            {
                string xpath =
                    "//div[@id='main']//div[contains(@class,'message-out')]" +
                    "//*[contains(text()," + XPathLiteral(fileBase) + ")]";
                return driver.FindElements(By.XPath(xpath)).Count;
            }
            catch { return 0; }
        }

        private static string XPathLiteral(string value)
        {
            if (value.IndexOf('\'') < 0) return "'" + value + "'";
            if (value.IndexOf('"') < 0) return "\"" + value + "\"";
            return "concat('" + value.Replace("'", "',\"'\",'") + "')";
        }

        private static IWebElement FindText(IWebDriver driver, string needle)
        {
            try
            {
                string xpath =
                    "//*[contains(translate(normalize-space(.)," +
                    "'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')," +
                    XPathLiteral(needle.ToLowerInvariant()) + ")]";
                IReadOnlyCollection<IWebElement> matches = driver.FindElements(By.XPath(xpath));
                foreach (IWebElement match in matches)
                {
                    try { if (match.Displayed) return match; } catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Captions come from TDL and can contain line breaks. SendKeys treats a
        /// newline as Enter, which would send the message before the caption is
        /// complete, so flatten it first.
        /// </summary>
        private static string Flatten(string text)
        {
            return text.Replace("\r", " ").Replace("\n", " ");
        }

        private static void DismissAlert(IWebDriver driver)
        {
            try { driver.SwitchTo().Alert().Accept(); } catch { }
        }

        private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            DateTime end = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < end)
            {
                try { if (condition()) return true; } catch { }
                Thread.Sleep(500);
            }
            return false;
        }

        // ----- misc ------------------------------------------------------------

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

        private static void Log(StringBuilder sb, string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message;
            sb.AppendLine(line);

            // Flush every line so the file shows how far a run got even if the
            // process is still busy, hung, or killed by Tally.
            try
            {
                File.AppendAllText(
                    Path.Combine(LogDirectory(), "log.txt"),
                    line + Environment.NewLine);
            }
            catch { }
        }

        private static string LogDirectory()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TallyWhatsAppSender");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WriteLog(StringBuilder sb)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(LogDirectory(), "log.txt"),
                    new string('-', 40) + Environment.NewLine);
            }
            catch { }
        }
    }
}
