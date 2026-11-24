using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>電腦這邊開放給手機的一個項目（檔案或一段文字）</summary>
    public sealed class ShareOffer
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; }
        public string Text { get; set; }
        public long Size { get; set; }
        public DateTime At { get; set; } = DateTime.Now;
        public bool IsText => Text != null;
    }

    /// <summary>一個可以讓手機連進來的網路位址</summary>
    public sealed class LanAddress
    {
        public string Ip { get; set; } = "";
        public string Adapter { get; set; } = "";
        public int Score { get; set; }
    }

    /// <summary>
    /// 手機 ↔ 電腦傳檔（同一個 Wi-Fi，不用線、不用裝 App）：
    /// 電腦開一個只在區域網路的小網頁伺服器，網址裡有一組每次都不一樣的隨機密碼；手機掃 QR code 打開網頁就能
    /// 上傳檔案／文字到電腦，也能下載電腦這邊拖進來的檔案。只接受區域網路來的連線；10 分鐘沒有動作自動關閉。
    /// </summary>
    public sealed class PhoneShare : IDisposable
    {
        public const int DefaultPort = 47811;
        private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);
        private const long MaxText = 256 * 1024;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly byte[] _tokenBytes;
        private readonly List<ShareOffer> _offers = new List<ShareOffer>();
        private readonly Timer _idleTimer;
        private int _uploading;
        private int _nextId;

        public int Port { get; }
        public string Token { get; }
        public IReadOnlyList<LanAddress> Addresses { get; private set; }
        public int AddressIndex { get; set; }
        public string ReceiveFolder { get; set; }
        public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
        public bool Running { get; private set; } = true;
        /// <summary>最近一次連進來的手機（例如「iPhone」），還沒有就是 null</summary>
        public string Client { get; private set; }
        public DateTime ClientSeen { get; private set; }

        public string Host => Addresses.Count > 0 ? Addresses[Math.Clamp(AddressIndex, 0, Addresses.Count - 1)].Ip : "127.0.0.1";
        public string Url => $"http://{Host}:{Port}/s/{Token}";

        // 事件都在背景執行緒觸發，介面要自己切回 UI 執行緒
        public event Action<string> ClientConnected;
        /// <summary>手機上傳中：檔名、已收到、總大小</summary>
        public event Action<string, long, long> UploadProgress;
        /// <summary>收到一個檔案：手機上的檔名、存到電腦的完整路徑</summary>
        public event Action<string, string> FileReceived;
        public event Action<string, string> UploadFailed;
        public event Action<string> TextReceived;
        /// <summary>手機下載了電腦這邊的某個項目</summary>
        public event Action<ShareOffer> OfferDownloaded;
        public event Action<string> Stopped;

        private PhoneShare(TcpListener listener, int port, string folder)
        {
            _listener = listener;
            Port = port;
            ReceiveFolder = folder;
            _tokenBytes = RandomNumberGenerator.GetBytes(12);
            Token = Convert.ToBase64String(_tokenBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            Addresses = LanAddresses();
            _idleTimer = new Timer(_ => CheckIdle(), null, 30000, 30000);
        }

        /// <summary>開始（找一個可以用的連接埠）；失敗丟出例外</summary>
        public static PhoneShare Start(string folder, int preferredPort = DefaultPort)
        {
            Exception last = null;
            foreach (int port in Enumerable.Range(preferredPort, 10).Append(0))
            {
                try
                {
                    var l = new TcpListener(IPAddress.Any, port);
                    l.Start(16);
                    int real = ((IPEndPoint)l.LocalEndpoint).Port;
                    var s = new PhoneShare(l, real, folder);
                    _ = s.AcceptLoopAsync();
                    Log.Write($"PhoneShare: 開始 port {real}，位址 {string.Join("、", s.Addresses.Select(a => a.Ip + "(" + a.Adapter + ")"))}");
                    return s;
                }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new InvalidOperationException("找不到可以用的連接埠");
        }

        public void Stop(string reason = null)
        {
            if (!Running) return;
            Running = false;
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _idleTimer.Dispose(); } catch { }
            Log.Write("PhoneShare: 停止" + (reason != null ? "（" + reason + "）" : ""));
            try { Stopped?.Invoke(reason); } catch { }
        }

        public void Dispose() => Stop();

        public void RefreshAddresses()
        {
            string cur = Host;
            Addresses = LanAddresses();
            int i = Addresses.ToList().FindIndex(a => a.Ip == cur);
            AddressIndex = Math.Max(0, i);
        }

        private void CheckIdle()
        {
            if (!Running || Volatile.Read(ref _uploading) > 0) return;
            if (DateTime.UtcNow - LastActivity > IdleLimit) Stop("10 分鐘沒有動作");
        }

        /// <summary>有人在用（介面上拖檔案進來也算）：延後自動關閉</summary>
        public void Touch() => LastActivity = DateTime.UtcNow;

        // ================================================================
        //  電腦 → 手機
        // ================================================================

        public ShareOffer OfferFile(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            lock (_offers)
            {
                var existing = _offers.FirstOrDefault(o => o.Path != null && string.Equals(o.Path, fi.FullName, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { existing.Size = fi.Length; existing.At = DateTime.Now; Touch(); return existing; }
                var o = new ShareOffer { Id = "f" + (++_nextId), Name = fi.Name, Path = fi.FullName, Size = fi.Length };
                _offers.Add(o);
                Touch();
                return o;
            }
        }

        public ShareOffer OfferText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.Length > MaxText) text = text.Substring(0, (int)MaxText);
            lock (_offers)
            {
                var o = new ShareOffer { Id = "t" + (++_nextId), Name = OneLine(text, 40), Text = text, Size = Encoding.UTF8.GetByteCount(text) };
                _offers.Add(o);
                Touch();
                return o;
            }
        }

        public void RemoveOffer(string id)
        {
            lock (_offers) _offers.RemoveAll(o => o.Id == id);
        }

        public List<ShareOffer> Offers() { lock (_offers) return _offers.ToList(); }

        // ================================================================
        //  HTTP
        // ================================================================

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Log.Write("PhoneShare: accept " + ex.Message); await Task.Delay(200); continue; }
                _ = Task.Run(() => HandleAsync(c));
            }
        }

        private sealed class Request
        {
            public string Method = "", Path = "", Query = "";
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Stream Body;
            public long Length = -1;
            public string Remote = "";
            public string Q(string key)
            {
                foreach (var part in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int i = part.IndexOf('=');
                    string k = Uri.UnescapeDataString((i < 0 ? part : part[..i]).Replace('+', ' '));
                    if (k == key) return i < 0 ? "" : Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' '));
                }
                return null;
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    if (remote == null || !IsLocal(remote)) { Log.Write("PhoneShare: 拒絕非區域網路連線 " + remote); return; }
                    var ns = client.GetStream();
                    using var headCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    headCts.CancelAfter(TimeSpan.FromSeconds(15));
                    var req = await ReadRequestAsync(ns, headCts.Token);
                    if (req == null) return;
                    req.Remote = remote.ToString();
                    await RouteAsync(req, ns);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (Exception ex) { Log.Write("PhoneShare: " + ex.GetType().Name + " " + ex.Message); }
            }
        }

        private static async Task<Request> ReadRequestAsync(NetworkStream ns, CancellationToken ct)
        {
            var buf = new byte[16384];
            int len = 0, end = -1;
            while (end < 0)
            {
                if (len == buf.Length) return null;   // 標頭太長
                int n = await ns.ReadAsync(buf.AsMemory(len, buf.Length - len), ct);
                if (n <= 0) return null;
                len += n;
                for (int i = Math.Max(0, len - n - 3); i + 3 < len; i++)
                    if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n') { end = i; break; }
            }
            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var req = new Request { Method = first[0].ToUpperInvariant() };
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = q < 0 ? target : target[..q];
            req.Query = q < 0 ? "" : target[(q + 1)..];
            foreach (var l in lines.Skip(1))
            {
                int c = l.IndexOf(':');
                if (c > 0) req.Headers[l[..c].Trim()] = l[(c + 1)..].Trim();
            }
            if (req.Headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out long length)) req.Length = length;
            int bodyStart = end + 4;
            req.Body = new PrefixStream(buf, bodyStart, len - bodyStart, ns);
            return req;
        }

        private bool TokenOk(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            var a = Encoding.ASCII.GetBytes(t);
            var b = Encoding.ASCII.GetBytes(Token);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        private async Task RouteAsync(Request req, NetworkStream ns)
        {
            // /s/{token}[/動作[/id]]
            var parts = req.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "s" || !TokenOk(parts[1]))
            {
                if (req.Path == "/favicon.ico") { await SendAsync(ns, 404, "text/plain", Array.Empty<byte>()); return; }
                await SendAsync(ns, 404, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(ExpiredPage));
                return;
            }
            Touch();
            string action = parts.Length >= 3 ? parts[2] : "";
            string ua = req.Headers.TryGetValue("User-Agent", out var u) ? u : "";
            SeeClient(ua);

            switch (req.Method, action)
            {
                case ("GET", ""):
                    if (!req.Path.EndsWith("/")) { await SendAsync(ns, 302, "text/plain", Array.Empty<byte>(), "Location: /s/" + Token + "/\r\n"); return; }
                    await SendAsync(ns, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PhonePage.Html(Environment.MachineName)), "Cache-Control: no-store\r\n");
                    return;
                case ("GET", "state"):
                    await SendJsonAsync(ns, StateJson());
                    return;
                case ("POST", "up"):
                    await ReceiveFileAsync(req, ns);
                    return;
                case ("POST", "text"):
                    await ReceiveTextAsync(req, ns);
                    return;
                case ("GET", "f") when parts.Length >= 4:
                    await SendOfferAsync(ns, parts[3], req.Q("inline") == "1");
                    return;
                default:
                    await SendAsync(ns, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"));
                    return;
            }
        }

        private void SeeClient(string ua)
        {
            string name = DeviceName(ua);
            bool fresh = Client == null || DateTime.UtcNow - ClientSeen > TimeSpan.FromSeconds(20) || Client != name;
            Client = name;
            ClientSeen = DateTime.UtcNow;
            if (fresh) try { ClientConnected?.Invoke(name); } catch { }
        }

        private string StateJson()
        {
            var list = Offers().OrderByDescending(o => o.At).Select(o => new Dictionary<string, object>
            {
                ["id"] = o.Id, ["name"] = o.Name, ["size"] = o.Size, ["text"] = o.Text,
                ["kind"] = o.IsText ? "text" : KindOf(o.Name)
            }).ToList();
            return JsonSerializer.Serialize(new Dictionary<string, object> { ["pc"] = Environment.MachineName, ["offers"] = list });
        }

        private async Task ReceiveFileAsync(Request req, NetworkStream ns)
        {
            string name = SafeName(req.Q("name"));
            if (req.Length < 0) { await SendJsonAsync(ns, "{\"ok\":false,\"error\":\"缺少檔案大小\"}", 411); return; }
            string folder = ReceiveFolder;
            string tmp = null;
            Interlocked.Increment(ref _uploading);
            try
            {
                Directory.CreateDirectory(folder);
                try
                {
                    var drive = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder)));
                    if (drive.AvailableFreeSpace < req.Length + 50L * 1024 * 1024)
                    {
                        UploadFailed?.Invoke(name, "電腦的磁碟空間不夠");
                        await SendJsonAsync(ns, "{\"ok\":false,\"error\":\"電腦的磁碟空間不夠\"}", 507);
                        return;
                    }
                }
                catch { }
                tmp = System.IO.Path.Combine(folder, "." + Guid.NewGuid().ToString("N")[..8] + ".islandbar-part");
                long got = 0;
                var lastReport = DateTime.MinValue;
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    var buf = new byte[1 << 16];
                    while (got < req.Length)
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        readCts.CancelAfter(TimeSpan.FromSeconds(60));   // 一分鐘都沒收到東西＝斷線了
                        int n = await req.Body.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, req.Length - got)), readCts.Token);
                        if (n <= 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                        got += n;
                        Touch();
                        if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(150))
                        {
                            lastReport = DateTime.UtcNow;
                            try { UploadProgress?.Invoke(name, got, req.Length); } catch { }
                        }
                    }
                }
                if (got < req.Length)
                {
                    TryDelete(tmp);
                    try { UploadFailed?.Invoke(name, "傳到一半斷線了"); } catch { }
                    return;
                }
                string final = UniquePath(folder, name);
                File.Move(tmp, final);
                tmp = null;
                Log.Write($"PhoneShare: 收到 {System.IO.Path.GetFileName(final)}（{got} bytes）");
                try { UploadProgress?.Invoke(name, got, req.Length); } catch { }
                try { FileReceived?.Invoke(name, final); } catch { }
                await SendJsonAsync(ns, JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = true, ["name"] = System.IO.Path.GetFileName(final) }));
            }
            catch (Exception ex)
            {
                if (tmp != null) TryDelete(tmp);
                if (ex is not OperationCanceledException && ex is not IOException) Log.Write("PhoneShare: 收檔失敗 " + ex.Message);
                try { UploadFailed?.Invoke(name, ex is IOException or OperationCanceledException ? "傳到一半斷線了" : ex.Message); } catch { }
                try { await SendJsonAsync(ns, "{\"ok\":false,\"error\":\"儲存失敗\"}", 500); } catch { }
            }
            finally { Interlocked.Decrement(ref _uploading); Touch(); }
        }

        private async Task ReceiveTextAsync(Request req, NetworkStream ns)
        {
            if (req.Length < 0 || req.Length > MaxText) { await SendJsonAsync(ns, "{\"ok\":false,\"error\":\"文字太長\"}", 413); return; }
            var buf = new byte[req.Length];
            int got = 0;
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            readCts.CancelAfter(TimeSpan.FromSeconds(20));
            while (got < buf.Length)
            {
                int n = await req.Body.ReadAsync(buf.AsMemory(got), readCts.Token);
                if (n <= 0) break;
                got += n;
            }
            string text = Encoding.UTF8.GetString(buf, 0, got).Replace("\r\n", "\n").Trim();
            if (text.Length > 0) try { TextReceived?.Invoke(text); } catch { }
            await SendJsonAsync(ns, "{\"ok\":true}");
        }

        private async Task SendOfferAsync(NetworkStream ns, string id, bool inline)
        {
            var o = Offers().FirstOrDefault(x => x.Id == id);
            if (o == null) { await SendAsync(ns, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("這個項目已經移除了")); return; }
            if (o.IsText)
            {
                await SendAsync(ns, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(o.Text));
                return;
            }
            FileStream fs;
            try { fs = new FileStream(o.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true); }
            catch { await SendAsync(ns, 410, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("電腦上找不到這個檔案了")); return; }
            using (fs)
            {
                string ascii = new string(o.Name.Select(ch => ch < 128 && ch >= 32 && ch != '"' && ch != '\\' ? ch : '_').ToArray());
                string disp = $"Content-Disposition: {(inline ? "inline" : "attachment")}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(o.Name)}\r\n";
                string head = $"HTTP/1.1 200 OK\r\nContent-Type: {MimeOf(o.Name)}\r\nContent-Length: {fs.Length}\r\n{disp}Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                var hb = Encoding.UTF8.GetBytes(head);
                await ns.WriteAsync(hb, _cts.Token);
                var buf = new byte[1 << 16];
                int n;
                while ((n = await fs.ReadAsync(buf, _cts.Token)) > 0)
                {
                    await ns.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                    Touch();
                }
                await ns.FlushAsync();
            }
            if (!inline) try { OfferDownloaded?.Invoke(o); } catch { }
        }

        private Task SendJsonAsync(NetworkStream ns, string json, int status = 200) =>
            SendAsync(ns, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), "Cache-Control: no-store\r\n");

        private async Task SendAsync(NetworkStream ns, int status, string type, byte[] body, string extra = "")
        {
            string reason = status switch { 200 => "OK", 302 => "Found", 404 => "Not Found", 410 => "Gone", 411 => "Length Required", 413 => "Payload Too Large", 507 => "Insufficient Storage", _ => "Error" };
            string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n{extra}Connection: close\r\n\r\n";
            var hb = Encoding.UTF8.GetBytes(head);
            await ns.WriteAsync(hb, _cts.Token);
            if (body.Length > 0) await ns.WriteAsync(body, _cts.Token);
            await ns.FlushAsync();
        }

        // ================================================================
        //  小工具
        // ================================================================

        /// <summary>只接受區域網路（家裡／公司的 Wi-Fi）來的連線</summary>
        internal static bool IsLocal(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        /// <summary>電腦的區域網路位址（真的網卡、有預設閘道的排前面；虛擬網卡排後面）</summary>
        public static List<LanAddress> LanAddresses()
        {
            var list = new List<LanAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    bool virt = new[] { "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "wsl", "tap-", "wintun", "wireguard", "zerotier", "tailscale", "npcap", "docker", "bluetooth", "loopback" }.Any(d.Contains);
                    var props = ni.GetIPProperties();
                    bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    bool wifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    foreach (var ua in props.UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                        var b = a.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue;
                        if (IPAddress.IsLoopback(a)) continue;
                        int score = (gw ? 100 : 0) + (virt ? 0 : 50) + (wifi ? 10 : 0) + (IsLocal(a) ? 20 : 0);
                        list.Add(new LanAddress { Ip = a.ToString(), Adapter = ni.Name, Score = score });
                    }
                }
            }
            catch (Exception ex) { Log.Write("PhoneShare: 找網路位址失敗 " + ex.Message); }
            return list.OrderByDescending(a => a.Score).ToList();
        }

        public static string DeviceName(string ua)
        {
            if (string.IsNullOrEmpty(ua)) return "手機";
            if (ua.Contains("iPhone")) return "iPhone";
            if (ua.Contains("iPad") || (ua.Contains("Macintosh") && ua.Contains("Mobile"))) return "iPad";
            if (ua.Contains("Android")) return ua.Contains("Mobile") ? "Android 手機" : "Android 平板";
            if (ua.Contains("Windows")) return "Windows 電腦";
            if (ua.Contains("Macintosh")) return "Mac";
            return "手機";
        }

        public static string SafeName(string name)
        {
            name = System.IO.Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last());
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            name = new string(name.Where(c => !invalid.Contains(c) && c >= 32).ToArray()).Trim().TrimEnd('.', ' ').TrimStart('.');
            if (name.Length == 0) name = "手機檔案_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (name.Length > 150)
            {
                string ext = System.IO.Path.GetExtension(name);
                if (ext.Length > 20) ext = "";
                name = name[..(150 - ext.Length)] + ext;
            }
            string stem = System.IO.Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)\d$")) name = "_" + name;
            return name;
        }

        public static string UniquePath(string folder, string name)
        {
            string p = System.IO.Path.Combine(folder, name);
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
            string stem = System.IO.Path.GetFileNameWithoutExtension(name), ext = System.IO.Path.GetExtension(name);
            for (int i = 2; i < 10000; i++)
            {
                p = System.IO.Path.Combine(folder, $"{stem} ({i}){ext}");
                if (!File.Exists(p) && !Directory.Exists(p)) return p;
            }
            return System.IO.Path.Combine(folder, $"{stem} {Guid.NewGuid():N}{ext}");
        }

        public static string KindOf(string name)
        {
            string e = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            if (e is "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "heic" or "heif" or "avif") return "image";
            if (e is "mp4" or "mov" or "m4v" or "webm" or "mkv" or "avi") return "video";
            if (e is "mp3" or "m4a" or "aac" or "wav" or "flac" or "ogg" or "opus") return "audio";
            if (e is "pdf") return "pdf";
            if (e is "zip" or "rar" or "7z") return "zip";
            return "file";
        }

        private static string MimeOf(string name)
        {
            string e = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            return e switch
            {
                "jpg" or "jpeg" => "image/jpeg", "png" => "image/png", "gif" => "image/gif", "webp" => "image/webp", "bmp" => "image/bmp",
                "heic" => "image/heic", "avif" => "image/avif",
                "mp4" or "m4v" => "video/mp4", "mov" => "video/quicktime", "webm" => "video/webm",
                "mp3" => "audio/mpeg", "m4a" => "audio/mp4", "wav" => "audio/wav",
                "pdf" => "application/pdf", "txt" => "text/plain; charset=utf-8", "zip" => "application/zip",
                _ => "application/octet-stream"
            };
        }

        public static string OneLine(string text, int max)
        {
            string t = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
            return t.Length > max ? t[..max] + "…" : t;
        }

        public static string FormatSize(long b) =>
            b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.#} KB" : b < 1024L * 1024 * 1024 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

        private static void TryDelete(string p) { try { File.Delete(p); } catch { } }

        private const string ExpiredPage = "<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>IslandBar</title>" +
            "<body style=\"background:#0e0e10;color:#eee;font-family:system-ui,sans-serif;display:flex;align-items:center;justify-content:center;height:90vh;text-align:center\">" +
            "<div><div style=\"font-size:40px\">⌛</div><h3>這個連結已經失效</h3><p style=\"color:#8e8e96\">請在電腦上重新打開「手機傳檔」，再掃一次 QR code。</p></div>";

        /// <summary>先讀已經收到的那一段，再接著讀網路</summary>
        private sealed class PrefixStream : Stream
        {
            private readonly byte[] _buf;
            private int _pos, _left;
            private readonly Stream _inner;
            public PrefixStream(byte[] buf, int start, int count, Stream inner) { _buf = buf; _pos = start; _left = Math.Max(0, count); _inner = inner; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left > 0) { int n = Math.Min(count, _left); Buffer.BlockCopy(_buf, _pos, buffer, offset, n); _pos += n; _left -= n; return n; }
                return _inner.Read(buffer, offset, count);
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (_left > 0) { int n = Math.Min(buffer.Length, _left); _buf.AsMemory(_pos, n).CopyTo(buffer); _pos += n; _left -= n; return n; }
                return await _inner.ReadAsync(buffer, ct);
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
