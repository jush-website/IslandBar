using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>一台電腦目前的狀態（遠端監看用；Windows 與 Linux 版共用同一個格式）</summary>
    public sealed class RemoteStatus
    {
        public string App { get; set; } = "IslandBar";
        public string Version { get; set; } = "";
        public string Os { get; set; } = "";
        public string Host { get; set; } = "";
        public double? Cpu { get; set; }
        public double? RamUsedGb { get; set; }
        public double? RamTotalGb { get; set; }
        public double UptimeSec { get; set; }
        public List<RemoteGpu> Gpus { get; set; } = new List<RemoteGpu>();
        /// <summary>正在用 GPU 的程式</summary>
        public List<RemoteProc> GpuProcs { get; set; } = new List<RemoteProc>();
        /// <summary>訓練進度（例如「epoch 12/50 · loss 0.123」），沒有就是 null</summary>
        public string Training { get; set; }
    }

    public sealed class RemoteGpu
    {
        public string Name { get; set; } = "";
        public double? Util { get; set; }
        public double? MemUsedGb { get; set; }
        public double? MemTotalGb { get; set; }
        public double? Temp { get; set; }
        public double? PowerW { get; set; }
    }

    public sealed class RemoteProc
    {
        public string Name { get; set; } = "";
        public double? MemGb { get; set; }
    }

    /// <summary>
    /// 讓同一個區網的其他 IslandBar 讀這台電腦的狀態：GET http://這台的IP:14320/islandbar/status 回傳 JSON。
    /// 只讀、不接受任何指令，只回應區網／本機的來源（10.x、172.16–31.x、192.168.x、100.64/10、IPv6 區域位址）。
    /// 用 TcpListener 自己回 HTTP，不需要系統管理員權限（第一次會跳 Windows 防火牆詢問，允許「私人網路」即可）。
    /// </summary>
    public sealed class StatusServer : IDisposable
    {
        public const int DefaultPort = 14320;
        public const string PathPrefix = "/islandbar/status";

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private readonly Func<RemoteStatus> _snapshot;
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        public int Port { get; private set; }
        public bool Running => _listener != null;
        public string LastError { get; private set; }

        public StatusServer(Func<RemoteStatus> snapshot) { _snapshot = snapshot; }

        public bool Start(int port)
        {
            Stop();
            try
            {
                var l = new TcpListener(IPAddress.IPv6Any, port);
                l.Server.DualMode = true;   // IPv4 + IPv6
                l.Start();
                _listener = l;
                Port = port;
                LastError = null;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                Task.Run(() => AcceptLoop(l, token));
                Log.Write($"StatusServer: listening on port {port}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("StatusServer.Start", ex);
                _listener = null;
                return false;
            }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts = null;
        }

        public void Dispose() => Stop();

        private async Task AcceptLoop(TcpListener l, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(token); }
                catch { break; }
                _ = Task.Run(() => Handle(c));
            }
        }

        private async Task Handle(TcpClient c)
        {
            using (c)
            {
                try
                {
                    var remote = (c.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    if (remote == null || !IsLocalNetwork(remote)) return;
                    c.ReceiveTimeout = 3000;
                    c.SendTimeout = 3000;
                    using var stream = c.GetStream();
                    using var cts = new CancellationTokenSource(3000);
                    var buf = new byte[4096];
                    var sb = new StringBuilder();
                    while (sb.Length < 8192)
                    {
                        int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                        if (n <= 0) break;
                        sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        if (sb.ToString().Contains("\r\n\r\n")) break;
                    }
                    string req = sb.ToString();
                    int sp1 = req.IndexOf(' '), sp2 = sp1 >= 0 ? req.IndexOf(' ', sp1 + 1) : -1;
                    string method = sp1 > 0 ? req.Substring(0, sp1) : "";
                    string path = sp2 > sp1 ? req.Substring(sp1 + 1, sp2 - sp1 - 1) : "";

                    int code; string body;
                    if (method != "GET") { code = 405; body = "{\"error\":\"method\"}"; }
                    else if (path == "/" || path.StartsWith(PathPrefix, StringComparison.Ordinal))
                    {
                        code = 200;
                        body = JsonSerializer.Serialize(_snapshot(), Json);
                    }
                    else { code = 404; body = "{\"error\":\"not found\"}"; }

                    var bytes = Encoding.UTF8.GetBytes(body);
                    string head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : code == 404 ? "Not Found" : "Method Not Allowed")}\r\n" +
                                  "Content-Type: application/json; charset=utf-8\r\n" +
                                  $"Content-Length: {bytes.Length}\r\n" +
                                  "Cache-Control: no-store\r\n" +
                                  "Connection: close\r\n\r\n";
                    var hb = Encoding.ASCII.GetBytes(head);
                    await stream.WriteAsync(hb, 0, hb.Length);
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
                catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is SocketException) { }
                catch (Exception ex) { Log.Error("StatusServer.Handle", ex); }
            }
        }

        /// <summary>和這台電腦的任何一張網卡在同一個 IPv4 子網</summary>
        private static bool SameSubnet(IPAddress a)
        {
            try
            {
                var ab = a.GetAddressBytes();
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                        var lb = u.Address.GetAddressBytes();
                        var mb = u.IPv4Mask.GetAddressBytes();
                        if (mb.Length != 4 || (mb[0] == 0 && mb[1] == 0)) continue;   // 遮罩太寬的不算
                        bool same = true;
                        for (int i = 0; i < 4; i++) if ((ab[i] & mb[i]) != (lb[i] & mb[i])) { same = false; break; }
                        if (same) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>本機、私人網段、CGNAT（Tailscale 等用的 100.64/10）、IPv6 區域位址</summary>
        public static bool IsLocalNetwork(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                return b[0] == 10 ||
                       (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                       (b[0] == 192 && b[1] == 168) ||
                       (b[0] == 169 && b[1] == 254) ||
                       (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                       SameSubnet(a);   // 校園網路常是公有格式的 IP：同一個子網也算區網
            }
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = a.GetAddressBytes();
                return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;   // fc00::/7
            }
            return false;
        }
    }
}
