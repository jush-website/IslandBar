using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar.Services
{
    /// <summary>
    /// 程式圖示：開始功能表的 App（shell:AppsFolder\…）、捷徑、exe、資源回收筒都可以，保留透明背景。要在 UI 執行緒（STA）呼叫。
    /// </summary>
    public static class ShellIcons
    {
        private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

        /// <summary>依「路徑或 shell 名稱」拿圖示；拿不到傳回 null</summary>
        public static ImageSource Get(string parsingName, int size = 48)
        {
            if (string.IsNullOrWhiteSpace(parsingName)) return null;
            string key = parsingName + "|" + size;
            if (Cache.TryGetValue(key, out var hit)) return hit;
            ImageSource img = null;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out IShellItemImageFactory f);
                if (f != null)
                {
                    // 先只要圖示（不要縮圖），不夠大可以給大一點的
                    if (f.GetImage(new SIZE { cx = size, cy = size }, 0x4 | 0x1, out IntPtr hbm) == 0 && hbm != IntPtr.Zero)
                    {
                        try { img = FromHBitmap(hbm); }
                        finally { DeleteObject(hbm); }
                    }
                    Marshal.ReleaseComObject(f);
                }
            }
            catch { }
            if (Cache.Count > 400) Cache.Clear();
            Cache[key] = img;
            return img;
        }

        /// <summary>視窗自己的圖示（WM_GETICON → 類別圖示）</summary>
        public static ImageSource FromWindow(IntPtr hwnd)
        {
            try
            {
                IntPtr h = IntPtr.Zero;
                SendMessageTimeout(hwnd, 0x007F /* WM_GETICON */, (IntPtr)1 /* ICON_BIG */, IntPtr.Zero, 0x2, 100, out h);
                if (h == IntPtr.Zero) SendMessageTimeout(hwnd, 0x007F, (IntPtr)2 /* ICON_SMALL2 */, IntPtr.Zero, 0x2, 100, out h);
                if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, -14 /* GCLP_HICON */);
                if (h == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
        }

        /// <summary>HBITMAP（32 位元、有透明度）→ WPF 圖片。直接用 CreateBitmapSourceFromHBitmap 會把透明變成黑色</summary>
        private static BitmapSource FromHBitmap(IntPtr hbm)
        {
            var bm = new BITMAP();
            if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var bytes = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbm, 0, (uint)h, bytes, ref bi, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            // 沒有透明度資料（全部 0）時當作不透明
            bool anyAlpha = false;
            for (int i = 3; i < bytes.Length; i += 4) if (bytes[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bytes, w * 4);
            src.Freeze();
            return src;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr h, int idx);
    }

    /// <summary>一個有開著視窗的程式（同一個程式的視窗合在一起）</summary>
    public sealed class OpenApp
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string Aumid { get; set; } = "";
        public List<IntPtr> Windows { get; } = new List<IntPtr>();
        public string FirstTitle { get; set; } = "";
    }

    /// <summary>目前開著的程式視窗（跟工作列上看到的差不多），以及把某個程式叫到前面</summary>
    public static class OpenWindows
    {
        public static List<OpenApp> List(IntPtr[] ignore = null)
        {
            var apps = new List<OpenApp>();
            var byKey = new Dictionary<string, OpenApp>(StringComparer.OrdinalIgnoreCase);
            int self = Environment.ProcessId;
            EnumWindows((h, _) =>
            {
                try
                {
                    if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                    if (ignore != null && ignore.Contains(h)) return true;
                    long ex = GetWindowLongPtr(h, -20).ToInt64();
                    if ((ex & 0x80) != 0 /* TOOLWINDOW */ && (ex & 0x40000) == 0 /* APPWINDOW */) return true;
                    if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;   // 其他桌面、背景的 UWP
                    int len = GetWindowTextLength(h);
                    if (len == 0) return true;
                    var title = new StringBuilder(len + 1);
                    GetWindowText(h, title, title.Capacity);
                    string cls = ClassName(h);
                    if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return true;
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid == self) return true;
                    string exe = ProcessPath(pid);
                    string aumid = WindowAumid(h);
                    // UWP 的視窗外框是 ApplicationFrameHost，真正的程式在裡面
                    if (exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string inner = InnerUwpPath(h, pid);
                        if (inner.Length > 0) exe = inner;
                    }
                    string key = aumid.Length > 0 ? aumid : exe.Length > 0 ? exe : cls;
                    if (!byKey.TryGetValue(key, out var app))
                    {
                        app = new OpenApp { Key = key, ExePath = exe, Aumid = aumid, Name = FriendlyName(exe, title.ToString()), FirstTitle = title.ToString() };
                        byKey[key] = app;
                        apps.Add(app);
                    }
                    app.Windows.Add(h);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return apps;
        }

        /// <summary>把程式叫到前面：已經在前面就換下一個視窗；縮小的先還原</summary>
        public static void Activate(OpenApp app)
        {
            if (app == null || app.Windows.Count == 0) return;
            IntPtr fg = GetForegroundWindow();
            int i = app.Windows.IndexOf(fg);
            IntPtr target = i >= 0 ? app.Windows[(i + 1) % app.Windows.Count] : app.Windows[0];
            Activate(target);
        }

        public static void Activate(IntPtr h)
        {
            if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
            // Windows 不讓背景程式搶前景：先「按」一下 Alt 就可以
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(h);
        }

        public static ImageSource Icon(OpenApp app, int size = 48)
        {
            if (app.Aumid.Length > 0)
            {
                var a = ShellIcons.Get(@"shell:AppsFolder\" + app.Aumid, size);
                if (a != null) return a;
            }
            if (app.ExePath.Length > 0 && !app.ExePath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var e = ShellIcons.Get(app.ExePath, size);
                if (e != null) return e;
            }
            return app.Windows.Count > 0 ? ShellIcons.FromWindow(app.Windows[0]) : null;
        }

        private static string FriendlyName(string exe, string title)
        {
            try
            {
                if (exe.Length > 0 && File.Exists(exe))
                {
                    string d = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription;
                    if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
                    return Path.GetFileNameWithoutExtension(exe);
                }
            }
            catch { }
            return title;
        }

        private static string ClassName(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string ProcessPath(uint pid)
        {
            IntPtr p = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (p == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(p, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(p); }
        }

        private static string InnerUwpPath(IntPtr frame, uint framePid)
        {
            string found = "";
            EnumChildWindows(frame, (c, _) =>
            {
                GetWindowThreadProcessId(c, out uint pid);
                if (pid != framePid) { found = ProcessPath(pid); return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>視窗的 App 識別碼（AppUserModelID），UWP 與很多新程式都有，用來拿正確的圖示</summary>
        private static string WindowAumid(IntPtr h)
        {
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(h, ref iid, out IPropertyStore ps) != 0 || ps == null) return "";
                try
                {
                    var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                    var pv = new PROPVARIANT();
                    if (ps.GetValue(ref key, out pv) != 0) return "";
                    try { return pv.vt == 31 /* VT_LPWSTR */ && pv.p != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p) ?? "" : ""; }
                    finally { PropVariantClear(ref pv); }
                }
                finally { Marshal.ReleaseComObject(ps); }
            }
            catch { return ""; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Sequential)] private struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint c);
            [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
            [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
            [PreserveSig] int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
            [PreserveSig] int Commit();
        }

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder s, ref int size);
        [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ps);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);
    }

    /// <summary>Dock 上固定的程式：檔案總管、預設瀏覽器、資源回收筒，或開始功能表裡的任何 App</summary>
    public static class DockApps
    {
        public static string ExplorerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        /// <summary>預設瀏覽器的 exe（從 http 的預設程式找）</summary>
        public static string BrowserPath()
        {
            try
            {
                using var uc = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
                string progId = uc?.GetValue("ProgId") as string;
                if (!string.IsNullOrEmpty(progId))
                {
                    using var cmd = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
                    string c = cmd?.GetValue("") as string ?? "";
                    string exe = c.StartsWith("\"") ? c.Substring(1, Math.Max(0, c.IndexOf('"', 1) - 1)) : c.Split(' ').FirstOrDefault() ?? "";
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }
            string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
            return File.Exists(edge) ? edge : "";
        }

        /// <summary>捷徑（.lnk）指到的 exe</summary>
        public static string ShortcutTarget(string lnk)
        {
            try
            {
                if (!lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(lnk)) return "";
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return "";
                dynamic sh = Activator.CreateInstance(t);
                try { return (string)sh.CreateShortcut(lnk).TargetPath ?? ""; }
                finally { Marshal.FinalReleaseComObject(sh); }
            }
            catch { return ""; }
        }
    }
}
