using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IslandBar.Conversion;
using IslandBar.Services.Voice;

namespace IslandBar.Services
{
    /// <summary>擷取到的畫面：圖、是哪個程式／視窗</summary>
    public sealed class ScreenCapture
    {
        public byte[] Png { get; set; }
        public string App { get; set; } = "";
        public string Title { get; set; } = "";
        public bool WholeScreen { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>給人看的：「Chrome — 某某新聞」</summary>
        public string Describe()
        {
            if (WholeScreen && string.IsNullOrWhiteSpace(Title)) return "整個螢幕";
            string t = (Title ?? "").Trim();
            if (t.Length > 60) t = t.Substring(0, 60) + "…";
            if (string.IsNullOrEmpty(App)) return t;
            if (string.IsNullOrEmpty(t) || t.Equals(App, StringComparison.OrdinalIgnoreCase)) return App;
            return $"{App} — {t}";
        }
    }

    /// <summary>
    /// 畫面重點摘要：擷取目前的視窗（或整個螢幕）→ Windows 內建的文字辨識（OCR）→ 交給 PAIR 或本機小模型整理重點。
    /// 圖片不會傳出去，只傳辨識出來的文字；用 PAIR／本機模型時資料都不出你的電腦／叢集。
    /// </summary>
    public static class ScreenSummary
    {
        public const string SystemPrompt =
            "你是閱讀助手，幫使用者快速看懂他螢幕上的內容。使用者會給你用 OCR 從螢幕擷取的文字，可能有錯字、順序有點亂，" +
            "也會混著選單、按鈕、分頁標題、廣告等介面文字，請忽略這些介面文字，只整理真正的內容。\n" +
            "一律用繁體中文（台灣用語）回答，格式固定：\n" +
            "第一行：一句話說這是什麼（例如「一封客戶詢問報價的信」「關於某某的新聞」「Python 程式的錯誤訊息」），不要加標題或符號。\n" +
            "接著 3 到 6 點重點，每點一行、以「• 」開頭，每點一句話、具體（有數字、日期、人名就寫出來）。\n" +
            "如果內容裡明確寫到需要使用者處理的事（截止日期、錯誤原因、要回覆的問題、要付的錢），最後加一行以「⚠ 」開頭說明；沒有就不要寫。" +
            "不要評論畫面本身、不要猜測日期或設定對不對。\n" +
            "不要用 Markdown 粗體或標題，不要說「以下是摘要」之類的開場白，不要編造畫面上沒有的內容。";

        /// <summary>
        /// 擷取目前前景的視窗；前景是桌面、工作列、IslandBar 自己，或視窗太小時，改抓整個主螢幕。
        /// </summary>
        public static ScreenCapture Capture(bool wholeScreen, IntPtr[] ignore, IntPtr target = default)
        {
            IntPtr fg = target != IntPtr.Zero ? target : GetForegroundWindow();
            string cls = fg == IntPtr.Zero ? "" : ClassName(fg);
            bool useScreen = wholeScreen || fg == IntPtr.Zero || (ignore != null && ignore.Contains(fg)) ||
                             cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
            Rectangle r = Rectangle.Empty;
            if (!useScreen)
            {
                // DWM 的外框（不含陰影）比 GetWindowRect 準
                if (DwmGetWindowAttribute(fg, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT rc, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(fg, out rc))
                    useScreen = true;
                else
                {
                    r = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
                    var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                    r.Intersect(vs);
                    if (r.Width < 200 || r.Height < 120) useScreen = true;
                }
            }
            var cap = new ScreenCapture { WholeScreen = useScreen };
            if (useScreen) r = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            else
            {
                cap.Title = WindowTitle(fg);
                cap.App = AppName(fg);
            }
            using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            cap.Png = ms.ToArray();
            cap.Width = r.Width;
            cap.Height = r.Height;
            return cap;
        }

        /// <summary>OCR 結果整理：去掉空白行、重複行、太短的雜訊行</summary>
        public static string CleanText(string text)
        {
            var lines = (text ?? "").Replace("\r", "").Split('\n')
                .Select(l => Regex.Replace(l, @"\s+", " ").Trim())
                .Where(l => l.Length >= 2)
                .ToList();
            var sb = new StringBuilder();
            string prev = null;
            foreach (var l in lines)
            {
                if (l == prev) continue;
                sb.AppendLine(l);
                prev = l;
            }
            return sb.ToString().Trim();
        }

        /// <summary>給 AI 的內容：哪個程式、視窗標題、畫面上的文字（依模型大小截短）</summary>
        public static string BuildPrompt(ScreenCapture cap, string text, bool local, out bool truncated)
        {
            int max = local ? 2000 : 9000;   // 本機小模型的上下文只有 4096 個 token
            truncated = text.Length > max;
            if (truncated) text = text.Substring(0, max);
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(cap.App)) sb.AppendLine("程式：" + cap.App);
            if (!string.IsNullOrWhiteSpace(cap.Title)) sb.AppendLine("視窗標題：" + cap.Title);
            sb.AppendLine(cap.WholeScreen ? "範圍：整個螢幕" : "範圍：目前的視窗");
            sb.AppendLine();
            sb.AppendLine("畫面上的文字：");
            sb.AppendLine(text);
            if (truncated) sb.AppendLine("（文字太長，後面省略）");
            return sb.ToString();
        }

