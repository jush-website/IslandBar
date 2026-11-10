using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandBar.Linux
{
    /// <summary>登入自動啟動（~/.config/autostart）、開啟網址、檢查新版</summary>
    public static class Desktop
    {
        public const string Repo = "jush-website/IslandBar";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
        }

        private static string AutostartFile
        {
            get
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                string baseDir = !string.IsNullOrEmpty(xdg) ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return Path.Combine(baseDir, "autostart", "islandbar.desktop");
            }
        }

        public static bool AutostartEnabled => File.Exists(AutostartFile);

        public static void SetAutostart(bool on)
        {
            try
            {
                if (!on)
                {
                    if (File.Exists(AutostartFile)) File.Delete(AutostartFile);
                    return;
                }
                string exe = Environment.ProcessPath ?? "islandbar";
                // .deb 安裝時用 /usr/bin/islandbar（升級後路徑不變）
                if (exe.StartsWith("/usr/lib/islandbar/") && File.Exists("/usr/bin/islandbar")) exe = "/usr/bin/islandbar";
                Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile));
                File.WriteAllText(AutostartFile,
                    "[Desktop Entry]\n" +
                    "Type=Application\n" +
                    "Name=IslandBar\n" +
                    "Comment=Dynamic Island system monitor\n" +
                    "Comment[zh_TW]=動態島系統監控\n" +
                    $"Exec=\"{exe}\"\n" +
                    "Icon=islandbar\n" +
                    "Terminal=false\n" +
                    "X-GNOME-Autostart-enabled=true\n" +
                    "X-GNOME-Autostart-Delay=5\n");
            }
            catch (Exception ex) { Log.Error("Autostart", ex); }
        }

        public static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); }
            catch (Exception ex) { Log.Error("Open " + url, ex); }
        }

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("IslandBar-Linux/" + Version);
            return h;
        }

        /// <summary>GitHub 上比目前新的版本號；沒有就回傳 null</summary>
        public static async Task<string> CheckLatestAsync()
        {
            try
            {
                using var resp = await Http.GetAsync("https://api.github.com/repos/" + Repo + "/releases/latest");
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                string v = tag.TrimStart('v', 'V');
                if (System.Version.TryParse(v, out var latest) && System.Version.TryParse(Version, out var cur) && latest > cur) return v;
            }
            catch (Exception ex) { Log.Error("Update check", ex); }
            return null;
        }
    }
}
