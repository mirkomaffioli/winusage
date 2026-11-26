using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace WinUsage.Tests
{
    /// <summary>
    /// Server Bot API finto nello stesso processo dei test (HttpListener, solo 127.0.0.1). Imita quello che conta di
    /// api.telegram.org: long polling di getUpdates, conferma degli update con l'offset, 409 "terminated by other getUpdates
    /// request" al getUpdates in attesa quando ne arriva un altro, header Date. Può far fallire o rallentare gli invii.
    /// </summary>
    internal sealed class FakeTelegram : IDisposable
    {
        internal sealed class Sent
        {
            public double At;            // secondi dall'avvio del server
            public long ChatId;
            public string Method;
            public string Text;
            public int Port;             // porta del client: connessione usata
        }

        public const long Phone = 111;

        private readonly HttpListener listener = new HttpListener();
        private readonly object gate = new object();
        private readonly List<Dictionary<string, object>> updates = new List<Dictionary<string, object>>();
        private readonly List<Sent> sent = new List<Sent>();
        private readonly DateTime started = DateTime.UtcNow;
        private long nextUpdate = 5000, nextMessage = 1;
        private int pollSeq, pendingPoll, polls, lastPollPort;
        private volatile bool running = true;

        /// <summary>Da mettere in TelegramApi.BaseUrl.</summary>
        public readonly string BaseUrl;
        /// <summary>sendMessage risponde 502 (rete o Telegram giù).</summary>
        public volatile bool FailSends = false;
        /// <summary>sendMessage aspetta tanto prima di rispondere.</summary>
        public volatile int SlowSendMs = 0;

        public FakeTelegram()
        {
            int port = FreePort();
            BaseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/bot";
            listener.Prefixes.Add("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
            listener.Start();
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Name = "Telegram finto";
            t.Start();
        }

        private static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // ---- stato, per i test ----

        public int Polls { get { lock (gate) return polls; } }
        public int LastPollPort { get { lock (gate) return lastPollPort; } }
        public bool PollPending { get { lock (gate) return pendingPoll != 0; } }

        public List<Sent> SentMessages
        {
            get { lock (gate) return new List<Sent>(sent); }
        }

        public int SentCount { get { lock (gate) return sent.Count; } }

        /// <summary>Messaggio dal telefono (chat privata) che il bot riceverà con getUpdates.</summary>
        public long Inject(string text) { return Inject(text, Phone); }

        public long Inject(string text, long fromId)
        {
            lock (gate)
            {
                long id = nextUpdate++;
                Dictionary<string, object> chat = new Dictionary<string, object>();
                chat["id"] = fromId;
                chat["type"] = "private";
                Dictionary<string, object> from = new Dictionary<string, object>();
                from["id"] = fromId;
                from["is_bot"] = false;
                from["first_name"] = "Mirko";
                Dictionary<string, object> msg = new Dictionary<string, object>();
                msg["message_id"] = nextMessage++;
                msg["date"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                msg["chat"] = chat;
                msg["from"] = from;
                msg["text"] = text;
                Dictionary<string, object> u = new Dictionary<string, object>();
                u["update_id"] = id;
                u["message"] = msg;
                updates.Add(u);
                Monitor.PulseAll(gate);
                return id;
            }
        }

        /// <summary>Aspetta che una condizione diventi vera (true) o che scada il tempo (false).</summary>
        public static bool WaitFor(Func<bool> condition, int ms)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(ms);
            while (true)
            {
                if (condition()) return true;
                if (DateTime.UtcNow >= until) return false;
                Thread.Sleep(25);
            }
        }

        // ---- HTTP ----

        private void AcceptLoop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception) { return; }
                HttpListenerContext c = ctx;
                Thread h = new Thread(delegate() { Handle(c); });
                h.IsBackground = true;
                h.Start();
            }
        }

        private double Now() { return Math.Round((DateTime.UtcNow - started).TotalSeconds, 2); }

        private static Dictionary<string, string> Form(string body)
        {
            Dictionary<string, string> f = new Dictionary<string, string>();
            foreach (string part in body.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                string v = eq < 0 ? "" : part.Substring(eq + 1);
                f[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
            }
            return f;
        }

        private static long Num(Dictionary<string, string> f, string key)
        {
            string v;
            long n;
            return f.TryGetValue(key, out v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        private bool Reply(HttpListenerContext ctx, int status, object obj)
        {
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(obj));
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                try { ctx.Response.Headers[HttpResponseHeader.Date] = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture); }
                catch (Exception) { /* alcuni HttpListener lo mettono da soli */ }
                ctx.Response.ContentLength64 = b.Length;
                ctx.Response.OutputStream.Write(b, 0, b.Length);
                ctx.Response.OutputStream.Close();
                return true;
            }
            catch (Exception) { return false; }   // il client ha chiuso la connessione (lettura interrotta)
        }

        private static Dictionary<string, object> Ok(object result)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["result"] = result;
            return d;
        }

        private static Dictionary<string, object> Error(int code, string description)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = false;
            d["error_code"] = code;
            d["description"] = description;
            return d;
        }

        private void Handle(HttpListenerContext ctx)
        {
            string body;
            try
            {
                using (StreamReader r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            }
            catch (Exception) { return; }
            string path = ctx.Request.Url.AbsolutePath;
            string method = path.Substring(path.LastIndexOf('/') + 1);
            int port = ctx.Request.RemoteEndPoint != null ? ctx.Request.RemoteEndPoint.Port : 0;
            bool multipart = (ctx.Request.ContentType ?? "").StartsWith("multipart/", StringComparison.Ordinal);
            Dictionary<string, string> f = multipart ? new Dictionary<string, string>() : Form(body);

            if (method == "getMe")
            {
                Dictionary<string, object> me = new Dictionary<string, object>();
                me["id"] = 1;
                me["is_bot"] = true;
                me["username"] = "finto_bot";
                Reply(ctx, 200, Ok(me));
            }
            else if (method == "getWebhookInfo")
            {
                Dictionary<string, object> wh = new Dictionary<string, object>();
                wh["url"] = "";
                Reply(ctx, 200, Ok(wh));
            }
            else if (method == "getUpdates") GetUpdates(ctx, f, port);
            else if (method == "sendMessage" || method == "sendPhoto" || method == "sendDocument" || method == "deleteMessage")
            {
                int slow = SlowSendMs;
                if (slow > 0) Thread.Sleep(slow);
                if (FailSends && method != "deleteMessage")
                {
                    Reply(ctx, 502, Error(502, "Bad Gateway"));
                    return;
                }
                long id;
                lock (gate)
                {
                    id = nextMessage++;
                    if (method != "deleteMessage")
                    {
                        Sent s = new Sent();
                        s.At = Now();
                        s.ChatId = Num(f, "chat_id");
                        s.Method = method;
                        string text;
                        s.Text = multipart ? body : f.TryGetValue("text", out text) ? text : "";   // immagini: tutto il corpo
                        s.Port = port;
                        sent.Add(s);
                    }
                }
                if (method == "deleteMessage") Reply(ctx, 200, Ok(true));
                else
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["message_id"] = id;
                    Reply(ctx, 200, Ok(m));
                }
            }
            else Reply(ctx, 404, Error(404, "Not Found"));
        }

        private void GetUpdates(HttpListenerContext ctx, Dictionary<string, string> f, int port)
        {
            long offset = Num(f, "offset");
            int timeout = (int)Num(f, "timeout");
            List<Dictionary<string, object>> result = null;
            lock (gate)
            {
                polls++;
                lastPollPort = port;
                if (offset > 0) updates.RemoveAll(delegate(Dictionary<string, object> u) { return (long)u["update_id"] < offset; });
                int me = ++pollSeq;
                pendingPoll = me;             // come Telegram: un getUpdates ancora in attesa riceve 409
                Monitor.PulseAll(gate);
                DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
                while (true)
                {
                    if (pendingPoll != me) break;
                    List<Dictionary<string, object>> avail =
                        updates.FindAll(delegate(Dictionary<string, object> u) { return (long)u["update_id"] >= offset; });
                    if (avail.Count > 0 || !running) { result = avail; break; }
                    TimeSpan left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) { result = avail; break; }
                    Monitor.Wait(gate, left);
                }
                if (pendingPoll == me) pendingPoll = 0;
            }
            if (result == null)
            {
                Reply(ctx, 409, Error(409, "Conflict: terminated by other getUpdates request; make sure that only one bot instance is running"));
                return;
            }
            Reply(ctx, 200, Ok(result));
        }

        public void Dispose()
        {
            running = false;
            lock (gate) Monitor.PulseAll(gate);
            try { listener.Stop(); listener.Close(); } catch (Exception) { }
        }
    }
}