        public const string TranslateSystemPrompt =
            "你是專業翻譯。使用者會給你用 OCR 從螢幕擷取的文字，可能有錯字、順序有點亂，也會混著選單、按鈕、分頁標題、廣告等介面文字：" +
            "忽略這些介面文字，只翻譯真正的內容。\n" +
            "規則：照使用者指定的方向翻譯；保留原本的段落、條列與換行；人名、產品名、程式碼、網址、檔名、數字保持原樣；" +
            "OCR 明顯的錯字依上下文修正後再翻。譯文要自然流暢，像母語的人寫的。只輸出譯文，不要解釋、不要開場白、不要附原文、不要用 Markdown。";

        /// <summary>畫面上主要是中文嗎？（中文字比英文字母多）</summary>
        public static bool MostlyChinese(string text)
        {
            int cjk = 0, latin = 0;
            foreach (char c in text ?? "")
            {
                if (c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF) cjk++;
                else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
            }
            // 一個中文字大約等於一個英文單字（約 5 個字母）
            return cjk * 5 >= latin;
        }

        /// <summary>給 AI 的翻譯內容：中文→英文、其他→繁體中文。翻譯的輸出跟輸入差不多長，所以本機模型能看的字比摘要少</summary>
        public static string BuildTranslatePrompt(ScreenCapture cap, string text, bool local, out bool truncated, out bool toEnglish)
        {
            toEnglish = MostlyChinese(text);
            int max = local ? 1200 : 6000;
            truncated = text.Length > max;
            if (truncated) text = text.Substring(0, max);
            var sb = new StringBuilder();
            sb.AppendLine(toEnglish ? "請把下面的內容翻成英文。" : "請把下面的內容翻成繁體中文（台灣用語）。");
            if (!string.IsNullOrWhiteSpace(cap.Title)) sb.AppendLine("（視窗標題：" + cap.Title + "，只是參考，不用翻）");
            sb.AppendLine();
            sb.AppendLine(text);
            return sb.ToString();
        }

        /// <summary>整套流程（測試也用這個）：OCR → 找大腦 → 串流摘要</summary>
        public static async Task<string> OcrAsync(ScreenCapture cap) => CleanText(await OcrOps.RecognizeAsync(cap.Png));

        // ---------------- Win32 ----------------

        private static string WindowTitle(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string ClassName(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>程式的顯示名稱（「Google Chrome」），拿不到就用程序名稱</summary>
        private static string AppName(IntPtr h)
        {
            try
            {
                GetWindowThreadProcessId(h, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                try
                {
                    string desc = p.MainModule?.FileVersionInfo?.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
                }
                catch { }   // 系統管理員權限的程式讀不到
                return p.ProcessName;
            }
            catch { return ""; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    }
}
