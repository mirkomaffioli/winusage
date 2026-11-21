using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace WinUsage
{
    /// <summary>Errore restituito da Telegram (o dalla rete). Il messaggio non contiene mai il token.</summary>
    internal sealed class TelegramException : Exception
    {
        public readonly int Code;   // codice HTTP/Telegram; 0 = errore di rete

        public TelegramException(int code, string message) : base(message)
        {
            Code = code;
        }

        /// <summary>Un altro programma sta leggendo con lo stesso token (getUpdates in parallelo o webhook).</summary>
        public bool Conflict { get { return Code == 409; } }
        /// <summary>Token non valido o revocato.</summary>
        public bool Unauthorized { get { return Code == 401 || Code == 404; } }
        /// <summary>Errore definitivo per quella richiesta (inutile riprovare).</summary>
        public bool Permanent { get { return Code == 400 || Code == 403; } }
    }

    /// <summary>Risultato di getUpdates: i messaggi e il prossimo offset da confermare.</summary>
    internal sealed class UpdateBatch
    {
        public List<IncomingMessage> Messages = new List<IncomingMessage>();
        public long NextOffset;
    }

    /// <summary>
    /// Client minimo della Bot API di Telegram, solo HTTPS in uscita (nessuna porta aperta sul PC).
    /// Usa solo il .NET Framework: HttpWebRequest e JavaScriptSerializer.
    /// </summary>
    internal sealed class TelegramApi
    {
        /// <summary>Indirizzo della Bot API (cambiato solo dai test, verso un server finto locale).</summary>
        internal static string BaseUrl = "https://api.telegram.org/bot";
        private readonly string token;
        private volatile HttpWebRequest current;

        /// <summary>Ora del server (header Date) dell'ultima risposta, con l'istante locale in cui è arrivata.</summary>
        public DateTime ServerDateUtc { get; private set; }
        public DateTime ServerDateReceivedUtc { get; private set; }

        static TelegramApi()
        {
            // Un exe compilato senza indicare la versione del framework partirebbe con i protocolli "storici": si lascia
            // scegliere a Windows (TLS 1.2/1.3 su Windows 11). La verifica dei certificati resta quella di sistema.
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)0; }   // SystemDefault (.NET 4.7+)
            catch (Exception)
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }   // Tls12
                catch (Exception) { }
            }
            // Il limite predefinito è 2 connessioni per server: lettura (long polling) e invio le occuperebbero entrambe e
            // il messaggio di spegnimento, che ha solo 3 secondi, resterebbe in attesa.
            if (ServicePointManager.DefaultConnectionLimit < 8) ServicePointManager.DefaultConnectionLimit = 8;
            ServicePointManager.Expect100Continue = false;
        }

        public TelegramApi(string token)
        {
            this.token = token ?? "";
        }

        /// <summary>Ora del server stimata adesso; se non c'è ancora una risposta, l'orologio del PC.</summary>
        public DateTime ServerNowUtc()
        {
            if (ServerDateUtc == DateTime.MinValue) return DateTime.UtcNow;
            return ServerDateUtc + (DateTime.UtcNow - ServerDateReceivedUtc);
        }

        /// <summary>Interrompe la richiesta in corso (long polling) per chiudere subito.</summary>
        public void Abort()
        {
            HttpWebRequest r = current;
            if (r != null) { try { r.Abort(); } catch (Exception) { } }
        }

        public static string Redact(string text, string token)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            if (!string.IsNullOrEmpty(token)) text = text.Replace(token, "<token>");
            // qualunque altro "/bot123456:..." (un token diverso, ad esempio in un URL di errore)
            int i = text.IndexOf("/bot", StringComparison.Ordinal);
            while (i >= 0)
            {
                int start = i + 4;
                if (start < text.Length && char.IsDigit(text[start]))
                {
                    int end = start;
                    while (end < text.Length && text[end] != '/' && !char.IsWhiteSpace(text[end])) end++;
                    text = text.Substring(0, start) + "<token>" + text.Substring(end);
                }
                i = text.IndexOf("/bot", start, StringComparison.Ordinal);
            }
            return text;
        }

        // ---- metodi della Bot API ----

        public string GetMeUsername()
        {
            Dictionary<string, object> r = AsObject(Call("getMe", null, 15000));
            return Str(r, "username");
        }

        public string GetWebhookUrl()
        {
            return Str(AsObject(Call("getWebhookInfo", null, 15000)), "url");
        }

        public UpdateBatch GetUpdates(long offset, int timeoutSeconds)
        {
            Dictionary<string, string> f = new Dictionary<string, string>();
            if (offset > 0) f["offset"] = offset.ToString(CultureInfo.InvariantCulture);
            f["timeout"] = timeoutSeconds.ToString(CultureInfo.InvariantCulture);
            f["allowed_updates"] = "[\"message\"]";   // niente messaggi modificati, canali, inline, Business
            object result = Call("getUpdates", f, (timeoutSeconds + 15) * 1000);
            return ParseUpdates(result, offset);
        }

        public long SendMessage(long chatId, string text, int timeoutMs)
        {
            Dictionary<string, string> f = new Dictionary<string, string>();
            f["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture);
            f["text"] = text.Length > 4000 ? text.Substring(0, 4000) : text;
            f["disable_web_page_preview"] = "true";
            return Long(AsObject(Call("sendMessage", f, timeoutMs)), "message_id");
        }

        public long SendImage(long chatId, byte[] data, bool asDocument, string caption)
        {
            Dictionary<string, string> f = new Dictionary<string, string>();
            f["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(caption)) f["caption"] = caption;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            object r = asDocument
                ? CallMultipart("sendDocument", f, "document", "screenshot-" + stamp + ".png", "image/png", data, 120000)
                : CallMultipart("sendPhoto", f, "photo", "screenshot-" + stamp + ".jpg", "image/jpeg", data, 120000);
            return Long(AsObject(r), "message_id");
        }

        public void DeleteMessage(long chatId, long messageId)
        {
            Dictionary<string, string> f = new Dictionary<string, string>();
            f["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture);
            f["message_id"] = messageId.ToString(CultureInfo.InvariantCulture);
            Call("deleteMessage", f, 20000);
        }

        // ---- trasporto ----

        private object Call(string method, Dictionary<string, string> fields, int timeoutMs)
        {
            byte[] body = new byte[0];
            if (fields != null)
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in fields)
                {
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(EscapeLong(kv.Value));
                }
                body = Encoding.UTF8.GetBytes(sb.ToString());
            }
            return Send(method, "application/x-www-form-urlencoded", body, timeoutMs);
        }

        /// <summary>Uri.EscapeDataString ha un limite di lunghezza nelle versioni vecchie: si codifica a pezzi.</summary>
        private static string EscapeLong(string s)
        {
            if (s.Length <= 2000) return Uri.EscapeDataString(s);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i += 2000)
            {
                int len = Math.Min(2000, s.Length - i);
                if (char.IsHighSurrogate(s[i + len - 1]) && i + len < s.Length) len++;
                sb.Append(Uri.EscapeDataString(s.Substring(i, len)));
            }
            return sb.ToString();
        }

        private object CallMultipart(string method, Dictionary<string, string> fields, string fileField, string fileName,
                                     string contentType, byte[] data, int timeoutMs)
        {
            string boundary = "----WinUsage" + Guid.NewGuid().ToString("N");
            MemoryStream ms = new MemoryStream();
            foreach (KeyValuePair<string, string> kv in fields)
            {
                WriteUtf8(ms, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + kv.Key + "\"\r\n\r\n" + kv.Value + "\r\n");
            }
            WriteUtf8(ms, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + fileField + "\"; filename=\"" + fileName +
                          "\"\r\nContent-Type: " + contentType + "\r\n\r\n");
            ms.Write(data, 0, data.Length);
            WriteUtf8(ms, "\r\n--" + boundary + "--\r\n");
            return Send(method, "multipart/form-data; boundary=" + boundary, ms.ToArray(), timeoutMs);
        }

        private static void WriteUtf8(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        private object Send(string method, string contentType, byte[] body, int timeoutMs)
        {
            if (token.Length == 0) throw new TelegramException(401, "Token del bot mancante.");
            HttpWebRequest req;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(BaseUrl + token + "/" + method);
            }
            catch (Exception)
            {
                throw new TelegramException(401, "Token del bot non valido.");
            }
            req.Method = "POST";
            req.ContentType = contentType;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "WinUsage";
            req.KeepAlive = true;

            string text;
            int status;
            current = req;
            try
            {
                req.ContentLength = body.Length;
                using (Stream rs = req.GetRequestStream()) rs.Write(body, 0, body.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    NoteDate(resp);
                    text = ReadAll(resp);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new TelegramException(0, "Rete: " + Redact(ex.Status.ToString(), token));
                using (resp)
                {
                    status = (int)resp.StatusCode;
                    NoteDate(resp);
                    try { text = ReadAll(resp); } catch (Exception) { text = ""; }
                }
            }
            catch (IOException ex)
            {
                throw new TelegramException(0, "Rete: " + Redact(ex.Message, token));
            }
            finally
            {
                current = null;
            }

            Dictionary<string, object> json = null;
            try { json = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>; }
            catch (Exception) { }
            if (json == null) throw new TelegramException(status == 200 ? 502 : status, "Risposta non valida da Telegram (HTTP " + status + ").");

            object ok;
            if (json.TryGetValue("ok", out ok) && ok is bool && (bool)ok)
            {
                object result;
                json.TryGetValue("result", out result);
                return result;
            }
            int code = (int)Long(json, "error_code");
            if (code == 0) code = status;
            throw new TelegramException(code, Redact(Str(json, "description"), token));
        }

        private void NoteDate(HttpWebResponse resp)
        {
            DateTime d;
            string h = resp.Headers["Date"];
            if (h != null && DateTime.TryParseExact(h, "r", CultureInfo.InvariantCulture,
                                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
            {
                ServerDateUtc = d;
                ServerDateReceivedUtc = DateTime.UtcNow;
            }
        }

        private static string ReadAll(HttpWebResponse resp)
        {
            using (Stream s = resp.GetResponseStream())
            using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        // ---- lettura del JSON ----

        /// <summary>Converte il risultato di getUpdates (array JSON già decodificato o testo) in messaggi.</summary>
        public static UpdateBatch ParseUpdates(object result, long currentOffset)
        {
            if (result is string) result = new JavaScriptSerializer().DeserializeObject((string)result);
            UpdateBatch batch = new UpdateBatch();
            batch.NextOffset = currentOffset;
            IEnumerable list = result as IEnumerable;
            if (list == null || result is string) return batch;
            foreach (object o in list)
            {
                Dictionary<string, object> u = o as Dictionary<string, object>;
                if (u == null) continue;
                long updateId = Long(u, "update_id");
                if (updateId + 1 > batch.NextOffset) batch.NextOffset = updateId + 1;   // anche gli update ignorati vanno confermati
                Dictionary<string, object> msg = Obj(u, "message");
                if (msg == null) continue;

                IncomingMessage m = new IncomingMessage();
                m.UpdateId = updateId;
                m.MessageId = Long(msg, "message_id");
                m.DateUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Long(msg, "date"));
                Dictionary<string, object> chat = Obj(msg, "chat");
                if (chat != null)
                {
                    m.ChatId = Long(chat, "id");
                    m.ChatType = Str(chat, "type");
                }
                Dictionary<string, object> from = Obj(msg, "from");
                if (from != null)
                {
                    m.FromId = Long(from, "id");
                    object isBot;
                    m.FromIsBot = from.TryGetValue("is_bot", out isBot) && isBot is bool && (bool)isBot;
                    string first = Str(from, "first_name"), user = Str(from, "username");
                    m.FromName = first.Length > 0 ? (user.Length > 0 ? first + " (@" + user + ")" : first)
                                                  : (user.Length > 0 ? "@" + user : "");
                }
                object text;
                if (msg.TryGetValue("text", out text) && text is string) m.Text = (string)text;
                m.Forwarded = msg.ContainsKey("forward_origin") || msg.ContainsKey("forward_from") ||
                              msg.ContainsKey("forward_from_chat") || msg.ContainsKey("forward_date") ||
                              msg.ContainsKey("forward_sender_name");
                m.ViaBot = msg.ContainsKey("via_bot");
                batch.Messages.Add(m);
            }
            return batch;
        }

        private static Dictionary<string, object> AsObject(object o)
        {
            return o as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) ? o as Dictionary<string, object> : null;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : "";
        }

        private static long Long(Dictionary<string, object> d, string key)
        {
            object o;
            if (!d.TryGetValue(key, out o) || o == null) return 0;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
        }
    }
}
