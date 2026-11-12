using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    public sealed class CleanupOptions
    {
        public bool Memory { get; set; } = true;
        public bool Temp { get; set; } = true;
        public bool CrashDumps { get; set; } = true;
        public bool Dns { get; set; } = true;
        /// <summary>暫存檔要放多久沒動才清（小時）：正在用的暫存檔不會被清到</summary>
        public int TempMinAgeHours { get; set; } = 24;
    }

    public sealed class CleanupResult
    {
        public double RamPercentBefore { get; set; }
        public double RamPercentAfter { get; set; }
        public long RamUsedBefore { get; set; }
        public long RamUsedAfter { get; set; }
        public int TrimmedProcesses { get; set; }
        public long TempBytes { get; set; }
        public int TempFiles { get; set; }
        public long DumpBytes { get; set; }
        public int DumpFiles { get; set; }
        public bool DnsFlushed { get; set; }
        public long RamFreed => Math.Max(0, RamUsedBefore - RamUsedAfter);
        public long DiskFreed => TempBytes + DumpBytes;
    }

    /// <summary>
    /// 一鍵加速（像手機的加速工具），只做不會動到個人資料的事：
    /// 1. 整理記憶體：請各程式把暫時用不到的記憶體交還給系統（EmptyWorkingSet；程式要用時會自己再拿回來，不會關掉任何程式）
    /// 2. 清暫存檔：只清 %TEMP% 裡超過一天沒動過的檔案；正在使用、被鎖住的會略過
    /// 3. 清當機傾印與錯誤報告（%LocalAppData%\CrashDumps、WER）：只是除錯用的檔案
    /// 4. 清 DNS 快取
    /// 不碰：文件、桌面、下載、資源回收筒、瀏覽器資料、系統資料夾。不需要系統管理員權限。
    /// </summary>
    public static class CleanupService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet")] private static extern bool EmptyWorkingSet(IntPtr process);
        [DllImport("dnsapi.dll")] private static extern bool DnsFlushResolverCache();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_SET_QUOTA = 0x0100;

        public static (double percent, long used, long total) Memory()
        {
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
            long total = (long)m.TotalPhys, used = (long)(m.TotalPhys - m.AvailPhys);
            return (total > 0 ? used * 100.0 / total : 0, used, total);
        }

        public static async Task<CleanupResult> RunAsync(CleanupOptions o, IProgress<string> progress = null)
        {
            var r = new CleanupResult();
            var before = Memory();
            r.RamPercentBefore = before.percent;
            r.RamUsedBefore = before.used;

            await Task.Run(() =>
            {
                if (o.Memory) { progress?.Report("整理記憶體…"); r.TrimmedProcesses = TrimWorkingSets(); }
                if (o.Temp)
                {
                    progress?.Report("清理暫存檔…");
                    var (files, bytes) = CleanFolder(Path.GetTempPath(), TimeSpan.FromHours(Math.Max(1, o.TempMinAgeHours)), null, keepRoot: true);
                    r.TempFiles = files; r.TempBytes = bytes;
                }
                if (o.CrashDumps)
                {
                    progress?.Report("清理當機記錄…");
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    foreach (var dir in new[]
                    {
                        Path.Combine(local, "CrashDumps"),
                        Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"),
                        Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue")
                    })
                    {
                        var (files, bytes) = CleanFolder(dir, TimeSpan.FromHours(1), null, keepRoot: true);
                        r.DumpFiles += files; r.DumpBytes += bytes;
                    }
                }
                if (o.Dns)
                {
                    try { r.DnsFlushed = DnsFlushResolverCache(); } catch { }
                }
            });

            // 讓系統把交回來的記憶體算進「可用」再量
            await Task.Delay(1500);
            var after = Memory();
            r.RamPercentAfter = after.percent;
            r.RamUsedAfter = after.used;
            Log.Write($"Cleanup: RAM {r.RamPercentBefore:0}% → {r.RamPercentAfter:0}%（{r.TrimmedProcesses} 個程式）、暫存 {r.TempFiles} 個 {r.TempBytes / 1048576.0:0.0} MB、" +
                      $"當機記錄 {r.DumpFiles} 個 {r.DumpBytes / 1048576.0:0.0} MB、DNS {(r.DnsFlushed ? "已清" : "略過")}");
            return r;
        }

        /// <summary>只整理記憶體（自動模式用）</summary>
        public static Task<CleanupResult> TrimMemoryAsync() =>
            RunAsync(new CleanupOptions { Memory = true, Temp = false, CrashDumps = false, Dns = false });

        /// <summary>
        /// 請每個程式把暫時用不到的記憶體交回系統。只動得到目前使用者自己的程式；前景視窗（正在用的程式、遊戲）不動，避免卡一下。
        /// </summary>
        private static int TrimWorkingSets()
        {
            int count = 0;
            uint fgPid = 0;
            try { GetWindowThreadProcessId(GetForegroundWindow(), out fgPid); } catch { }
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id <= 4 || p.Id == fgPid) continue;
                        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, p.Id);
                        if (h == IntPtr.Zero) continue;   // 系統或其他使用者的程式：沒有權限就略過
                        try { if (EmptyWorkingSet(h)) count++; }
                        finally { CloseHandle(h); }
                    }
                    catch { }
                }
            }
            return count;
        }

        /// <summary>
        /// 刪掉資料夾裡超過 minAge 沒修改的檔案（不跟著 junction／捷徑走到別的地方），刪不掉的（正在使用）就略過。
        /// keepRoot：資料夾本身留著，只清裡面。
        /// </summary>
        private static (int files, long bytes) CleanFolder(string dir, TimeSpan minAge, HashSet<string> skip, bool keepRoot)
        {
            int files = 0;
            long bytes = 0;
            try
            {
                var di = new DirectoryInfo(dir);
                if (!di.Exists || di.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (0, 0);
                var cutoff = DateTime.UtcNow - minAge;
                foreach (var f in di.EnumerateFiles())
                {
                    try
                    {
                        if (f.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        if (f.LastWriteTimeUtc > cutoff || f.CreationTimeUtc > cutoff) continue;
                        long len = f.Length;
                        if (f.IsReadOnly) f.IsReadOnly = false;
                        f.Delete();
                        files++;
                        bytes += len;
                    }
                    catch { }   // 正在使用、沒權限：略過
                }
                foreach (var sub in di.EnumerateDirectories())
                {
                    try
                    {
                        if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        var (f2, b2) = CleanFolder(sub.FullName, minAge, skip, keepRoot: false);
                        files += f2;
                        bytes += b2;
                    }
                    catch { }
                }
                if (!keepRoot)
                {
                    try
                    {
                        di.Refresh();
                        if (!di.EnumerateFileSystemInfos().Any() && di.LastWriteTimeUtc < cutoff) di.Delete();
                    }
                    catch { }
                }
            }
            catch { }
            return (files, bytes);
        }

        public static string Size(long b) =>
            b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : b > 0 ? $"{Math.Max(1, b / 1024)} KB" : "0";
    }
}
