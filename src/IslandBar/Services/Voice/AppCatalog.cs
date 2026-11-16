using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services.Voice
{
    /// <summary>
    /// 電腦上所有的應用程式：跟開始功能表「所有應用程式」同一份清單（shell:AppsFolder，包含 Microsoft Store 的 App），
    /// 再加上桌面、開始功能表裡的捷徑。用名字（含模糊比對、別名、常見的語音辨識同音字）找程式。
    /// </summary>
    public static class AppCatalog
    {
        public sealed class AppEntry
        {
            public string Name { get; init; }
            public string Norm { get; init; }
            /// <summary>怎麼打開：shell:AppsFolder\ID（Store App 和一般程式都能用），或捷徑檔路徑</summary>
            public string Launch { get; init; }
            public string Source { get; init; }
        }

        private static List<AppEntry> _apps;
        private static DateTime _loadedAt;
        private static readonly object Lock = new object();
        private static Task _loading;

        /// <summary>語音辨識常把英文程式名聽成中文：常見的對照（使用者也可以在設定加自己的叫法）</summary>
        public static readonly Dictionary<string, string> BuiltInNicknames = new Dictionary<string, string>
        {
            ["賴"] = "LINE", ["來"] = "LINE", ["萊"] = "LINE", ["萊恩"] = "LINE", ["賴恩"] = "LINE",
            ["迪斯可"] = "Discord", ["低斯扣"] = "Discord", ["dc"] = "Discord",
            ["微信"] = "WeChat", ["電報"] = "Telegram",
            ["vscode"] = "Visual Studio Code", ["code"] = "Visual Studio Code",
            ["chrome"] = "Google Chrome", ["谷歌瀏覽器"] = "Google Chrome", ["火狐"] = "Firefox",
            ["word"] = "Word", ["excel"] = "Excel", ["ppt"] = "PowerPoint", ["簡報"] = "PowerPoint",
            ["teams"] = "Microsoft Teams", ["obs"] = "OBS Studio", ["vlc"] = "VLC media player",
            ["ps"] = "Photoshop", ["pr"] = "Premiere Pro", ["ae"] = "After Effects",
            ["小黑"] = "Steam", ["蒸氣"] = "Steam", ["思播"] = "Spotify",
            ["網易雲"] = "網易雲音樂", ["酷狗"] = "酷狗音樂",
        };

        public static string Norm(string s) =>
            Regex.Replace((s ?? "").ToLowerInvariant(), @"[\s\-_.,，。、！？!?'""’「」()（）:：;；~～…®™]+", "");

        /// <summary>背景先載一次（第一次說「打開…」時就不用等）</summary>
        public static void Preload()
        {
            lock (Lock)
            {
                if (_apps != null || _loading != null) return;
                _loading = Task.Run(() => { try { All(); } catch { } finally { _loading = null; } });
            }
        }

        public static List<AppEntry> All(bool refresh = false)
        {
            lock (Lock)
            {
                if (!refresh && _apps != null && DateTime.UtcNow - _loadedAt < TimeSpan.FromMinutes(10)) return _apps;
            }
            var list = new List<AppEntry>();
            try { list.AddRange(LoadAppsFolder()); }
            catch (Exception ex) { Log.Write("Voice: 讀不到所有應用程式清單：" + ex.Message); }
            var names = new HashSet<string>(list.Select(a => a.Norm));
            foreach (var s in LoadShortcuts()) if (names.Add(s.Norm)) list.Add(s);
            lock (Lock)
            {
                _apps = list;
                _loadedAt = DateTime.UtcNow;
            }
            Log.Write($"Voice: 找到 {list.Count} 個應用程式");
            return list;
        }

        private static bool Junk(string norm) =>
            norm.Length == 0 || Regex.IsMatch(norm, "uninstall|解除安裝|移除|readme|說明文件|license|授權|website|網站$|releasenotes|help$");

        /// <summary>開始功能表「所有應用程式」（shell:AppsFolder），要在 STA 執行緒上讀</summary>
        private static List<AppEntry> LoadAppsFolder()
        {
            var list = new List<AppEntry>();
            Exception err = null;
            var t = new Thread(() =>
            {
                object shell = null;
                try
                {
                    var type = Type.GetTypeFromProgID("Shell.Application");
                    if (type == null) return;
                    shell = Activator.CreateInstance(type);
                    dynamic sh = shell;
                    dynamic folder = sh.NameSpace("shell:AppsFolder");
                    if (folder == null) return;
                    dynamic items = folder.Items();
                    int n = items.Count;
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            dynamic it = items.Item(i);
                            string name = it.Name;
                            string path = it.Path;
                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                            string norm = Norm(name);
                            if (Junk(norm)) continue;
                            // 網址捷徑（http…）不算程式
                            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                            list.Add(new AppEntry { Name = name, Norm = norm, Launch = @"shell:AppsFolder\" + path, Source = "apps" });
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { err = ex; }
                finally { if (shell != null) try { Marshal.FinalReleaseComObject(shell); } catch { } }
            }) { IsBackground = true, Name = "AppsFolder" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(15000);
            if (err != null) throw err;
            return list;
        }

        private static List<AppEntry> LoadShortcuts()
        {
            var list = new List<AppEntry>();
            foreach (var dir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            })
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".lnk" && ext != ".appref-ms" && ext != ".url") continue;
                        string name = Path.GetFileNameWithoutExtension(f);
                        string norm = Norm(name);
                        if (Junk(norm)) continue;
                        list.Add(new AppEntry { Name = name, Norm = norm, Launch = f, Source = "shortcut" });
                    }
                }
                catch { }
            }
            return list;
        }

        /// <summary>自訂叫法：「賴=LINE, 小黑=Steam」（逗號、分號或換行分開）</summary>
        public static Dictionary<string, string> ParseNicknames(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (var part in (text ?? "").Split(new[] { ',', '，', ';', '；', '\n', '\r', '、' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOfAny(new[] { '=', '＝', ':', '：' });
                if (eq <= 0) continue;
                string k = Norm(part.Substring(0, eq)), v = part.Substring(eq + 1).Trim();
                if (k.Length > 0 && v.Length > 0) d[k] = v;
            }
            return d;
        }

        /// <summary>
        /// 用名字找程式。先看自訂叫法、內建同音字，再比對名稱：完全一樣 ＞ 開頭一樣 ＞ 某個字開頭 ＞ 包含 ＞ 很像（打錯字、聽錯一兩個字也找得到）。
        /// </summary>
        public static AppEntry Find(string query, string userNicknames)
        {
            string q = Norm(query);
            if (q.Length == 0) return null;
            var candidates = new List<string> { q };
            var user = ParseNicknames(userNicknames);
            if (user.TryGetValue(q, out var u)) candidates.Insert(0, Norm(u));
            else if (BuiltInNicknames.TryGetValue(q, out var b)) candidates.Insert(0, Norm(b));

            var apps = All();
            foreach (var c in candidates)
            {
                var best = apps.Select(a => (a, score: Score(a, c)))
                               .Where(x => x.score >= 55)
                               .OrderByDescending(x => x.score)
                               .ThenBy(x => x.a.Source == "apps" ? 0 : 1)
                               .ThenBy(x => x.a.Norm.Length)
                               .FirstOrDefault();
                if (best.a != null) return best.a;
            }
            return null;
        }

        private static int Score(AppEntry a, string q)
        {
            string n = a.Norm;
            if (n == q) return 100;
            if (n.StartsWith(q, StringComparison.Ordinal)) return 90 - Math.Min(15, n.Length - q.Length);
            // 某個英文單字開頭符合，例如「studio code」→「Visual Studio Code」
            var words = Regex.Split(a.Name.ToLowerInvariant(), @"[\s\-_.]+").Where(w => w.Length > 0).ToList();
            for (int i = 1; i < words.Count; i++)
                if (string.Concat(words.Skip(i)).StartsWith(q, StringComparison.Ordinal)) return 82;
            if (q.Length >= 2 && n.Contains(q, StringComparison.Ordinal)) return 72 - Math.Min(12, (n.Length - q.Length) / 2);
            if (q.Length >= 4 && q.StartsWith(n, StringComparison.Ordinal) && n.Length >= 3) return 66;
            // 很像：英文用編輯距離，中文看共同字元
            if (q.Length >= 3)
            {
                double sim = Similarity(n, q);
                if (sim >= 0.72) return (int)(50 + sim * 20);
            }
            return 0;
        }

        private static double Similarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            bool cjk = Regex.IsMatch(a + b, @"\p{IsCJKUnifiedIdeographs}");
            if (cjk)
            {
                int common = b.Count(ch => a.Contains(ch));
                return common / (double)Math.Max(a.Length, b.Length);
            }
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return 1 - d[a.Length, b.Length] / (double)Math.Max(a.Length, b.Length);
        }
    }
}
