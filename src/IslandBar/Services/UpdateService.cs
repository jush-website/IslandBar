using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    public sealed class UpdateInfo
    {
        public Version Version { get; set; }
        public string Tag { get; set; } = "";
        public string MsiUrl { get; set; }
        public string PageUrl { get; set; } = "";
    }

    /// <summary>從 GitHub Releases 檢查 / 下載 / 安裝新版本</summary>
    public static class UpdateService
    {
        public const string Repo = "jush-website/IslandBar";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";

        private static readonly HttpClient ApiClient = CreateClient(TimeSpan.FromSeconds(20));
        private static readonly HttpClient DownloadClient = CreateClient(TimeSpan.FromMinutes(15));

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var c = new HttpClient { Timeout = timeout };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("IslandBar-Updater/1.0");
            return c;
        }

        public static Version CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>是不是透過 MSI 安裝的（%LocalAppData%\Programs\IslandBar）</summary>
        public static bool IsInstalled
        {
            get
            {
                try
                {
                    string exe = Environment.ProcessPath ?? "";
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "IslandBar");
                    return exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        /// <summary>取得最新 Release；失敗回傳 null</summary>
        public static async Task<UpdateInfo> GetLatestAsync()
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await ApiClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;

                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tag = root.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;

                var info = new UpdateInfo
                {
                    Tag = tag,
                    Version = new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build)),
                    PageUrl = root.TryGetProperty("html_url", out var page) ? page.GetString() ?? ReleasesPage : ReleasesPage
                };

                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        {
                            info.MsiUrl = a.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }
                return info;
            }
            catch
            {
                return null;
            }
        }

        public static async Task<string> DownloadAsync(UpdateInfo info)
        {
            string path = Path.Combine(Path.GetTempPath(), $"IslandBar-Setup-{info.Version}.msi");
            string tmp = path + ".part";

            using (var resp = await DownloadClient.GetAsync(info.MsiUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                using var src = await resp.Content.ReadAsStreamAsync();
                using var dst = File.Create(tmp);
                await src.CopyToAsync(dst);
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        /// <summary>以「只顯示進度列」模式執行安裝程式（安裝完會自動重新啟動 IslandBar）</summary>
        public static void RunInstaller(string msiPath)
        {
            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\" /passive /norestart")
            {
                UseShellExecute = true
            });
        }

        public static void OpenReleasesPage(string url = null)
        {
            try { Process.Start(new ProcessStartInfo(url ?? ReleasesPage) { UseShellExecute = true }); } catch { }
        }
    }
}
