using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>
    /// 「下載」資料夾有新檔案下載完成時觸發 Completed。
    /// 瀏覽器下載時先寫 .crdownload／.part／.tmp，完成才改名，所以等檔名正常、大小 2 秒內不再變、檔案沒被鎖住才算完成。
    /// </summary>
    public sealed class DownloadWatcher : IDisposable
    {
        private static readonly string[] TempExt = { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!ut", ".aria2" };
        private FileSystemWatcher _fsw;
        private readonly Dictionary<string, DateTime> _pending = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _announced = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private Timer _timer;

        public event Action<string> Completed;
        public string Folder { get; private set; }

        public void Start()
        {
            if (_fsw != null) return;
            Folder = DownloadsFolder();
            if (Folder == null || !Directory.Exists(Folder)) { Log.Write("Downloads: folder not found"); return; }
            _fsw = new FileSystemWatcher(Folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            _fsw.Created += (s, e) => Touch(e.FullPath);
            _fsw.Renamed += (s, e) => Touch(e.FullPath);
            _fsw.Changed += (s, e) => { lock (_pending) if (_pending.ContainsKey(e.FullPath)) _pending[e.FullPath] = DateTime.UtcNow; };
            _fsw.EnableRaisingEvents = true;
            _timer = new Timer(_ => Check(), null, 1000, 1000);
            Log.Write("Downloads: watching " + Folder);
        }

        public void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            try { if (_fsw != null) { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); } } catch { }
            _timer = null;
            _fsw = null;
        }

        public void Dispose() => Stop();

        private void Touch(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string name = Path.GetFileName(path);
            if (TempExt.Contains(ext) || name.StartsWith("~$") || name.StartsWith(".") || name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) return;
            lock (_pending) _pending[path] = DateTime.UtcNow;
        }

        private void Check()
        {
            List<string> ready;
            lock (_pending)
            {
                ready = _pending.Where(kv => DateTime.UtcNow - kv.Value >= TimeSpan.FromSeconds(2)).Select(kv => kv.Key).ToList();
                foreach (var r in ready) _pending.Remove(r);
            }
            foreach (var path in ready)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var fi = new FileInfo(path);
                    if (fi.Length == 0) continue;
                    // 還在寫：晚一點再看
                    if (IsLocked(path)) { lock (_pending) _pending[path] = DateTime.UtcNow; continue; }
                    lock (_announced)
                    {
                        if (_announced.TryGetValue(path, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(1)) continue;
                        _announced[path] = DateTime.UtcNow;
                        if (_announced.Count > 200) _announced.Clear();
                    }
                    Completed?.Invoke(path);
                }
                catch (Exception ex) { Log.Error("Downloads.Check", ex); }
            }
        }

        private static bool IsLocked(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);   // 有人開著寫入就會失敗
                return false;
            }
            catch (IOException) { return true; }
            catch { return false; }
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        /// <summary>使用者的「下載」資料夾（可能被搬到別的磁碟）</summary>
        public static string DownloadsFolder()
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out IntPtr p) == 0)
                {
                    string s = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
