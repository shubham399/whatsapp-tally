using System;
using System.Collections.Generic;
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
            "//div[@id='main']//div[@contenteditable='true']"
        };

        private static readonly string[] SendButtonXPaths =
        {
            "//span[@data-icon='send']",
            "//button[@aria-label='Send']",
            "//div[@role='button'][@aria-label='Send']",
            "//div[@aria-label='Send']"
        };

        private static readonly string[] AttachButtonXPaths =
        {
            "//span[@data-icon='plus']",
            "//span[@data-icon='clip']",
            "//button[@title='Attach']",
            "//div[@title='Attach']"
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
            string result;
            try
            {
                result = SendCore(contacts, pdfPath, caption, chromeBinary);
            }
            catch (Exception ex)
            {
                result = "Error: " + ex.GetType().Name + ": " + ex.Message;
            }

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
                Log(log, "basedir  = " + AppDomain.CurrentDomain.BaseDirectory);

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

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string driverExe = Path.Combine(baseDir, "chromedriver.exe");

                ChromeOptions options = BuildChromeOptions(chromeBinary);
                Log(log, "profile   = " + ProfileDirectory());
                Log(log, "driver    = " + (File.Exists(driverExe) ? driverExe : "none, using Selenium Manager"));
                Log(log, "starting Chrome ...");

                driver = CreateDriver(baseDir, driverExe, options);
                Log(log, "Chrome started, browser version = " + SafeBrowserVersion(driver));

                var js = (IJavaScriptExecutor)driver;

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
                        return "Error: WhatsApp Web did not open a chat for " + number + ". " +
                               "Check that the number is registered on WhatsApp.";
                    }

                    IWebElement fileInput = FindFileInput(driver);
                    if (fileInput == null)
                    {
                        ClickAttach(driver);
                        WaitUntil(() => FindFileInput(driver) != null, TimeSpan.FromSeconds(5));
                        fileInput = FindFileInput(driver);
                    }
                    if (fileInput == null)
                        return "Error: could not find the WhatsApp Web attachment control for " + number + ".";

                    fileInput.SendKeys(pdfPath);

                    if (!WaitUntil(() => FindVisible(driver, SendButtonXPaths) != null,
                                   TimeSpan.FromSeconds(ElementTimeoutSeconds)))
                    {
                        return "Error: the document preview did not appear for " + number + ".";
                    }

                    if (!string.IsNullOrEmpty(caption))
                    {
                        IWebElement box = FindVisible(driver, ComposeBoxXPaths);
                        if (box != null)
                        {
                            try
                            {
                                box.Click();
                                box.SendKeys(caption);
                            }
                            catch (Exception ex) { Log(log, "caption failed: " + ex.Message); }
                        }
                    }

                    IWebElement send = FindVisible(driver, SendButtonXPaths);
                    if (send == null)
                        return "Error: could not find the Send button for " + number + ".";

                    js.ExecuteScript("arguments[0].click();", send);
                    Log(log, "clicked send for " + number);

                    // The preview panel closes only once WhatsApp accepts the
                    // attachment. If the click did not register, the preview
                    // stays open and the message would silently never be sent -
                    // which is exactly the "no error, but nothing arrived" case.
                    if (!WaitUntil(() => FindVisible(driver, SendButtonXPaths) == null,
                                   TimeSpan.FromSeconds(ElementTimeoutSeconds)))
                    {
                        return "Error: the document preview for " + number + " stayed open, " +
                               "so the Send click did not go through. Check the Chrome window.";
                    }

                    Log(log, "preview closed - message accepted for " + number);
                    Thread.Sleep(5000); // let the upload finish before the next contact
                }

                return "Success: invoice sent on WhatsApp to " + string.Join(", ", numbers.ToArray());
            }
            catch (Exception ex)
            {
                Log(log, "ERROR " + ex.GetType().Name + ": " + ex.Message);
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

        private static IWebDriver CreateDriver(string baseDir, string driverExe, ChromeOptions options)
        {
            if (File.Exists(driverExe))
            {
                try
                {
                    ChromeDriverService service =
                        ChromeDriverService.CreateDefaultService(baseDir, "chromedriver.exe");
                    service.HideCommandPromptWindow = true;
                    return new ChromeDriver(service, options, TimeSpan.FromSeconds(LoginTimeoutSeconds));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Chrome did not start with the bundled driver (" + driverExe + "): " + ex.Message +
                        " Delete that file so the matching driver can be downloaded automatically.", ex);
                }
            }

            string manager = Path.Combine(baseDir, "selenium-manager", "windows", "selenium-manager.exe");
            try
            {
                // No local chromedriver.exe: let Selenium Manager (Selenium 4)
                // find or download a matching driver.
                return new ChromeDriver(options);
            }
            catch (Exception ex)
            {
                string hint = File.Exists(manager)
                    ? ""
                    : " The Selenium driver manager is missing too (expected at " + manager + ").";
                throw new InvalidOperationException(
                    "Chrome did not start. No chromedriver.exe sits next to the DLL, so Selenium Manager " +
                    "has to download a driver that matches Google Chrome." + hint +
                    " Check that Chrome is installed and that this machine has internet access for the " +
                    "one-time download. Original error: " + ex.Message, ex);
            }
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

                IWebElement fallback = null;
                foreach (IWebElement input in inputs)
                {
                    fallback = input;
                    string accept = "";
                    try { accept = (input.GetAttribute("accept") ?? "").ToLowerInvariant(); } catch { }
                    // The document input is the one not limited to images/videos.
                    if (!accept.Contains("image") && !accept.Contains("video"))
                        return input;
                }
                return fallback;
            }
            catch { return null; }
        }

        private static void ClickAttach(IWebDriver driver)
        {
            IWebElement button = FindVisible(driver, AttachButtonXPaths);
            if (button != null)
            {
                try { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", button); }
                catch { }
            }
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
