using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>
    /// Google 日曆（OAuth 2.0 桌面應用程式流程：瀏覽器登入 → 本機 127.0.0.1 接收授權碼，PKCE）。
    /// 只要求「唯讀」權限；refresh token 用 Windows DPAPI 加密存在 %AppData%\IslandBar\google.bin，只有這個 Windows 帳號解得開。
    /// </summary>
    public static class GoogleCalendarService
    {
        private const string Scope = "https://www.googleapis.com/auth/calendar.readonly";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar", "google.bin");

        private sealed class Store
        {
            public string ClientId { get; set; } = "";
            public string ClientSecret { get; set; } = "";
            public string RefreshToken { get; set; } = "";
            public string Email { get; set; } = "";
        }

        private static Store _store;
        private static string _accessToken;
        private static DateTime _accessExpires = DateTime.MinValue;

        // ================================================================
        //  狀態
        // ================================================================

        /// <summary>這個版本有內建的 OAuth 用戶端（GitHub Actions 建置時注入）</summary>
        public static bool HasBuiltInClient => !string.IsNullOrEmpty(BuiltInId);

        private static string BuiltInId => BuildSecrets.GoogleClientId.StartsWith("__") ? "" : BuildSecrets.GoogleClientId;
        private static string BuiltInSecret => BuildSecrets.GoogleClientSecret.StartsWith("__") ? "" : BuildSecrets.GoogleClientSecret;

        public static bool HasClient
        {
            get { var s = Load(); return HasBuiltInClient || !string.IsNullOrEmpty(s.ClientId); }
        }

        public static bool Connected => !string.IsNullOrEmpty(Load().RefreshToken);
        public static string Email => Load().Email;

        public static void SaveClient(string id, string secret)
        {
            var s = Load();
            s.ClientId = (id ?? "").Trim();
            s.ClientSecret = (secret ?? "").Trim();
            Save(s);
        }

        public static void Disconnect()
        {
            var s = Load();
            string token = s.RefreshToken;
            s.RefreshToken = "";
            s.Email = "";
            Save(s);
            _accessToken = null;
            if (!string.IsNullOrEmpty(token))
                _ = Http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        }

        private static (string id, string secret) Client()
        {
            var s = Load();
            if (!string.IsNullOrEmpty(s.ClientId)) return (s.ClientId, s.ClientSecret);
            return (BuiltInId, BuiltInSecret);
        }

        // ================================================================
        //  登入
        // ================================================================

        /// <summary>開瀏覽器讓使用者登入並同意；成功回傳帳號 email</summary>
        public static async Task<string> SignInAsync(CancellationToken token)
        {
            var (clientId, clientSecret) = Client();
            if (string.IsNullOrEmpty(clientId)) throw new InvalidOperationException("還沒有設定 Google OAuth 用戶端");

            int port = FreePort();
            string redirect = $"http://127.0.0.1:{port}/";
            string verifier = RandomString(64);
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = RandomString(24);

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();

            string url = "https://accounts.google.com/o/oauth2/v2/auth" +
                         "?client_id=" + Uri.EscapeDataString(clientId) +
                         "&redirect_uri=" + Uri.EscapeDataString(redirect) +
                         "&response_type=code" +
                         "&scope=" + Uri.EscapeDataString(Scope) +
                         "&code_challenge=" + challenge + "&code_challenge_method=S256" +
                         "&access_type=offline&prompt=consent" +
                         "&state=" + state;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            // 等瀏覽器導回來（最多 5 分鐘）
            var ctxTask = listener.GetContextAsync();
            var done = await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(5), token));
            if (done != ctxTask) throw new TimeoutException("等待 Google 登入逾時");
            var ctx = ctxTask.Result;
            var q = ctx.Request.QueryString;
            string code = q["code"], error = q["error"];
            bool ok = string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(code) && q["state"] == state;
            await Respond(ctx, ok
                ? "已連結 Google 日曆，可以關閉這個分頁回到 IslandBar。"
                : "沒有完成授權（" + (error ?? "狀態不符") + "），可以關閉這個分頁再試一次。");
            listener.Stop();
            if (!ok) throw new InvalidOperationException("Google 授權失敗：" + (error ?? "state mismatch"));

            // 授權碼換 token
            var form = new Dictionary<string, string>
            {
                ["code"] = code, ["client_id"] = clientId, ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code", ["code_verifier"] = verifier
            };
            if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
            using var resp = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), token);
            string json = await resp.Content.ReadAsStringAsync(token);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("換取 token 失敗：" + ErrorText(json));
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            if (string.IsNullOrEmpty(refresh)) throw new InvalidOperationException("Google 沒有給 refresh token，請到 Google 帳戶 → 安全性 → 第三方存取 移除後再試");
            _accessToken = r.GetProperty("access_token").GetString();
            _accessExpires = DateTime.UtcNow.AddSeconds(r.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() - 60 : 3000);

            var s = Load();
            s.RefreshToken = refresh;
            s.Email = await PrimaryEmailAsync() ?? "";
            Save(s);
            Log.Write("Google 日曆已連結" + (string.IsNullOrEmpty(s.Email) ? "" : "：" + s.Email));
            return s.Email;
        }

        private static async Task Respond(HttpListenerContext ctx, string message)
        {
            string html = "<!doctype html><meta charset=utf-8><title>IslandBar</title>" +
                          "<body style=\"font-family:'Segoe UI','Microsoft JhengHei',sans-serif;background:#0d0d0f;color:#fff;" +
                          "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
                          "<div style=\"font-size:20px\">" + WebUtility.HtmlEncode(message) + "</div></body>";
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        // ================================================================
        //  讀取行程
        // ================================================================

        private static async Task<string> AccessTokenAsync()
        {
            if (_accessToken != null && DateTime.UtcNow < _accessExpires) return _accessToken;
            var s = Load();
            if (string.IsNullOrEmpty(s.RefreshToken)) throw new InvalidOperationException("Google 日曆尚未連結");
            var (clientId, clientSecret) = Client();
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["refresh_token"] = s.RefreshToken, ["grant_type"] = "refresh_token"
            };
            if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
            using var resp = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                if (json.Contains("invalid_grant"))
                {
                    // 被撤銷或過期（OAuth 同意畫面還在「測試」狀態時，7 天就會過期）
                    s.RefreshToken = "";
                    Save(s);
                    throw new InvalidOperationException("Google 授權已失效，請重新連結 Google 日曆");
                }
                throw new InvalidOperationException("更新 Google token 失敗：" + ErrorText(json));
            }
            using var doc = JsonDocument.Parse(json);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            _accessExpires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
            return _accessToken;
        }

        private static async Task<JsonDocument> GetJsonAsync(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await AccessTokenAsync());
            using var resp = await Http.SendAsync(req);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Google API {(int)resp.StatusCode}：{ErrorText(json)}");
            return JsonDocument.Parse(json);
        }

        private static async Task<string> PrimaryEmailAsync()
        {
            try
            {
                using var doc = await GetJsonAsync("https://www.googleapis.com/calendar/v3/calendars/primary");
                return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            }
            catch (Exception ex) { Log.Error("Google.PrimaryEmail", ex); return null; }
        }

        /// <summary>所有勾選顯示的日曆（含訂閱的節日日曆）在期間內的行程</summary>
        public static async Task<List<CalEvent>> GetEventsAsync(DateTime from, DateTime to)
        {
            var result = new List<CalEvent>();
            using var list = await GetJsonAsync("https://www.googleapis.com/calendar/v3/users/me/calendarList?minAccessRole=reader");
            var cals = new List<(string id, string color)>();
            foreach (var c in list.RootElement.GetProperty("items").EnumerateArray())
            {
                bool selected = c.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.True;
                bool primary = c.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True;
                if (!selected && !primary) continue;
                cals.Add((c.GetProperty("id").GetString(), c.TryGetProperty("backgroundColor", out var bg) ? bg.GetString() : null));
            }

            string tmin = Uri.EscapeDataString(from.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            string tmax = Uri.EscapeDataString(to.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            foreach (var (id, color) in cals)
            {
                try
                {
                    using var doc = await GetJsonAsync(
                        $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(id)}/events" +
                        $"?timeMin={tmin}&timeMax={tmax}&singleEvents=true&orderBy=startTime&maxResults=250");
                    foreach (var e in doc.RootElement.GetProperty("items").EnumerateArray())
                    {
                        if (e.TryGetProperty("status", out var st) && st.GetString() == "cancelled") continue;
                        if (Declined(e)) continue;
                        var ev = ToEvent(e, color);
                        if (ev != null) result.Add(ev);
                    }
                }
                catch (Exception ex) { Log.Error("Google.Events " + id, ex); }
            }
            Log.Write($"Google 日曆：{cals.Count} 個日曆、{result.Count} 個行程");
            return result;
        }

        private static bool Declined(JsonElement e)
        {
            if (!e.TryGetProperty("attendees", out var att)) return false;
            foreach (var a in att.EnumerateArray())
                if (a.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True &&
                    a.TryGetProperty("responseStatus", out var rs) && rs.GetString() == "declined") return true;
            return false;
        }

        private static CalEvent ToEvent(JsonElement e, string color)
        {
            if (!e.TryGetProperty("start", out var s) || !e.TryGetProperty("end", out var en)) return null;
            var ev = new CalEvent { Summary = e.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "(無標題)" : "(無標題)", Color = color };
            if (s.TryGetProperty("date", out var sd))
            {
                ev.AllDay = true;
                ev.Start = DateTime.ParseExact(sd.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                ev.End = en.TryGetProperty("date", out var ed) ? DateTime.ParseExact(ed.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture) : ev.Start.AddDays(1);
            }
            else if (s.TryGetProperty("dateTime", out var sdt))
            {
                ev.Start = DateTimeOffset.Parse(sdt.GetString(), CultureInfo.InvariantCulture).LocalDateTime;
                ev.End = en.TryGetProperty("dateTime", out var edt) ? DateTimeOffset.Parse(edt.GetString(), CultureInfo.InvariantCulture).LocalDateTime : ev.Start.AddHours(1);
            }
            else return null;
            return ev;
        }

        // ================================================================
        //  儲存（DPAPI）
        // ================================================================

        private static Store Load()
        {
            if (_store != null) return _store;
            try
            {
                if (File.Exists(StorePath))
                {
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(StorePath), null, DataProtectionScope.CurrentUser);
                    _store = JsonSerializer.Deserialize<Store>(plain) ?? new Store();
                    return _store;
                }
            }
            catch (Exception ex) { Log.Error("Google.Load", ex); }
            _store = new Store();
            return _store;
        }

        private static void Save(Store s)
        {
            _store = s;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                byte[] data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(s), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(StorePath, data);
            }
            catch (Exception ex) { Log.Error("Google.Save", ex); }
        }

        // ================================================================
        //  工具
        // ================================================================

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static string RandomString(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

        private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string ErrorText(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.TryGetProperty("error_description", out var d)) return d.GetString();
                if (r.TryGetProperty("error", out var e))
                    return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString();
            }
            catch { }
            return json.Length > 200 ? json.Substring(0, 200) : json;
        }
    }
}
