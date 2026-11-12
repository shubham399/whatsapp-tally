using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace TallyWhatsappSender
{
    /// <summary>
    /// The log is written line by line so the file shows how far a run got even
    /// if Chrome hangs or Tally is closed while a send is in progress.
    /// </summary>
    internal static class LogFile
    {
        public static void Line(StringBuilder log, string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message;
            if (log != null) log.AppendLine(line);

            try
            {
                File.AppendAllText(
                    Path.Combine(Location(), "log.txt"),
                    line + Environment.NewLine);
            }
            catch { }
        }

        public static void Separator()
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Location(), "log.txt"),
                    new string('-', 40) + Environment.NewLine);
            }
            catch { }
        }

        public static string Location()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TallyWhatsAppSender");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// The WhatsApp Web flow, expressed with page scripts and DevTools mouse /
    /// file commands instead of Selenium. All the selectors live here so a
    /// WhatsApp redesign is a one-file change.
    /// </summary>
    internal sealed class WhatsAppAutomation
    {
        // WhatsApp Web's markup changes from time to time. Every selector is
        // collected here so it can be updated without touching the flow.

        private static readonly string[] ComposeBoxXPaths =
        {
            "//footer//div[@contenteditable='true']",
            "//footer//div[@role='textbox']",
            "//div[@id='main']//footer//div[@contenteditable='true']"
        };

        private static readonly string[] CaptionBoxXPaths =
        {
            // Current WhatsApp Web: the media editor's caption field.
            "//div[@data-testid='media-caption-input-container']",
            "//div[@role='textbox'][@aria-label='Type a message'][@contenteditable='true']",

            // Older layouts.
            "//div[@aria-label='Add a caption']",
            "//div[contains(@aria-label,'aption')]",
            "//div[@id='main']//div[@contenteditable='true'][not(ancestor::footer)]",
            "//div[@id='main']//div[@role='textbox'][@contenteditable='true'][not(ancestor::footer)]"
        };

        private static readonly string[] SendButtonXPaths =
        {
            // Current WhatsApp Web: a role=button whose label is "Send",
            // "Send 1 selected", "Send 2 selected", ...
            "//div[@role='button'][starts-with(@aria-label,'Send ')]",
            "//div[@role='button'][@aria-label='Send']",
            "//*[@role='button'][contains(@aria-label,'Send')]",
            "//button[@aria-label='Send']",

            // Icon-based layouts.
            "//span[@data-icon='send']",
            "//span[@data-icon='wds-ic-send-filled']",
            "//span[@data-icon='send-filled']",
            "//span[@data-icon='wds-ic-send']",
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

        private const int ElementTimeoutSeconds = 60;

        /// <summary>
        /// Shared page-side helpers. They are prepended to every expression
        /// because a navigation would wipe anything stored on the page.
        /// </summary>
        private const string JsPrelude =
            "function waVis(e){if(!e)return false;try{var r=e.getBoundingClientRect();" +
            "return r.width>0&&r.height>0;}catch(x){return false;}}" +
            "function waFind(xs){for(var i=0;i<xs.length;i++){" +
            "var r=document.evaluate(xs[i],document,null,6,null);" +
            "for(var j=r.snapshotLength-1;j>=0;j--){var e=r.snapshotItem(j);" +
            "if(waVis(e))return e;}}return null;}";

        private readonly ChromeSession _session;
        private readonly CdpClient _cdp;
        private readonly StringBuilder _log;

        public WhatsAppAutomation(ChromeSession session, StringBuilder log)
        {
            _session = session;
            _cdp = session.Client;
            _log = log;
        }

        // ----- public flow -----------------------------------------------------

        public bool EnsureLoggedIn(int loginTimeoutSeconds)
        {
            Log("opening " + BaseUrl());
            _session.Navigate(BaseUrl());

            DateTime deadline = DateTime.UtcNow.AddSeconds(loginTimeoutSeconds);
            bool qrSeen = false;

            while (DateTime.UtcNow < deadline)
            {
                if (Exists(LoggedInXPaths))
                {
                    Log("WhatsApp Web is logged in");
                    return true;
                }

                if (!qrSeen && Exists(QrCodeXPaths))
                {
                    qrSeen = true;
                    Log("QR code displayed - waiting for the phone to link this device");
                }

                Thread.Sleep(1000);
            }

            Log(qrSeen
                ? "still on the QR screen after " + loginTimeoutSeconds + "s; the code was not scanned"
                : "chat list did not appear after " + loginTimeoutSeconds + "s");
            return false;
        }

        /// <summary>
        /// Opens the chat for <paramref name="number"/>, attaches the PDF, types
        /// the caption and verifies that a new document bubble appeared.
        /// Returns null on success, or an error message for the user.
        /// </summary>
        public string SendOne(string number, string pdfPath, string caption)
        {
            string fileName = Path.GetFileName(pdfPath);
            string fileBase = Path.GetFileNameWithoutExtension(pdfPath);

            Log("opening " + ChatUrl(number));
            _session.Navigate(ChatUrl(number));

            if (!WaitVisible(ComposeBoxXPaths, ElementTimeoutSeconds))
            {
                if (HasVisibleText("invalid"))
                {
                    Log("WhatsApp Web reports the number as invalid");
                    return "Error: WhatsApp Web says " + number + " is not a valid WhatsApp " +
                           "number. Check the Mobile No. in the party ledger.";
                }

                return "Error: WhatsApp Web did not open a chat for " + number + ". " +
                       "Check that the number is registered on WhatsApp.";
            }

            // Let any pending history render before counting, so a late load
            // cannot look like a newly sent message.
            Thread.Sleep(2000);

            int messagesBefore = CountOutgoing();
            int documentsBefore = CountDocuments(fileBase);
            int thumbsBefore = CountDocumentThumbs(fileName);
            Log("chat ready: " + messagesBefore + " outgoing message(s), " +
                documentsBefore + " text matches, " + thumbsBefore + " document bubble(s)");

            string attachError = Attach(pdfPath);
            if (attachError != null) return attachError;

            Log("handed " + fileName + " to the page - waiting for the preview");

            if (!WaitVisible(CaptionBoxXPaths, ElementTimeoutSeconds))
            {
                return "Error: the document preview did not appear for " + number + ". " +
                       "The file was handed to WhatsApp Web but it did not show the preview.";
            }

            if (!string.IsNullOrEmpty(caption)) TypeCaption(caption);

            if (!ClickAny(SendButtonXPaths, "send button"))
            {
                return "Error: could not find the Send button for " + number + ".";
            }

            // The document bubble only appears once WhatsApp accepts the
            // attachment. Counting it (rather than trusting the click) is what
            // turns "no error, but nothing arrived" into a real answer.
            bool delivered = WaitUntil(
                () => Sent(fileName, fileBase, thumbsBefore, documentsBefore, messagesBefore),
                TimeSpan.FromSeconds(ElementTimeoutSeconds));

            if (!delivered)
            {
                Log("no new message appeared - pressing Enter as a fallback");
                PressEnterInCaption();
                delivered = WaitUntil(
                    () => Sent(fileName, fileBase, thumbsBefore, documentsBefore, messagesBefore),
                    TimeSpan.FromSeconds(20));
            }

            if (!delivered)
            {
                return "Error: the Send click for " + number + " did not produce a message in the chat. " +
                       "Check the Chrome window; the log records which step found what.";
            }

            Log("message appears in the chat for " + number);
            Thread.Sleep(5000); // let the upload finish before the next contact
            return null;
        }

        // ----- attaching -------------------------------------------------------

        /// <summary>
        /// Hands the PDF to WhatsApp Web. The hidden file input usually exists
        /// as soon as the attach menu has been opened; the Document entry is
        /// only clicked when no input can be found.
        /// </summary>
        public string Attach(string pdfPath)
        {
            if (!MarkFileInput())
            {
                Log("no file input yet - opening the attach menu");
                if (!ClickAny(AttachButtonXPaths, "attach button"))
                {
                    return "Error: could not find the WhatsApp Web attach button. " +
                           "The page layout may have changed; open WhatsApp Web in Chrome and check the log.";
                }
            }

            WaitUntil(MarkFileInput, TimeSpan.FromSeconds(10));

            if (!MarkFileInput())
            {
                Log("still no file input - clicking the Document entry");
                if (ClickAny(DocumentMenuItemXPaths, "Document menu item"))
                {
                    WaitUntil(MarkFileInput, TimeSpan.FromSeconds(5));
                }
            }

            if (!MarkFileInput())
            {
                return "Error: could not find the WhatsApp Web attachment control. " +
                       "The page layout may have changed; open WhatsApp Web in Chrome and check the log.";
            }

            int nodeId = FileInputNodeId();
            if (nodeId == 0)
            {
                return "Error: the WhatsApp Web file input was found but Chrome could not address it. " +
                       "Please try again.";
            }

            Log("file input found - setting " + Path.GetFileName(pdfPath));
            _cdp.Send(
                "DOM.setFileInputFiles",
                "{\"files\":[" + Json.Quote(pdfPath) + "],\"nodeId\":" +
                    nodeId.ToString(CultureInfo.InvariantCulture) + "}",
                60000);
            return null;
        }

        /// <summary>
        /// Marks the document file input on the page. WhatsApp also has inputs
        /// that only accept photos and videos; handing a PDF to those opens the
        /// editor and then fails with "File not supported", so only an input
        /// that allows documents is ever chosen.
        /// </summary>
        private bool MarkFileInput()
        {
            const string expression =
                "(function(){" +
                "var ins=document.querySelectorAll('input[type=file]');" +
                "var best=null;" +
                "for(var i=0;i<ins.length;i++){" +
                "var a=(ins[i].getAttribute('accept')||'').toLowerCase().trim();" +
                "var documentCapable=(a.indexOf('pdf')>=0||a.indexOf('application')>=0||" +
                "a==='*'||a==='*/*'||a==='');" +
                "var mediaOnly=(a.indexOf('image')>=0||a.indexOf('video')>=0||a.indexOf('audio')>=0);" +
                "if(documentCapable&&!mediaOnly){best=ins[i];break;}}" +
                "if(!best)return false;" +
                "best.setAttribute('data-wa-target','1');return true;})()";

            return _cdp.EvaluateBool(expression, 15000);
        }

        private int FileInputNodeId()
        {
            object document = _cdp.Send("DOM.getDocument", "{\"depth\":0}", 15000);
            int root = Json.Int(Json.Get(document, "root"), "nodeId", 0);
            if (root == 0) return 0;

            object found = _cdp.Send(
                "DOM.querySelector",
                "{\"nodeId\":" + root.ToString(CultureInfo.InvariantCulture) +
                    ",\"selector\":\"input[data-wa-target='1']\"}",
                15000);

            return Json.Int(found, "nodeId", 0);
        }

        // ----- typing and clicking ---------------------------------------------

        private void TypeCaption(string caption)
        {
            string text = Json.Quote(Flatten(caption));

            // Click the field first: the media editor uses a Lexical input, and
            // Chrome's own text insertion only reaches it once it owns focus.
            if (!ClickAny(CaptionBoxXPaths, "caption box"))
            {
                Log("caption box disappeared before typing");
                return;
            }

            try
            {
                _cdp.Send("Input.insertText", "{\"text\":" + text + "}", 15000);
            }
            catch (Exception ex)
            {
                Log("Chrome text input failed: " + ex.Message);
            }

            Thread.Sleep(700);

            string typed = _cdp.EvaluateString(CaptionTextExpression(), 15000) ?? "";
            if (typed.Trim().Length == 0)
            {
                // Some versions only react to an editing command.
                Log("typing did not stick - trying the page's own insert command");
                string expression =
                    "(function(){" + JsPrelude +
                    "var e=waFind(" + JsArray(CaptionBoxXPaths) + ");" +
                    "if(!e)return false;" +
                    "e.focus();" +
                    "try{document.execCommand('insertText',false," + text + ");}catch(err){return false;}" +
                    "return true;})()";

                _cdp.EvaluateBool(expression, 15000);
                Thread.Sleep(500);
                typed = _cdp.EvaluateString(CaptionTextExpression(), 15000) ?? "";
            }

            if (typed.Trim().Length == 0)
            {
                Log("caption could not be typed - sending the document without it");
            }
            else
            {
                Log("typed the caption");
            }
        }

        private string CaptionTextExpression()
        {
            return "(function(){" + JsPrelude +
                   "var e=waFind(" + JsArray(CaptionBoxXPaths) + ");" +
                   "return e?((e.innerText||e.textContent||'')):'';})()";
        }

        private void PressEnterInCaption()
        {
            if (!ClickAny(CaptionBoxXPaths, "caption box")) return;

            const string down =
                "{\"type\":\"keyDown\",\"windowsVirtualKeyCode\":13," +
                "\"nativeVirtualKeyCode\":13,\"key\":\"Enter\",\"code\":\"Enter\"}";
            const string up =
                "{\"type\":\"keyUp\",\"windowsVirtualKeyCode\":13," +
                "\"nativeVirtualKeyCode\":13,\"key\":\"Enter\",\"code\":\"Enter\"}";

            try
            {
                _cdp.Send("Input.dispatchKeyEvent", down, 10000);
                _cdp.Send("Input.dispatchKeyEvent", up, 10000);
            }
            catch (Exception ex)
            {
                Log("Enter fallback failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Clicks the centre of the first visible element matching one of the
        /// XPaths with a real (synthesised) mouse event. Page-level .click()
        /// misses handlers that expect pointer events.
        /// </summary>
        private bool ClickAny(string[] xpaths, string description)
        {
            string centre = _cdp.EvaluateString(
                "(function(){" + JsPrelude +
                "var e=waFind(" + JsArray(xpaths) + ");" +
                "if(!e)return '';" +
                "var r=e.getBoundingClientRect();" +
                "return Math.round(r.left+r.width/2)+','+Math.round(r.top+r.height/2);})()",
                15000);

            if (string.IsNullOrEmpty(centre)) return false;

            int comma = centre.IndexOf(',');
            if (comma <= 0) return false;

            string x = centre.Substring(0, comma);
            string y = centre.Substring(comma + 1);

            string at = "{\"x\":" + x + ",\"y\":" + y;
            _cdp.Send("Input.dispatchMouseEvent", "{\"type\":\"mouseMoved\"," + at.Substring(1) + "}", 10000);
            _cdp.Send(
                "Input.dispatchMouseEvent",
                "{\"type\":\"mousePressed\"," + at.Substring(1) +
                    ",\"button\":\"left\",\"buttons\":1,\"clickCount\":1}",
                10000);
            _cdp.Send(
                "Input.dispatchMouseEvent",
                "{\"type\":\"mouseReleased\"," + at.Substring(1) +
                    ",\"button\":\"left\",\"buttons\":0,\"clickCount\":1}",
                10000);

            try
            {
                string hit = _cdp.EvaluateString(
                    "(function(){var e=document.elementFromPoint(" + x + "," + y + ");" +
                    "if(!e)return 'nothing';" +
                    "return e.tagName+(e.getAttribute('aria-label')?'['+e.getAttribute('aria-label')+']':'');})()",
                    10000);
                Log("  element at " + centre + ": " + hit);
            }
            catch { }

            Log("clicked " + description + " at " + centre);
            return true;
        }

        // ----- reading the page ------------------------------------------------

        private bool Sent(string fileName, string fileBase, int thumbsBefore,
                          int documentsBefore, int messagesBefore)
        {
            // The document bubble carries title="Download "invoice.pdf"" on its
            // thumbnail in current WhatsApp Web builds - the most precise sign
            // that this attachment was accepted.
            if (CountDocumentThumbs(fileName) > thumbsBefore) return true;

            if (CountDocuments(fileBase) > documentsBefore) return true;

            // Older layouts do not mark the document bubble; accept a new
            // outgoing message once the attachment preview has gone.
            return !Exists(CaptionBoxXPaths) && CountOutgoing() > messagesBefore;
        }

        private int CountDocumentThumbs(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return 0;

            string expression =
                "(function(){var n=0;" +
                "var els=document.querySelectorAll(\"div[data-testid='document-thumb']\");" +
                "for(var i=0;i<els.length;i++){" +
                "var t=els[i].getAttribute('title')||'';" +
                "if(t.indexOf(" + Json.Quote(fileName) + ")>=0)n++;}return n;})()";

            return _cdp.EvaluateInt(expression, 15000);
        }

        private int CountOutgoing()
        {
            const string expression =
                "(function(){var b=0;" +
                "var s=[\"div.message-out\",\"div[class*='message-out']\"," +
                "\"div[data-testid='msg-container'].message-out\"];" +
                "for(var i=0;i<s.length;i++){var n=document.querySelectorAll(s[i]).length;" +
                "if(n>b)b=n;}return b;})()";

            return _cdp.EvaluateInt(expression, 15000);
        }

        /// <summary>
        /// Counts sent bubbles that show this file name. The search is limited
        /// to outgoing messages so the attachment preview (which also shows the
        /// file name) is not mistaken for a delivered message.
        /// </summary>
        private int CountDocuments(string fileBase)
        {
            if (string.IsNullOrEmpty(fileBase)) return 0;

            string xpath =
                "//div[@id='main']//div[contains(@class,'message-out')]" +
                "//*[contains(text()," + XPathLiteral(fileBase) + ")]";

            return _cdp.EvaluateInt(
                "(function(){var r=document.evaluate(" + Json.Quote(xpath) +
                ",document,null,6,null);return r.snapshotLength;})()",
                15000);
        }

        private bool HasVisibleText(string needle)
        {
            string xpath =
                "//*[contains(translate(normalize-space(.)," +
                "'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')," +
                XPathLiteral(needle.ToLowerInvariant()) + ")]";

            string expression =
                "(function(){" + JsPrelude +
                "var r=document.evaluate(" + Json.Quote(xpath) + ",document,null,6,null);" +
                "for(var i=r.snapshotLength-1;i>=0;i--){var e=r.snapshotItem(i);" +
                "if(waVis(e))return true;}return false;})()";

            return _cdp.EvaluateBool(expression, 15000);
        }

        private bool Exists(string[] xpaths)
        {
            return _cdp.EvaluateBool(
                "(function(){" + JsPrelude +
                "return waFind(" + JsArray(xpaths) + ")!==null;})()",
                15000);
        }

        // ----- helpers ---------------------------------------------------------

        private string ChatUrl(string number)
        {
            // Test hook: the integration test points every chat at a local page
            // that mimics WhatsApp's markup.
            string test = Environment.GetEnvironmentVariable("TALLY_WA_TEST_URL");
            if (!string.IsNullOrEmpty(test)) return test;

            return "https://web.whatsapp.com/send?phone=" + number;
        }

        private static string BaseUrl()
        {
            string test = Environment.GetEnvironmentVariable("TALLY_WA_TEST_URL");
            return string.IsNullOrEmpty(test) ? "https://web.whatsapp.com" : test;
        }

        private bool WaitVisible(string[] xpaths, int seconds)
        {
            return WaitUntil(() => Exists(xpaths), TimeSpan.FromSeconds(seconds));
        }

        private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                try { if (condition()) return true; }
                catch { }
                Thread.Sleep(500);
            }
            return false;
        }

        private static string JsArray(string[] values)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Json.Quote(values[i]));
            }
            return sb.Append(']').ToString();
        }

        private static string XPathLiteral(string value)
        {
            if (value.IndexOf('\'') < 0) return "'" + value + "'";
            if (value.IndexOf('"') < 0) return "\"" + value + "\"";
            return "concat('" + value.Replace("'", "',\"'\",'") + "')";
        }

        /// <summary>
        /// Captions come from TDL and can contain line breaks. A newline would
        /// send the message before the caption is complete, so flatten it.
        /// </summary>
        private static string Flatten(string text)
        {
            return text.Replace("\r", " ").Replace("\n", " ");
        }

        private void Log(string message)
        {
            LogFile.Line(_log, message);
        }
    }
}
