using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using TallyWhatsappSender;

namespace CdpHarness
{
    /// <summary>
    /// End-to-end test of the DevTools automation.
    ///
    /// Without arguments it runs against test/mock-whatsapp.html in headless
    /// Chromium, which exercises the protocol client, the file upload, the
    /// caption and the delivery check without touching a real chat.
    ///
    /// With --live NUMBER PDF it opens the real WhatsApp Web in a visible
    /// window (scan the QR code), then sends that PDF to that number.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--live") return Live(args);
            if (args.Length > 0 && args[0] == "--inspect") return Inspect(args);
            if (args.Length > 0 && args[0] == "--eval") return Eval(args);

            string chrome = args.Length > 0 ? args[0] : "chromium";
            string page = Path.GetFullPath(args.Length > 1 ? args[1] : "../mock-whatsapp.html");
            return Mock(chrome, page);
        }

        private static int Mock(string chrome, string page)
        {
            const int port = 9444;
            string pdf = Path.Combine(Path.GetTempPath(), "cdp-harness-invoice.pdf");
            File.WriteAllText(pdf, "%PDF-1.4\n% test\n");

            string profile = Path.Combine(Path.GetTempPath(), "cdp-harness-profile");
            Directory.CreateDirectory(profile);

            var start = new ProcessStartInfo(chrome);
            start.ArgumentList.Add("--headless=new");
            start.ArgumentList.Add("--no-sandbox");
            start.ArgumentList.Add("--disable-gpu");
            start.ArgumentList.Add("--remote-debugging-port=" + port);
            start.ArgumentList.Add("--user-data-dir=" + profile);
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("file://" + page);
            start.UseShellExecute = false;

            Console.WriteLine("starting " + chrome);
            Process process = Process.Start(start);

            try
            {
                if (!WaitForPort(port, 20)) return 1;

                // The automation always uses this URL instead of WhatsApp Web.
                Environment.SetEnvironmentVariable("TALLY_WA_TEST_URL", "file://" + page);

                var log = new StringBuilder();

                using (ChromeSession session = ChromeSession.Attach(port, false))
                {
                    var automation = new WhatsAppAutomation(session, log);

                    if (!automation.EnsureLoggedIn(10))
                    {
                        Console.WriteLine("FAIL: the mock page was not recognised as logged in.");
                        Console.WriteLine(log.ToString());
                        return 1;
                    }

                    string error = automation.SendOne("919876543210", pdf, "Hello from the test");
                    if (error != null)
                    {
                        Console.WriteLine("FAIL: " + error);
                        Console.WriteLine(log.ToString());
                        return 1;
                    }

                    int bubbles = session.Client.EvaluateInt(
                        "(function(){return document.querySelectorAll('.message-out').length;})()", 10000);
                    string caption = session.Client.EvaluateString(
                        "(function(){var e=document.querySelector('.message-out .cap');" +
                        "return e?e.textContent:'';})()", 10000);

                    Console.WriteLine("bubbles = " + bubbles + ", caption = '" + caption + "'");

                    if (bubbles != 1)
                    {
                        Console.WriteLine("FAIL: expected one outgoing message bubble.");
                        return 1;
                    }

                    if (caption.IndexOf("Hello from the test", StringComparison.Ordinal) < 0)
                    {
                        Console.WriteLine("FAIL: the caption did not reach the page.");
                        return 1;
                    }

                    Console.WriteLine(log.ToString());
                    Console.WriteLine("PASS: file attached, caption typed and send verified.");
                    return 0;
                }
            }
            finally
            {
                try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            }
        }

        private static int Live(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("usage: CdpHarness --live <number> <pdf> [profile-dir]");
                return 2;
            }

            const int port = 9455;
            string number = args[1];
            string pdf = Path.GetFullPath(args[2]);
            // The same per-user folder the add-on uses (on Windows that is
            // %LOCALAPPDATA%\TallyWhatsAppSender\chrome-profile). It must be a
            // persistent path: the WhatsApp Web login lives inside it, and a
            // temp directory would be wiped on reboot and force a new QR scan.
            string profile = args.Length > 3
                ? Path.GetFullPath(args[3])
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TallyWhatsAppSender", "chrome-profile");

            string chrome = Environment.GetEnvironmentVariable("WA_CHROME");
            if (string.IsNullOrEmpty(chrome)) chrome = "chromium";

            if (!File.Exists(pdf))
            {
                Console.WriteLine("PDF not found: " + pdf);
                return 2;
            }

            Directory.CreateDirectory(profile);

            var start = new ProcessStartInfo(chrome);
            start.ArgumentList.Add("--remote-debugging-port=" + port);
            start.ArgumentList.Add("--user-data-dir=" + profile);
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("--disable-popup-blocking");
            start.ArgumentList.Add("--disable-notifications");
            start.ArgumentList.Add("--start-maximized");
            start.ArgumentList.Add("about:blank");
            start.UseShellExecute = false;

            Console.WriteLine("starting " + chrome + " with profile " + profile);
            if (!ChromeSession.IsListening(port))
            {
                Process.Start(start);
            }

            if (!WaitForPort(port, 30)) return 1;

            var log = new StringBuilder();
            using (ChromeSession session = ChromeSession.Attach(port, false))
            {
                var automation = new WhatsAppAutomation(session, log);

                Console.WriteLine("waiting for login - scan the QR code in the Chrome window");
                if (!automation.EnsureLoggedIn(600))
                {
                    Console.WriteLine("FAIL: not logged in.");
                    Console.WriteLine(log.ToString());
                    return 1;
                }

                Console.WriteLine("logged in, sending " + pdf + " to " + number);
                string error = automation.SendOne(number, pdf, "Tally WhatsApp Sender - live test");
                if (error != null)
                {
                    Console.WriteLine("FAIL: " + error);
                    Console.WriteLine(log.ToString());
                    return 1;
                }

                Console.WriteLine(log.ToString());
                Console.WriteLine("PASS: the invoice PDF was sent on WhatsApp.");
                return 0;
            }
        }

        private static int Inspect(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("usage: CdpHarness --inspect <number> <pdf>");
                return 2;
            }

            const int port = 9455;
            string number = args[1];
            string pdf = Path.GetFullPath(args[2]);
            var log = new StringBuilder();

            using (ChromeSession session = ChromeSession.Attach(port, false))
            {
                var automation = new WhatsAppAutomation(session, log);

                string url = "https://web.whatsapp.com/send?phone=" + number;
                Console.WriteLine("opening " + url);
                session.Navigate(url);
                Thread.Sleep(8000);

                string attach = automation.Attach(pdf);
                Console.WriteLine("attach: " + (attach ?? "ok"));
                Thread.Sleep(6000);

                Console.WriteLine(session.Client.EvaluateString(DumpExpression, 30000));
            }

            return 0;
        }

        private const string DumpExpression = @"
