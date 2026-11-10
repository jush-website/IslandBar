using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    public sealed class NetworkState
    {
        public bool Online { get; set; } = true;
        /// <summary>Wi‑Fi 名稱；有線網路是 null</summary>
        public string Ssid { get; set; }
        public int? Signal { get; set; }
        /// <summary>連線名稱（Wi‑Fi 名稱或「乙太網路」）</summary>
        public string Connection { get; set; } = "";
        public bool Vpn { get; set; }
        public string VpnName { get; set; }
        /// <summary>到網際網路的延遲（ms）</summary>
        public long? LatencyMs { get; set; }
        public DateTime? OfflineSince { get; set; }
    }

    /// <summary>
    /// 網路狀態：連線名稱（Wi‑Fi／有線）、訊號、VPN、延遲；每 5 秒檢查一次。
    /// 斷線、恢復、換網路時觸發事件（動態島顯示提示）。
    /// </summary>
    public sealed class NetworkService
    {
        private CancellationTokenSource _cts;
        private int _tick;
        private int _fails;
        private string _ssid;
        private int? _signal;

        public NetworkState State { get; private set; } = new NetworkState();
        public event Action Updated;
        /// <summary>斷線（true）／恢復（false，附上中斷多久）</summary>
        public event Action<bool, TimeSpan> OnlineChanged;
        /// <summary>換了網路（新的連線名稱）</summary>
        public event Action<string> ConnectionChanged;

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                string lastConn = null;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var prev = State;
                        var s = await SampleAsync();
                        State = s;
                        if (prev.Online && !s.Online) OnlineChanged?.Invoke(true, TimeSpan.Zero);
                        else if (!prev.Online && s.Online) OnlineChanged?.Invoke(false, DateTime.Now - (prev.OfflineSince ?? DateTime.Now));
                        if (s.Online && lastConn != null && s.Connection != lastConn && s.Connection.Length > 0) ConnectionChanged?.Invoke(s.Connection);
                        if (s.Online && s.Connection.Length > 0) lastConn = s.Connection;
                        Updated?.Invoke();
                    }
                    catch (Exception ex) { Log.Error("Network", ex); }
                    _tick++;
                    try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch { }
                }
            });
        }

        public void Stop() { _cts?.Cancel(); _cts = null; }

        private async Task<NetworkState> SampleAsync()
        {
            var s = new NetworkState();
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();
            var vpn = nics.FirstOrDefault(IsVpn);
            s.Vpn = vpn != null;
            s.VpnName = vpn?.Name;
            var phys = nics.Where(n => !IsVpn(n) && !IsVirtual(n) && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)))
                .ToList();
            var wifi = phys.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

            // Wi‑Fi 名稱：netsh（每 15 秒讀一次就好）
            if (wifi != null)
            {
                if (_tick % 3 == 0 || _ssid == null) ReadWifi();
                s.Ssid = _ssid;
                s.Signal = _signal;
            }
            else { _ssid = null; _signal = null; }
            s.Connection = s.Ssid ?? (phys.Count > 0 ? (phys.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet) ? "有線網路" : phys[0].Name) : "");

            // 延遲：1.1.1.1 → 8.8.8.8；兩次都失敗才算斷線
            s.LatencyMs = await PingAsync("1.1.1.1") ?? await PingAsync("8.8.8.8");
            if (s.LatencyMs == null && phys.Count > 0)
            {
                // ICMP 被擋的網路（部分校園網路）：改試 DNS／HTTPS 連得上就算有網路
                if (await TcpReachable("1.1.1.1", 443) || await TcpReachable("8.8.8.8", 53)) _fails = 0;
                else _fails++;
            }
            else if (s.LatencyMs == null) _fails = 99;
            else _fails = 0;

            s.Online = _fails < 2;
            s.OfflineSince = s.Online ? null : (State.OfflineSince ?? DateTime.Now);
            return s;
        }

        private static bool IsVpn(NetworkInterface n)
        {
            string d = (n.Description + " " + n.Name).ToLowerInvariant();
            return n.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                   d.Contains("vpn") || d.Contains("wireguard") || d.Contains("tailscale") || d.Contains("openvpn") ||
                   d.Contains("anyconnect") || d.Contains("fortinet") || d.Contains("forticlient") || d.Contains("zerotier") ||
                   d.Contains("globalprotect") || d.Contains("pangp") || d.Contains("tap-windows") || d.Contains("wintun");
        }

        private static bool IsVirtual(NetworkInterface n)
        {
            string d = (n.Description + " " + n.Name).ToLowerInvariant();
            return d.Contains("hyper-v") || d.Contains("virtualbox") || d.Contains("vmware") || d.Contains("vethernet") || d.Contains("wsl") || d.Contains("bluetooth");
        }

        private static async Task<long?> PingAsync(string host)
        {
            try
            {
                using var p = new Ping();
                var r = await p.SendPingAsync(host, 1500);
                return r.Status == IPStatus.Success ? r.RoundtripTime : (long?)null;
            }
            catch { return null; }
        }

        private static async Task<bool> TcpReachable(string host, int port)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                var t = c.ConnectAsync(host, port);
                return await Task.WhenAny(t, Task.Delay(1500)) == t && c.Connected;
            }
            catch { return false; }
        }

        private void ReadWifi()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c chcp 65001 >nul & netsh wlan show interfaces")   // 強制 UTF-8，中文 Wi‑Fi 名稱才不會亂碼
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                // 英文：SSID / Signal；中文：SSID / 訊號（各語系的 SSID 欄位名稱都一樣，BSSID 要排除）
                var m = Regex.Match(o, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                _ssid = m.Success ? m.Groups[1].Value.Trim() : null;
                var sig = Regex.Match(o, @"^\s*(Signal|訊號|信号)\s*:\s*(\d+)\s*%", RegexOptions.Multiline);
                _signal = sig.Success && int.TryParse(sig.Groups[2].Value, out int v) ? v : (int?)null;
            }
            catch (Exception ex) { Log.Error("Network.Wifi", ex); _ssid = null; }
        }
    }
}
