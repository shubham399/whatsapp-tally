using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace TallyWhatsappSender
{
    /// <summary>
    /// A deliberately small JSON reader/writer. The add-on talks to Chrome over
    /// the DevTools protocol, which speaks JSON, and pulling in a full JSON
    /// library would reintroduce exactly the dependency problem this design
    /// avoids: a TallyPrime add-on is loaded into Tally's AppDomain, where the
    /// host owns the config file and no binding redirect can be added.
    /// </summary>
    internal static class Json
    {
        public static string Quote(string value)
        {
            if (value == null) return "null";

            var sb = new StringBuilder(value.Length + 16);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static object Parse(string text)
        {
            int index = 0;
            object value = ParseValue(text, ref index);
            return value;
        }

        public static object Get(object node, string key)
        {
            var obj = node as Dictionary<string, object>;
            if (obj == null) return null;
            object value;
            return obj.TryGetValue(key, out value) ? value : null;
        }

        public static string Str(object node, string key)
        {
            object value = Get(node, key);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static int Int(object node, string key, int fallback)
        {
            object value = Get(node, key);
            if (value == null) return fallback;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");

            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("Expected a property name.");
                string key = ParseString(s, ref i);

                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':'.");
                i++;

                result[key] = ParseValue(s, ref i);

                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return result; }
                throw new FormatException("Expected ',' or '}'.");
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // '['

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(s, ref i));

                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return result; }
                throw new FormatException("Expected ',' or ']'.");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();

            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= s.Length) break;
                char escape = s[i++];
                switch (escape)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Truncated unicode escape.");
                        sb.Append((char)int.Parse(
                            s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default:
                        throw new FormatException("Unknown escape sequence.");
                }
            }

            throw new FormatException("Unterminated string.");
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("Expected a value.");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length ||
                string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("Expected '" + literal + "'.");
            }
            i += literal.Length;
        }
    }

    /// <summary>
    /// Minimal client for the Chrome DevTools protocol: one WebSocket, commands
    /// correlated by id, page scripts run with Runtime.evaluate.
    /// </summary>
    internal sealed class CdpClient : IDisposable
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly object _sendLock = new object();
        private int _nextId;

        /// <summary>The flattened session id of the tab we drive, if any.</summary>
        public string SessionId;

        public void Connect(string webSocketUrl, int timeoutMs)
        {
            using (var cancel = new CancellationTokenSource(timeoutMs))
            {
                _socket.ConnectAsync(new Uri(webSocketUrl), cancel.Token).GetAwaiter().GetResult();
            }
        }

        /// <summary>Sends a command inside the attached tab.</summary>
        public object Send(string method, string paramsJson, int timeoutMs)
        {
            return SendCore(method, paramsJson, timeoutMs, true);
        }

        /// <summary>Sends a browser-level command (Target.*, Browser.*).</summary>
        public object SendBrowser(string method, string paramsJson, int timeoutMs)
        {
            return SendCore(method, paramsJson, timeoutMs, false);
        }

        /// <summary>Runs JavaScript in the page and returns its value.</summary>
        public object Evaluate(string expression, int timeoutMs)
        {
            string parameters =
                "{\"expression\":" + Json.Quote(expression) +
                ",\"returnByValue\":true,\"awaitPromise\":false}";

            object result = Send("Runtime.evaluate", parameters, timeoutMs);

            object exception = Json.Get(result, "exceptionDetails");
            if (exception != null)
            {
                string text = Json.Str(exception, "text");
                throw new InvalidOperationException("The page reported a script error: " + (text ?? "unknown"));
            }

            object inner = Json.Get(result, "result");
            if (inner == null) return null;

            string type = Json.Str(inner, "type");
            if (type == "undefined") return null;

            return Json.Get(inner, "value");
        }

        public bool EvaluateBool(string expression, int timeoutMs)
        {
            object value = Evaluate(expression, timeoutMs);
            return value is bool && (bool)value;
        }

        public int EvaluateInt(string expression, int timeoutMs)
        {
            object value = Evaluate(expression, timeoutMs);
            if (value == null) return 0;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public string EvaluateString(string expression, int timeoutMs)
        {
            object value = Evaluate(expression, timeoutMs);
            return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private object SendCore(string method, string paramsJson, int timeoutMs, bool withSession)
        {
            int id = Interlocked.Increment(ref _nextId);

            var message = new StringBuilder();
            message.Append("{\"id\":").Append(id);
            message.Append(",\"method\":").Append(Json.Quote(method));
            if (withSession && !string.IsNullOrEmpty(SessionId))
            {
                message.Append(",\"sessionId\":").Append(Json.Quote(SessionId));
            }
            message.Append(",\"params\":")
                   .Append(string.IsNullOrEmpty(paramsJson) ? "{}" : paramsJson)
                   .Append('}');

            lock (_sendLock)
            {
                SendText(message.ToString(), timeoutMs);
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                {
                    throw new TimeoutException(
                        "Chrome did not answer '" + method + "' within " + timeoutMs + " ms.");
                }

                string reply = ReceiveText(remaining);

                object parsed;
                try { parsed = Json.Parse(reply); }
                catch (FormatException) { continue; }

                object replyId = Json.Get(parsed, "id");
                if (replyId == null)
                {
                    HandleEvent(parsed);
                    continue;
                }

                if (Convert.ToInt32(replyId, CultureInfo.InvariantCulture) != id) continue;

                object error = Json.Get(parsed, "error");
                if (error != null)
                {
                    throw new InvalidOperationException(
                        "Chrome rejected '" + method + "': " + Json.Str(error, "message"));
                }

                return Json.Get(parsed, "result");
            }
        }

        /// <summary>
        /// The page may raise a confirm/beforeunload dialog while navigating.
        /// Accept it so the command that is waiting does not stall.
        /// </summary>
        private void HandleEvent(object message)
        {
            string method = Json.Str(message, "method");
            if (method != "Page.javascriptDialogOpening") return;

            try
            {
                int id = Interlocked.Increment(ref _nextId);
                string command =
                    "{\"id\":" + id +
                    ",\"method\":\"Page.handleJavaScriptDialog\"" +
                    (string.IsNullOrEmpty(SessionId)
                        ? ""
                        : ",\"sessionId\":" + Json.Quote(SessionId)) +
                    ",\"params\":{\"accept\":true}}";
                SendText(command, 5000);
            }
            catch
            {
                // If this fails the original command times out and reports it.
            }
        }

        private void SendText(string text, int timeoutMs)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            using (var cancel = new CancellationTokenSource(timeoutMs))
            {
                _socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    cancel.Token).GetAwaiter().GetResult();
            }
        }

        private string ReceiveText(int timeoutMs)
        {
            byte[] buffer = new byte[8192];

            using (var cancel = new CancellationTokenSource(timeoutMs))
            using (var payload = new MemoryStream())
            {
                while (true)
                {
                    WebSocketReceiveResult part = _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancel.Token).GetAwaiter().GetResult();

                    if (part.MessageType == WebSocketMessageType.Close)
                    {
                        throw new InvalidOperationException("Chrome closed the automation connection.");
                    }

                    payload.Write(buffer, 0, part.Count);

                    if (part.EndOfMessage) break;

                    if (payload.Length > 32 * 1024 * 1024)
                    {
                        throw new InvalidOperationException("Chrome sent an unexpectedly large reply.");
                    }
                }

                return Encoding.UTF8.GetString(payload.ToArray());
            }
        }

        public void Dispose()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                           .Wait(2000);
                }
            }
            catch { }

            try { _socket.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// A Chrome instance driven over its own remote-debugging port. No
    /// chromedriver and no Selenium: the protocol is stable and needs no
    /// third-party assembly inside Tally's process.
    /// </summary>
    internal sealed class ChromeSession : IDisposable
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        private readonly bool _closeBrowser;
        private readonly bool _createdTarget;

        public CdpClient Client { get; private set; }
        public string TargetId { get; private set; }

        private ChromeSession(CdpClient client, string targetId, bool closeBrowser, bool createdTarget)
        {
            Client = client;
            TargetId = targetId;
            _closeBrowser = closeBrowser;
            _createdTarget = createdTarget;
        }

        /// <summary>True when Chrome is already listening on the debug port.</summary>
        public static bool IsListening(int port)
        {
            try
            {
                string version = Http.GetStringAsync(VersionUrl(port)).GetAwaiter().GetResult();
                return version != null &&
                       version.IndexOf("webSocketDebuggerUrl", StringComparison.Ordinal) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Connects to the browser on <paramref name="port"/>, reuses the tab it
        /// already has (creating one only when there is none) and attaches to
        /// it. <paramref name="closeBrowser"/> decides whether disposing the
        /// session also closes Chrome, which is only wanted when this add-on
        /// started it.
        /// </summary>
        public static ChromeSession Attach(int port, bool closeBrowser)
        {
            string webSocketUrl;
            try
            {
                string version = Http.GetStringAsync(VersionUrl(port)).GetAwaiter().GetResult();
                webSocketUrl = Json.Str(Json.Parse(version), "webSocketDebuggerUrl");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not read Chrome's automation endpoint on port " + port + ": " + ex.Message, ex);
            }

            if (string.IsNullOrEmpty(webSocketUrl))
            {
                throw new InvalidOperationException(
                    "Chrome is not accepting automation connections on port " + port + ".");
            }

            var client = new CdpClient();
            client.Connect(webSocketUrl, 15000);

            // Reuse Chrome's own tab. Creating a tab here (the earlier
            // behaviour) left the startup tab sitting next to WhatsApp Web, so
            // every send produced one more stray about:blank tab.
            string targetId = FindPageTarget(client);
            bool created = false;

            if (string.IsNullOrEmpty(targetId))
            {
                object createdTarget = client.SendBrowser(
                    "Target.createTarget", "{\"url\":\"about:blank\"}", 30000);
                targetId = Json.Str(createdTarget, "targetId");
                created = true;

                if (string.IsNullOrEmpty(targetId))
                {
                    client.Dispose();
                    throw new InvalidOperationException("Chrome did not open a tab for the add-on.");
                }
            }

            object attached = client.SendBrowser(
                "Target.attachToTarget",
                "{\"targetId\":" + Json.Quote(targetId) + ",\"flatten\":true}",
                15000);
            string sessionId = Json.Str(attached, "sessionId");
            if (string.IsNullOrEmpty(sessionId))
            {
                client.Dispose();
                throw new InvalidOperationException("Chrome did not attach to the tab.");
            }

            client.SessionId = sessionId;

            // Page.enable lets us receive the dialog event handled above;
            // Runtime.enable makes evaluate-based waits immediate. Intercepting
            // the file chooser keeps a native "open file" dialog from covering
            // the page when WhatsApp's Document entry is used.
            try { client.Send("Page.enable", "{}", 15000); } catch { }
            try { client.Send("Runtime.enable", "{}", 15000); } catch { }
            try { client.Send("Page.setInterceptFileChooserDialog", "{\"enabled\":true}", 10000); } catch { }

            return new ChromeSession(client, targetId, closeBrowser, created);
        }

        /// <summary>
        /// The first ordinary page target in the browser. DevTools windows are
        /// also reported as pages but cannot be driven as a web page.
        /// </summary>
        private static string FindPageTarget(CdpClient client)
        {
            object reply = client.SendBrowser("Target.getTargets", "{}", 15000);
            var targets = Json.Get(reply, "targetInfos") as List<object>;
            if (targets == null) return null;

            foreach (object target in targets)
            {
                if (Json.Str(target, "type") != "page") continue;

                string url = Json.Str(target, "url") ?? "";
                if (url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)) continue;

                string id = Json.Str(target, "targetId");
                if (!string.IsNullOrEmpty(id)) return id;
            }

            return null;
        }

        public void Navigate(string url)
        {
            Client.Send("Page.navigate", "{\"url\":" + Json.Quote(url) + "}", 30000);
        }

        private static string VersionUrl(int port)
        {
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/json/version";
        }

        public void Dispose()
        {
            // A reused tab belongs to the browser: leave it where it is, so a
            // Chrome that was already running keeps its WhatsApp tab instead of
            // quitting with its last tab. Only a tab this session created is
            // closed again.
            if (_createdTarget)
            {
                try
                {
                    Client.SendBrowser(
                        "Target.closeTarget",
                        "{\"targetId\":" + Json.Quote(TargetId) + "}",
                        10000);
                }
                catch { }
            }

            if (_closeBrowser)
            {
                try { Client.SendBrowser("Browser.close", "{}", 5000); } catch { }
            }

            try { Client.Dispose(); } catch { }
        }
    }
}
