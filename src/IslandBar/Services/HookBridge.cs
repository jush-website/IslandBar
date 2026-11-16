using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>Claude Code / Codex 傳來的事件</summary>
    public sealed class AgentEvent
    {
        public string Source { get; set; } = "";   // claude / codex
        public string Kind { get; set; } = "";     // stop / notification / turn-complete
        public string Folder { get; set; } = "";   // 專案資料夾名稱
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// Claude Code 的 hooks 與 Codex 的 notify 會執行「IslandBar.exe --hook claude|codex」，
    /// 這個短命的程式把事件透過具名管道轉給常駐的 IslandBar，然後馬上結束。
    /// </summary>
    public static class HookBridge
    {
        private static string PipeName => "IslandBar_Hook_" + Environment.UserName;

        // ---------------- 短命的 --hook 程式 ----------------

        public static void RunClient(string[] args)
        {
            try
            {
                string source = args.Length > 1 ? args[1].ToLowerInvariant() : "claude";
                var ev = new AgentEvent { Source = source };

                if (source == "codex")
                {
                    // Codex 把 JSON 當成最後一個參數傳進來
                    string json = args.Length > 2 ? args[args.Length - 1] : "{}";
                    ParseCodex(json, ev);
                }
                else
                {
                    // Claude Code 把 JSON 從 stdin 傳進來
                    string json = ReadStdin(TimeSpan.FromSeconds(2));
                    ParseClaude(json, ev);
                }

                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(1500);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ev) + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
            }
            catch (Exception ex)
            {
                Log.Error("HookClient", ex);
            }
        }

        // ---------------- Claude Code 狀態列：IslandBar.exe --statusline ----------------

        /// <summary>
        /// Claude Code 更新狀態列時執行：從 stdin 讀 JSON，把 rate_limits（5 小時／每週額度）存起來給動態島用，
        /// 再輸出一行狀態列文字（模型 · 5h % · 7d %）。不需要讀 Claude 的登入憑證。
        /// </summary>
        public static void RunStatusLine()
        {
            string json = ReadStdin(TimeSpan.FromSeconds(2));
            string line = "IslandBar";
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var r = doc.RootElement;
                string model = r.TryGetProperty("model", out var m) ? (Str(m, "display_name") ?? Str(m, "id") ?? "") : "";
                var parts = new List<string>();
                if (model.Length > 0) parts.Add(model);
                if (r.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
                {
                    var o = new JsonObject { ["updatedAt"] = DateTimeOffset.Now.ToString("o") };
                    foreach (var p in rl.EnumerateObject()) o[p.Name] = JsonNode.Parse(p.Value.GetRawText());
                    string file = QuotaService.StatusLineFile;
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    string tmp = file + "." + Environment.ProcessId + ".tmp";
                    File.WriteAllText(tmp, o.ToJsonString());
                    File.Move(tmp, file, true);
                    foreach (var (key, label) in new[] { ("five_hour", "5h"), ("seven_day", "7d") })
                        if (rl.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Object &&
                            w.TryGetProperty("used_percentage", out var pct) && pct.ValueKind == JsonValueKind.Number)
                            parts.Add($"{label} {pct.GetDouble():0}%");
                }
                if (parts.Count > 0) line = string.Join(" | ", parts);
            }
            catch { }
            byte[] output = null;
            // 使用者原本就有自己的狀態列：照樣執行它、顯示它的內容（IslandBar 只是順便把額度記下來）
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--chain") >= 0) output = RunChained(json);
            try
            {
                using var stdout = Console.OpenStandardOutput();
                var bytes = output ?? Encoding.UTF8.GetBytes(line + "\n");
                stdout.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        /// <summary>接手前的狀態列設定（整段 statusLine），解除時還原</summary>
        private static string ChainFile => Path.Combine(Path.GetDirectoryName(QuotaService.StatusLineFile) ?? "", "statusline-chain.json");

        private static byte[] RunChained(string json)
        {
            try
            {
                if (!File.Exists(ChainFile)) return null;
                var node = JsonNode.Parse(File.ReadAllText(ChainFile));
                string cmd = node?["command"]?.ToString();
                if (string.IsNullOrWhiteSpace(cmd)) return null;
                var outp = RunShell("cmd.exe", new[] { "/d", "/s", "/c", "\"" + cmd + "\"" }, json, raw: true);
                if (outp == null || outp.Length == 0)
                {
                    // Claude Code 在 Windows 上可能是用 Git Bash 跑狀態列：bash 語法的指令改用 bash 再試
                    string bash = FindGitBash();
                    if (bash != null) outp = RunShell(bash, new[] { "-c", cmd }, json, raw: false);
                }
                return outp != null && outp.Length > 0 ? outp : null;
            }
            catch { return null; }
        }

        private static byte[] RunShell(string exe, string[] args, string stdin, bool raw)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (raw) psi.Arguments = string.Join(" ", args);   // cmd 的 /s /c "…" 要原封不動
                else foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return null;
                var bytes = Encoding.UTF8.GetBytes(stdin ?? "");
                try { p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); p.StandardInput.Close(); } catch { }
                var ms = new MemoryStream();
                var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
                _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } return null; }
                copy.Wait(2000);
                return ms.ToArray();
            }
            catch { return null; }
        }

        private static string FindGitBash()
        {
            foreach (var dir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
            })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string b = Path.Combine(dir, "Git", "bin", "bash.exe");
                if (File.Exists(b)) return b;
            }
            return null;
        }

        private static string StatusLineCommand =>
            $"\"{(Environment.ProcessPath ?? "IslandBar.exe").Replace('\\', '/')}\" --statusline";

        public static bool IsClaudeStatusLineInstalled()
        {
            try { return File.Exists(ClaudeSettingsPath) && File.ReadAllText(ClaudeSettingsPath).Contains("--statusline"); }
            catch { return false; }
        }

        /// <summary>設定 Claude Code 的 statusLine 指向 IslandBar（已經有自訂狀態列就不覆蓋）</summary>
        public static string InstallClaudeStatusLine()
        {
            string path = ClaudeSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            JsonObject root;
            if (File.Exists(path))
            {
                File.Copy(path, path + ".islandbar.bak", true);
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            else root = new JsonObject();
            string existing = root["statusLine"]?["command"]?.ToString();
            if (!string.IsNullOrEmpty(existing) && !existing.Contains("--statusline"))
            {
                // 已經有自己的狀態列：記下原本的設定，改成「IslandBar 先記額度，再執行原本的狀態列」，畫面跟原本一樣
                var original = JsonNode.Parse(root["statusLine"].ToJsonString()) as JsonObject;
                Directory.CreateDirectory(Path.GetDirectoryName(ChainFile));
                File.WriteAllText(ChainFile, original.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                var wrapped = JsonNode.Parse(original.ToJsonString()) as JsonObject;
                wrapped["type"] = "command";
                wrapped["command"] = StatusLineCommand + " --chain";
                root["statusLine"] = wrapped;
                Log.Write("StatusLine: 接在原本的狀態列前面（原本的設定存在 " + ChainFile + "）");
            }
            else
            {
                root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = StatusLineCommand, ["padding"] = 0 };
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return path;
        }

        public static void UninstallClaudeStatusLine()
        {
            string path = ClaudeSettingsPath;
            if (!File.Exists(path)) return;
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root == null) return;
            if ((root["statusLine"]?["command"]?.ToString() ?? "").Contains("--statusline"))
            {
                root.Remove("statusLine");
                // 還原使用者原本的狀態列
                if (File.Exists(ChainFile))
                {
                    try { root["statusLine"] = JsonNode.Parse(File.ReadAllText(ChainFile)); } catch { }
                    try { File.Delete(ChainFile); } catch { }
                }
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }

        private static string ReadStdin(TimeSpan timeout)
        {
            try
            {
                var task = Task.Run(() =>
                {
                    using var stdin = Console.OpenStandardInput();
                    using var reader = new StreamReader(stdin, Encoding.UTF8);
                    return reader.ReadToEnd();
                });
                return task.Wait(timeout) ? task.Result : "";
            }
            catch { return ""; }
        }

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static void ParseClaude(string json, AgentEvent ev)
        {
            ev.Kind = "stop";
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                string name = Str(r, "hook_event_name") ?? "";
                ev.Kind = name.Equals("Notification", StringComparison.OrdinalIgnoreCase) ? "notification" : "stop";
                ev.Text = Str(r, "message") ?? "";
                string cwd = Str(r, "cwd");
                if (!string.IsNullOrEmpty(cwd)) ev.Folder = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            }
            catch { }
        }

        private static void ParseCodex(string json, AgentEvent ev)
        {
            ev.Kind = "turn-complete";
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                ev.Kind = Str(r, "type") ?? "turn-complete";
                string msg = Str(r, "last-assistant-message") ?? Str(r, "last_assistant_message") ?? "";
                ev.Text = msg.Length > 120 ? msg.Substring(0, 120) + "…" : msg;
                string cwd = Str(r, "cwd");
                if (!string.IsNullOrEmpty(cwd)) ev.Folder = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            }
            catch { }
        }

        // ---------------- 常駐程式：接收 ----------------

        public static void StartServer(Action<AgentEvent> onEvent, CancellationToken token)
        {
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 4,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await server.WaitForConnectionAsync(token);
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        string line = await reader.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            var ev = JsonSerializer.Deserialize<AgentEvent>(line);
                            if (ev != null) onEvent(ev);
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Log.Error("HookServer", ex);
                        try { await Task.Delay(1000, token); } catch { return; }
                    }
                }
            }, token);
        }

        // ---------------- 安裝 / 移除 hooks ----------------

        /// <summary>給 hooks 用的指令（用正斜線，bash 和 cmd 都能執行）</summary>
        private static string HookCommand(string source) =>
            $"\"{(Environment.ProcessPath ?? "IslandBar.exe").Replace('\\', '/')}\" --hook {source}";

        private static string ClaudeSettingsPath => Path.Combine(QuotaService.ClaudeConfigDir, "settings.json");
        private static string CodexConfigPath =>
            Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ??
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "config.toml");

        public static bool IsClaudeInstalled()
        {
            try { return File.Exists(ClaudeSettingsPath) && File.ReadAllText(ClaudeSettingsPath).Contains("--hook claude"); }
            catch { return false; }
        }

        public static bool IsCodexInstalled()
        {
            try { return File.Exists(CodexConfigPath) && File.ReadAllText(CodexConfigPath).Contains("IslandBar"); }
            catch { return false; }
        }

        /// <summary>在 Claude Code 的 settings.json 加上 Stop / Notification hooks（會先備份）</summary>
        public static string InstallClaude()
        {
            string path = ClaudeSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            JsonObject root;
            if (File.Exists(path))
            {
                File.Copy(path, path + ".islandbar.bak", true);
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            else root = new JsonObject();

            var hooks = root["hooks"] as JsonObject;
            if (hooks == null) { hooks = new JsonObject(); root["hooks"] = hooks; }

            foreach (var evName in new[] { "Stop", "Notification" })
            {
                var arr = hooks[evName] as JsonArray;
                if (arr == null) { arr = new JsonArray(); hooks[evName] = arr; }
                RemoveOurs(arr);
                arr.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "command", ["command"] = HookCommand("claude") }
                    }
                });
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return path;
        }

        public static void UninstallClaude()
        {
            string path = ClaudeSettingsPath;
            if (!File.Exists(path)) return;
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root?["hooks"] is not JsonObject hooks) return;
            foreach (var evName in new[] { "Stop", "Notification" })
            {
                if (hooks[evName] is JsonArray arr)
                {
                    RemoveOurs(arr);
                    if (arr.Count == 0) hooks.Remove(evName);
                }
            }
            if (hooks.Count == 0) root.Remove("hooks");
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }

        private static void RemoveOurs(JsonArray arr)
        {
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is JsonObject group && group["hooks"] is JsonArray inner)
                {
                    for (int j = inner.Count - 1; j >= 0; j--)
                        if ((inner[j]?["command"]?.ToString() ?? "").Contains("--hook claude")) inner.RemoveAt(j);
                    if (inner.Count == 0) arr.RemoveAt(i);
                }
            }
        }

        /// <summary>在 Codex 的 config.toml 最上面加 notify（已經有別的 notify 就不動）</summary>
        public static string InstallCodex()
        {
            string path = CodexConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            if (File.Exists(path)) File.Copy(path, path + ".islandbar.bak", true);

            // 只看第一個 [table] 之前的頂層設定
            int firstTable = lines.FindIndex(l => l.TrimStart().StartsWith("["));
            int topEnd = firstTable < 0 ? lines.Count : firstTable;
            int existing = lines.Take(topEnd).ToList().FindIndex(l => l.TrimStart().StartsWith("notify"));
            if (existing >= 0)
            {
                if (lines[existing].Contains("IslandBar")) lines.RemoveAt(existing);
                else throw new InvalidOperationException("Codex 的 config.toml 已經有別的 notify 設定，為了不覆蓋它，請手動合併");
            }
            string exe = (Environment.ProcessPath ?? "IslandBar.exe").Replace('\\', '/');
            lines.Insert(0, $"notify = [\"{exe}\", \"--hook\", \"codex\"]  # IslandBar 任務完成提醒");
            File.WriteAllLines(path, lines);
            return path;
        }

        public static void UninstallCodex()
        {
            string path = CodexConfigPath;
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path).Where(l => !(l.TrimStart().StartsWith("notify") && l.Contains("IslandBar"))).ToList();
            File.WriteAllLines(path, lines);
        }
    }
}
