using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace IslandBar.Linux.Sensors
{
    public sealed class DiskInfo
    {
        public string Mount { get; set; } = "";
        public string Device { get; set; } = "";
        public string FsType { get; set; } = "";
        public double TotalGb { get; set; }
        public double UsedGb { get; set; }
        public double Pct => TotalGb > 0 ? Math.Clamp(UsedGb / TotalGb * 100, 0, 100) : 0;
    }

    public sealed class NetInfo
    {
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "";
        public double RxBps { get; set; }
        public double TxBps { get; set; }
        public bool Wireless { get; set; }
    }

    public sealed class SystemSnapshot
    {
        public double? CpuPct { get; set; }
        public int Cores { get; set; }
        public string CpuModel { get; set; } = "";
        public double? CpuMhz { get; set; }
        public string LoadAvg { get; set; } = "";
        public double MemTotalGb { get; set; }
        public double MemUsedGb { get; set; }
        public double MemPct => MemTotalGb > 0 ? Math.Clamp(MemUsedGb / MemTotalGb * 100, 0, 100) : 0;
        public double SwapTotalGb { get; set; }
        public double SwapUsedGb { get; set; }
        public List<DiskInfo> Disks { get; set; } = new List<DiskInfo>();
        public double DiskReadBps { get; set; }
        public double DiskWriteBps { get; set; }
        public List<NetInfo> Nets { get; set; } = new List<NetInfo>();
        public double NetRxBps => Nets.Sum(n => n.RxBps);
        public double NetTxBps => Nets.Sum(n => n.TxBps);
        public TimeSpan Uptime { get; set; }
        public string Host { get; set; } = "";
        public string Distro { get; set; } = "";
    }

    /// <summary>CPU／記憶體／磁碟／網路：全部讀 /proc 與 /sys，不需要 root。</summary>
    public sealed class SystemSampler
    {
        private long _prevTotal, _prevIdle;
        private readonly Dictionary<string, (long rx, long tx)> _prevNet = new Dictionary<string, (long, long)>();
        private long _prevRead, _prevWrite;
        private DateTime _prevAt = DateTime.MinValue;
        private int _tick;
        private List<DiskInfo> _disks = new List<DiskInfo>();
        private Dictionary<string, string> _ips = new Dictionary<string, string>();
        private readonly string _cpuModel;
        private readonly string _distro;

        private static readonly Regex WholeDisk = new Regex(@"^(sd[a-z]+|hd[a-z]+|vd[a-z]+|xvd[a-z]+|nvme\d+n\d+|mmcblk\d+)$", RegexOptions.Compiled);
        private static readonly HashSet<string> RealFs = new HashSet<string>
        {
            "ext2", "ext3", "ext4", "xfs", "btrfs", "f2fs", "ntfs", "ntfs3", "fuseblk", "exfat", "zfs", "bcachefs", "jfs", "reiserfs"
        };

        public SystemSampler()
        {
            _cpuModel = ReadCpuModel();
            _distro = ReadDistro();
        }

        public SystemSnapshot Sample()
        {
            var s = new SystemSnapshot { CpuModel = _cpuModel, Distro = _distro, Host = Environment.MachineName };
            var now = DateTime.UtcNow;
            double dt = _prevAt == DateTime.MinValue ? 0 : (now - _prevAt).TotalSeconds;
            _prevAt = now;

            try { ReadCpu(s); } catch (Exception ex) { Log.Error("Sys.cpu", ex); }
            try { ReadMem(s); } catch (Exception ex) { Log.Error("Sys.mem", ex); }
            try
            {
                if (_tick % 10 == 0) _disks = ReadDisks();
                s.Disks = _disks;
                ReadDiskIo(s, dt);
            }
            catch (Exception ex) { Log.Error("Sys.disk", ex); }
            try
            {
                if (_tick % 10 == 0) _ips = ReadIps();
                ReadNet(s, dt);
            }
            catch (Exception ex) { Log.Error("Sys.net", ex); }
            try
            {
                string up = File.ReadAllText("/proc/uptime").Split(' ')[0];
                s.Uptime = TimeSpan.FromSeconds(double.Parse(up, CultureInfo.InvariantCulture));
                s.LoadAvg = string.Join("  ", File.ReadAllText("/proc/loadavg").Split(' ').Take(3));
            }
            catch { }
            _tick++;
            return s;
        }

        private void ReadCpu(SystemSnapshot s)
        {
            int cores = 0;
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("cpu")) break;
                if (line.StartsWith("cpu "))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
                        .Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    long total = f.Sum(), idle = f[3] + (f.Length > 4 ? f[4] : 0);
                    if (_prevTotal > 0 && total > _prevTotal)
                        s.CpuPct = Math.Clamp(100.0 * (1 - (double)(idle - _prevIdle) / (total - _prevTotal)), 0, 100);
                    _prevTotal = total; _prevIdle = idle;
                }
                else cores++;
            }
            s.Cores = cores > 0 ? cores : Environment.ProcessorCount;

            // 目前時脈：各核心 scaling_cur_freq 的平均（kHz）
            double sum = 0; int n = 0;
            for (int i = 0; i < s.Cores; i++)
            {
                string f = $"/sys/devices/system/cpu/cpu{i}/cpufreq/scaling_cur_freq";
                if (!File.Exists(f)) continue;
                if (double.TryParse(File.ReadAllText(f).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double khz)) { sum += khz; n++; }
            }
            if (n > 0) s.CpuMhz = sum / n / 1000.0;
        }

        private static void ReadMem(SystemSnapshot s)
        {
            long total = 0, avail = -1, free = 0, buffers = 0, cached = 0, swapTotal = 0, swapFree = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line.Substring(0, colon);
                var parts = line.Substring(colon + 1).Trim().Split(' ');
                if (!long.TryParse(parts[0], out long kb)) continue;
                switch (key)
                {
                    case "MemTotal": total = kb; break;
                    case "MemAvailable": avail = kb; break;
                    case "MemFree": free = kb; break;
                    case "Buffers": buffers = kb; break;
                    case "Cached": cached = kb; break;
                    case "SwapTotal": swapTotal = kb; break;
                    case "SwapFree": swapFree = kb; break;
                }
            }
            if (avail < 0) avail = free + buffers + cached;
            const double KbPerGb = 1024.0 * 1024.0;
            s.MemTotalGb = total / KbPerGb;
            s.MemUsedGb = (total - avail) / KbPerGb;
            s.SwapTotalGb = swapTotal / KbPerGb;
            s.SwapUsedGb = (swapTotal - swapFree) / KbPerGb;
        }

        private static List<DiskInfo> ReadDisks()
        {
            var list = new List<DiskInfo>();
            var seen = new Dictionary<string, DiskInfo>();
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var f = line.Split(' ');
                if (f.Length < 3) continue;
                string dev = f[0], mount = Unescape(f[1]), fs = f[2];
                if (!RealFs.Contains(fs)) continue;
                if (mount.StartsWith("/snap/") || mount.StartsWith("/var/snap/") || mount.StartsWith("/var/lib/docker") ||
                    mount.StartsWith("/run/") || mount.StartsWith("/proc") || mount.StartsWith("/sys")) continue;
                try
                {
                    var di = new DriveInfo(mount);
                    if (di.TotalSize <= 0) continue;
                    var d = new DiskInfo
                    {
                        Mount = mount, Device = dev, FsType = fs,
                        TotalGb = di.TotalSize / 1073741824.0,
                        UsedGb = (di.TotalSize - di.TotalFreeSpace) / 1073741824.0
                    };
                    // btrfs 子磁碟區等：同一個裝置只留最短的掛載點
                    if (seen.TryGetValue(dev, out var prev))
                    {
                        if (mount.Length < prev.Mount.Length) { list.Remove(prev); list.Add(d); seen[dev] = d; }
                        continue;
                    }
                    seen[dev] = d;
                    list.Add(d);
                }
                catch { }
            }
            return list.OrderBy(d => d.Mount == "/" ? "" : d.Mount, StringComparer.Ordinal).ToList();
        }

        private static string Unescape(string s) => s.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\134", "\\");

        private void ReadDiskIo(SystemSnapshot s, double dt)
        {
            long read = 0, write = 0;
            foreach (var line in File.ReadLines("/proc/diskstats"))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10 || !WholeDisk.IsMatch(f[2])) continue;
                read += long.Parse(f[5], CultureInfo.InvariantCulture) * 512;
                write += long.Parse(f[9], CultureInfo.InvariantCulture) * 512;
            }
            if (dt > 0 && _prevRead > 0)
            {
                s.DiskReadBps = Math.Max(0, (read - _prevRead) / dt);
                s.DiskWriteBps = Math.Max(0, (write - _prevWrite) / dt);
            }
            _prevRead = read; _prevWrite = write;
        }

        private static bool IsVirtualNic(string n) =>
            n == "lo" || n.StartsWith("veth") || n.StartsWith("docker") || n.StartsWith("br-") || n.StartsWith("virbr") ||
            n.StartsWith("vnet") || n.StartsWith("cni") || n.StartsWith("flannel") || n.StartsWith("kube") || n.StartsWith("lxc") || n.StartsWith("podman");

        private void ReadNet(SystemSnapshot s, double dt)
        {
            var seen = new HashSet<string>();
            foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string name = line.Substring(0, colon).Trim();
                if (IsVirtualNic(name)) continue;
                var f = line.Substring(colon + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 9) continue;
                long rx = long.Parse(f[0], CultureInfo.InvariantCulture), tx = long.Parse(f[8], CultureInfo.InvariantCulture);
                seen.Add(name);
                var n = new NetInfo
                {
                    Name = name,
                    Ip = _ips.TryGetValue(name, out var ip) ? ip : "",
                    Wireless = Directory.Exists($"/sys/class/net/{name}/wireless")
                };
                if (dt > 0 && _prevNet.TryGetValue(name, out var p))
                {
                    n.RxBps = Math.Max(0, (rx - p.rx) / dt);
                    n.TxBps = Math.Max(0, (tx - p.tx) / dt);
                }
                _prevNet[name] = (rx, tx);
                // 沒有 IP 又沒有流量的介面（拔掉的網卡等）不顯示
                if (n.Ip.Length > 0 || n.RxBps > 0 || n.TxBps > 0) s.Nets.Add(n);
            }
            foreach (var gone in _prevNet.Keys.Where(k => !seen.Contains(k)).ToList()) _prevNet.Remove(gone);
        }

        private static Dictionary<string, string> ReadIps()
        {
            var map = new Dictionary<string, string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up && ni.OperationalStatus != OperationalStatus.Unknown) continue;
                    var v4 = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (v4 != null) map[ni.Name] = v4.Address.ToString();
                }
            }
            catch (Exception ex) { Log.Error("Sys.ips", ex); }
            return map;
        }

        private static string ReadCpuModel()
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name") || line.StartsWith("Model") || line.StartsWith("Hardware"))
                    {
                        int c = line.IndexOf(':');
                        if (c > 0) return Tidy(line.Substring(c + 1).Trim());
                    }
                }
                string dt = "/sys/firmware/devicetree/base/model";
                if (File.Exists(dt)) return File.ReadAllText(dt).Trim('\0', ' ', '\n');
            }
            catch { }
            return "";
        }

        private static string Tidy(string m) =>
            Regex.Replace(m.Replace("(R)", "").Replace("(TM)", "").Replace(" CPU", "").Replace("  ", " "), @"\s+@.*$", "").Trim();

        private static string ReadDistro()
        {
            try
            {
                foreach (var line in File.ReadLines("/etc/os-release"))
                    if (line.StartsWith("PRETTY_NAME=")) return line.Substring(12).Trim('"');
            }
            catch { }
            return "Linux";
        }
    }
}