(function(){
  function d(e){
    if(!e) return '';
    var r=e.getBoundingClientRect();
    var cls=(e.className&&typeof e.className==='string')?e.className.trim().split(/\s+/).slice(0,3).join('.'):'';
    var bits=[e.tagName.toLowerCase()+(cls?'.'+cls:'')];
    ['role','aria-label','title','contenteditable','data-icon','data-testid','data-lexical-editor'].forEach(function(a){
      var v=e.getAttribute(a); if(v) bits.push('['+a+'=""'+v+'""]');
    });
    bits.push('@'+Math.round(r.left)+','+Math.round(r.top)+' '+Math.round(r.width)+'x'+Math.round(r.height));
    return bits.join('');
  }
  var out=[];
  out.push('URL '+location.href);
  out.push('--- contenteditable ---');
  document.querySelectorAll('[contenteditable]').forEach(function(e){out.push(d(e));});
  out.push('--- aria-labelled / buttons ---');
  document.querySelectorAll('button,[role=""button""],[aria-label]').forEach(function(e){
    if(e.getAttribute('aria-label')||e.getAttribute('title')||e.querySelector('[data-icon]')) out.push(d(e));
  });
  out.push('--- file inputs ---');
  document.querySelectorAll('input[type=file]').forEach(function(e){
    out.push('accept=' + (e.getAttribute('accept')||'(none)') +
             ' multiple=' + e.multiple + ' @' + Math.round(e.getBoundingClientRect().width) + 'x' +
             Math.round(e.getBoundingClientRect().height));
  });
  out.push('--- visible data-icon ---');
  document.querySelectorAll('[data-icon]').forEach(function(e){
    var r=e.getBoundingClientRect(); if(r.width>0&&r.height>0) out.push(d(e)+' parent='+e.parentElement.tagName);
  });
  out.push('--- html around caption text ---');
  var all=document.querySelectorAll('*');
  for(var i=0;i<all.length;i++){
    var t=(all[i].textContent||'').trim();
    if(t.length<40 && /add a caption/i.test(t)){
      var p=all[i]; for(var k=0;k<6&&p;k++)p=p.parentElement;
      out.push((p?p.outerHTML:'').slice(0,2500)); break;
    }
  }
  return out.join('\n');
})()";

        private static int Eval(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: CdpHarness --eval <javascript>");
                return 2;
            }

            using (ChromeSession session = ChromeSession.Attach(9455, false))
            {
                object value = session.Client.Evaluate(args[1], 30000);
                Console.WriteLine(value == null ? "(null)" : value.ToString());
            }

            return 0;
        }

        private static bool WaitForPort(int port, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!ChromeSession.IsListening(port))
            {
                if (DateTime.UtcNow > deadline)
                {
                    Console.WriteLine("FAIL: Chrome's automation port never opened.");
                    return false;
                }
                Thread.Sleep(250);
            }

            Console.WriteLine("Chrome is listening on port " + port);
            return true;
        }
    }
}
