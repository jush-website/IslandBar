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
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
