using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>一個可以調亮度的螢幕</summary>
    public sealed class DisplayBrightness
    {
        public string Name { get; set; } = "";
        public bool Internal { get; set; }
        public int Min { get; set; }
        public int Max { get; set; } = 100;
        public int Current { get; set; }
        internal IntPtr Physical;
        public int Percent => Max > Min ? (int)Math.Round((Current - Min) * 100.0 / (Max - Min)) : Current;
    }

    /// <summary>
    /// 螢幕亮度：筆電內建螢幕用 WMI，外接螢幕用 DDC/CI（螢幕選單裡要開 DDC/CI）。
    /// 概念參考 Twinkle Tray、Monitorian（都是 MIT）。DDC/CI 很慢（一次幾十到上百毫秒），全部在背景執行緒做；
    /// 拖滑桿時只套用最新的值。
    /// </summary>
    public static class BrightnessService
    {
        private static List<DisplayBrightness> _cache = new List<DisplayBrightness>();
        private static DateTime _cachedAt = DateTime.MinValue;
        private static readonly object Lock = new object();
        private static readonly Dictionary<DisplayBrightness, int> Pending = new Dictionary<DisplayBrightness, int>();
        private static bool _applying;
        public static Func<bool> UseDdc { get; set; } = () => true;

        /// <summary>找可以調亮度的螢幕（慢：在背景執行緒呼叫）；fresh＝不用快取</summary>
        public static List<DisplayBrightness> Displays(bool fresh = false)
        {
            lock (Lock)
            {
                if (!fresh && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(60)) return _cache.ToList();
                foreach (var d in _cache) if (d.Physical != IntPtr.Zero) try { DestroyPhysicalMonitor(d.Physical); } catch { }
                var list = new List<DisplayBrightness>();
                // 內建螢幕（WMI）
                try
                {
                    using var q = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                    foreach (ManagementObject o in q.Get())
                        list.Add(new DisplayBrightness { Name = "內建螢幕", Internal = true, Min = 0, Max = 100, Current = Convert.ToInt32(o["CurrentBrightness"]) });
                }
                catch { }
                // 外接螢幕（DDC/CI）
                if (UseDdc())
                {
                    foreach (var m in Monitors.All())
                    {
                        try
                        {
                            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(m.Handle, out uint n) || n == 0) continue;
                            var arr = new PHYSICAL_MONITOR[n];
                            if (!GetPhysicalMonitorsFromHMONITOR(m.Handle, n, arr)) continue;
                            foreach (var pm in arr)
                            {
                                if (GetMonitorBrightness(pm.hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                                {
                                    string desc = (pm.szPhysicalMonitorDescription ?? "").Trim();
                                    string name = m.ShortLabel + (desc.Length > 0 && !desc.StartsWith("Generic", StringComparison.OrdinalIgnoreCase) ? " · " + desc : "");
                                    list.Add(new DisplayBrightness { Name = name, Min = (int)min, Max = (int)max, Current = (int)cur, Physical = pm.hPhysicalMonitor });
                                }
                                else DestroyPhysicalMonitor(pm.hPhysicalMonitor);
                            }
                        }
                        catch (Exception ex) { Log.Write("Brightness: " + m.ShortLabel + " " + ex.Message); }
                    }
                }
                _cache = list;
                _cachedAt = DateTime.UtcNow;
                return list.ToList();
            }
        }

        /// <summary>設定亮度（0～100）；在背景排隊，拖滑桿時只套用最後一個值</summary>
        public static void Set(DisplayBrightness d, int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            lock (Pending)
            {
                Pending[d] = percent;
                if (_applying) return;
                _applying = true;
            }
            Task.Run(() =>
            {
                while (true)
                {
                    KeyValuePair<DisplayBrightness, int> job;
                    lock (Pending)
                    {
                        if (Pending.Count == 0) { _applying = false; return; }
                        job = Pending.First();
                        Pending.Remove(job.Key);
                    }
                    Apply(job.Key, job.Value);
                }
            });
        }

        private static void Apply(DisplayBrightness d, int percent)
        {
            int value = d.Min + (int)Math.Round((d.Max - d.Min) * percent / 100.0);
            try
            {
                if (d.Internal)
                {
                    using var q = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                    foreach (ManagementObject o in q.Get()) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)value });
                }
                else if (d.Physical != IntPtr.Zero) SetMonitorBrightness(d.Physical, (uint)value);
                d.Current = value;
            }
            catch (Exception ex) { Log.Write("Brightness: 設定失敗 " + ex.Message); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }

        [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)] private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);
        [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint min, out uint cur, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint value);
    }
}
