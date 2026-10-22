using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace IslandBar.Services
{
    /// <summary>CPU / 記憶體 / 電池 / 網路 / 磁碟 等系統資訊</summary>
    public sealed class SystemStats
    {
        private long _prevIdle, _prevKernel, _prevUser;
        private long _prevRx, _prevTx;
        private DateTime _prevNetTime = DateTime.MinValue;

        public double CpuPercent { get; private set; }
        public double RamPercent { get; private set; }
        public double RamUsedGb { get; private set; }
        public double RamTotalGb { get; private set; }

        public bool HasBattery { get; private set; }
        public double BatteryPercent { get; private set; }
        public bool IsCharging { get; private set; }

        public double DownBytesPerSec { get; private set; }
        public double UpBytesPerSec { get; private set; }

        public double DiskPercent { get; private set; }
        public string DiskText { get; private set; } = "";

        public TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

        public SystemStats()
        {
            NativeMethods.GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);
        }

        public void Update()
        {
            UpdateCpu();
            UpdateRam();
            UpdateBattery();
            UpdateNetwork();
            UpdateDisk();
        }

        private void UpdateCpu()
        {
            try
            {
                if (!NativeMethods.GetSystemTimes(out long idle, out long kernel, out long user)) return;
                long idleDiff = idle - _prevIdle;
                long total = (kernel - _prevKernel) + (user - _prevUser); // kernel 時間已包含 idle
                _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
                if (total > 0)
                    CpuPercent = Math.Clamp((1.0 - (double)idleDiff / total) * 100.0, 0, 100);
            }
            catch { }
        }

        private void UpdateRam()
        {
            try
            {
                var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
                if (NativeMethods.GlobalMemoryStatusEx(ref mem))
                {
                    RamTotalGb = mem.ullTotalPhys / 1024.0 / 1024 / 1024;
                    RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024.0 / 1024 / 1024;
                    RamPercent = RamTotalGb > 0 ? RamUsedGb / RamTotalGb * 100 : 0;
                }
            }
            catch { }
        }

        private void UpdateBattery()
        {
            try
            {
                var ps = Forms.SystemInformation.PowerStatus;
                var status = ps.BatteryChargeStatus;
                HasBattery = (status & Forms.BatteryChargeStatus.NoSystemBattery) == 0 &&
                             status != Forms.BatteryChargeStatus.Unknown &&
                             ps.BatteryLifePercent <= 1.0f;
                BatteryPercent = Math.Clamp(ps.BatteryLifePercent * 100.0, 0, 100);
                IsCharging = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
            }
            catch
            {
                HasBattery = false;
            }
        }

        private void UpdateNetwork()
        {
            try
            {
                long rx = 0, tx = 0;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var st = ni.GetIPStatistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }

                var now = DateTime.UtcNow;
                if (_prevNetTime != DateTime.MinValue)
                {
                    double secs = (now - _prevNetTime).TotalSeconds;
                    if (secs > 0.1)
                    {
                        DownBytesPerSec = Math.Max(0, (rx - _prevRx) / secs);
                        UpBytesPerSec = Math.Max(0, (tx - _prevTx) / secs);
                    }
                }
                _prevRx = rx; _prevTx = tx; _prevNetTime = now;
            }
            catch { }
        }

        private int _diskCounter;
        private void UpdateDisk()
        {
            if (_diskCounter++ % 10 != 0) return; // 每 10 次更新一次就好
            try
            {
                string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var d = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady &&
                        string.Equals(x.Name, sysRoot, StringComparison.OrdinalIgnoreCase));
                if (d == null) return;
                double total = d.TotalSize / 1024.0 / 1024 / 1024;
                double free = d.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                DiskPercent = total > 0 ? (total - free) / total * 100 : 0;
                DiskText = $"{d.Name.TrimEnd('\\')} 剩 {free:0} GB";
            }
            catch { }
        }

        public static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024) return $"{bytesPerSec / 1024 / 1024:0.0} MB/s";
            if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:0} KB/s";
            return $"{bytesPerSec:0} B/s";
        }
    }
}
