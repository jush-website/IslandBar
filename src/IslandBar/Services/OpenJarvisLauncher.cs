using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>
    /// OpenJarvis（https://github.com/open-jarvis/OpenJarvis，Apache-2.0）：史丹佛做的本機 AI 框架。
    /// IslandBar 不內建它（它是 Python），只負責：找到裝好的 jarvis、在背景執行「jarvis serve」（OpenAI 相容 API，預設 127.0.0.1:8000），
    /// 然後 AI 大腦／畫面摘要就會自動改用它。只關掉自己開的那一個。
    /// </summary>
    public static class OpenJarvisLauncher
    {
        public const string DefaultUrl = "http://127.0.0.1:8000";
        private static Process _proc;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        /// <summary>官方 Windows 安裝程式放的 jarvis.cmd（%LOCALAPPDATA%\OpenJarvis\bin），找不到就看 PATH</summary>
        public static string FindJarvis()
        {
            try
            {
                string shim = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenJarvis", "bin", "jarvis.cmd");
                if (File.Exists(shim)) return shim;
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    foreach (var name in new[] { "jarvis.cmd", "jarvis.exe", "jarvis.bat" })
                    {
                        string p = Path.Combine(dir.Trim().Trim('"'), name);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        public static bool Installed => FindJarvis() != null;

        /// <summary>這次是 IslandBar 開的、而且還在跑</summary>
        public static bool StartedByUs => _proc != null && !_proc.HasExited;

        public static async Task<bool> IsRunningAsync(string baseUrl, CancellationToken ct = default)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Voice.Brain.NormalizeUrl(baseUrl) + "/health");
                using var r = await Http.SendAsync(req, ct);
                return r.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>在背景執行 jarvis serve（沒有黑色視窗；輸出寫到 openjarvis.log）。傳回錯誤訊息，成功傳回 null</summary>
        public static async Task<string> StartAsync(string baseUrl)
        {
            if (await IsRunningAsync(baseUrl)) return null;
            if (StartedByUs) return null;
            string jarvis = FindJarvis();
            if (jarvis == null) return "找不到 OpenJarvis（jarvis 指令）。請先照官方說明安裝。";
            int port = 8000;
            try { port = new Uri(Voice.Brain.NormalizeUrl(baseUrl)).Port; } catch { }
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/d /c \"\"{jarvis}\" serve --port {port}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(jarvis) ?? Environment.CurrentDirectory
                };
                // uv 裝在 %USERPROFILE%\.local\bin：IslandBar 開機時就啟動，PATH 可能還沒有它
                string uvDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
                psi.Environment["PATH"] = uvDir + ";" + (psi.Environment.TryGetValue("PATH", out var path) ? path : "");
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.Environment["NO_COLOR"] = "1";
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                string logPath = LogPath;
                try { if (File.Exists(logPath) && new FileInfo(logPath).Length > 2_000_000) File.Delete(logPath); } catch { }
                void Append(string line) { if (line == null) return; try { lock (LogLock) File.AppendAllText(logPath, line + Environment.NewLine); } catch { } }
                p.OutputDataReceived += (s, e) => Append(e.Data);
                p.ErrorDataReceived += (s, e) => Append(e.Data);
                p.Exited += (s, e) => Log.Write($"OpenJarvis: jarvis serve 結束（{SafeExit(p)}）");
                Append($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} IslandBar 啟動 jarvis serve --port {port} =====");
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                try { p.StandardInput.Close(); } catch { }
                _proc = p;
                Log.Write("OpenJarvis: 啟動 jarvis serve，port " + port);
                // 第一次啟動要載入模型、套件：最多等 90 秒
                for (int i = 0; i < 90; i++)
                {
                    await Task.Delay(1000);
                    if (p.HasExited) return "OpenJarvis 啟動失敗（詳細內容在 openjarvis.log）";
                    if (await IsRunningAsync(baseUrl)) return null;
                }
                return "OpenJarvis 還在啟動中，稍等一下再看看";
            }
            catch (Exception ex)
            {
                Log.Error("OpenJarvis.Start", ex);
                return "OpenJarvis 啟動失敗：" + ex.Message;
            }
        }

        /// <summary>關掉 IslandBar 自己開的 jarvis serve（使用者自己開的不動）</summary>
        public static void Stop()
        {
            var p = _proc;
            _proc = null;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }

        public static string LogPath => Path.Combine(Path.GetDirectoryName(Log.FilePath) ?? Path.GetTempPath(), "openjarvis.log");
        private static readonly object LogLock = new object();

        private static string SafeExit(Process p) { try { return "代碼 " + p.ExitCode; } catch { return "?"; } }
    }
}
