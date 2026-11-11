using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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

        // How long to wait for WhatsApp Web to open a chat (this covers the
        // one-time QR-code login) and for the document preview to appear.
        private const int LoginTimeoutSeconds = 120;
        private const int ElementTimeoutSeconds = 60;

        /// <summary>
        /// Called from TDL:
        ///   EXEC COM INTERFACE : WhatsAppEngine : contacts : pdfPath : caption : chromeBinary
        /// Returns a status string which Tally shows in a message box.
        /// </summary>
        public string Send(string contacts, string pdfPath, string caption, string chromeBinary)
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

                if (string.IsNullOrWhiteSpace(pdfPath))
                    return "Error: no invoice PDF path was supplied.";

                pdfPath = ResolvePdfPath(pdfPath);

                if (!File.Exists(pdfPath))
                    return "Error: the invoice PDF was not found at " + pdfPath +
                           ". The export may have failed.";

                List<string> numbers = NormalizeNumbers(contacts);
                if (numbers.Count == 0)
                    return "Error: no valid 10-digit mobile number was found for this party. " +
                           "Please enter a 10-digit mobile number in the party ledger.";

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string driverExe = Path.Combine(baseDir, "chromedriver.exe");

                ChromeOptions options = BuildChromeOptions(chromeBinary);
                driver = CreateDriver(baseDir, driverExe, options);
                var js = (IJavaScriptExecutor)driver;

                string archivePath = ArchiveCopy(pdfPath);
                Log(log, "archived = " + archivePath);

                foreach (string number in numbers)
                {
                    Log(log, "sending to " + number);

                    driver.Navigate().GoToUrl("https://web.whatsapp.com/send?phone=" + number);
                    DismissAlert(driver);

                    if (!WaitUntil(() => FindVisible(driver, ComposeBoxXPaths) != null,
                                   TimeSpan.FromSeconds(LoginTimeoutSeconds)))
                    {
                        return "Error: WhatsApp Web did not open a chat for " + number + ". " +
                               "In the Chrome window scan the QR code to log in once, then try again.";
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

                    Thread.Sleep(5000); // let the upload finish before the next contact
                }

                return "Success: invoice sent on WhatsApp to " + string.Join(", ", numbers.ToArray());
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
            finally
            {
                WriteLog(log);
                try { if (driver != null) driver.Quit(); } catch { }
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
            string profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TallyWhatsAppSender", "chrome-profile");
            Directory.CreateDirectory(profileDir);

            options.AddArgument("--user-data-dir=" + profileDir);
            options.AddArgument("--no-first-run");
            options.AddArgument("--no-default-browser-check");
            options.AddArgument("--disable-popup-blocking");
            options.AddArgument("--disable-notifications");
            options.AddArgument("--start-maximized");
            return options;
        }

        private static IWebDriver CreateDriver(string baseDir, string driverExe, ChromeOptions options)
        {
            if (File.Exists(driverExe))
            {
                ChromeDriverService service =
                    ChromeDriverService.CreateDefaultService(baseDir, "chromedriver.exe");
                service.HideCommandPromptWindow = true;
                return new ChromeDriver(service, options, TimeSpan.FromSeconds(LoginTimeoutSeconds));
            }

            // No local chromedriver.exe: let Selenium Manager (Selenium 4) find
            // or download a matching driver (requires internet access).
            return new ChromeDriver(options);
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
            sb.AppendLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message);
        }

        private static void WriteLog(StringBuilder sb)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TallyWhatsAppSender");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "log.txt"),
                    sb.ToString() + new string('-', 40) + Environment.NewLine);
            }
            catch { }
        }
    }
}
