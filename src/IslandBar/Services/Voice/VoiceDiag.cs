using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services.Voice
{
    /// <summary>
    /// 語音助理排查：遠端桌面、麥克風收不到聲音、模型沒下載…這些情況要講清楚怎麼修，
    /// 設定頁的「自我檢查」也從這裡跑。
    /// </summary>
    public static class VoiceDiag
    {
        /// <summary>錄到的聲音最大值低於這個＝一片靜音（被靜音、隱私權擋住、遠端桌面沒帶麥克風）</summary>
        public const float SilentPeak = 0.0015f;

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        private const int SM_REMOTESESSION = 0x1000;

        /// <summary>現在是不是從遠端桌面（RDP）連進來的</summary>
        public static bool IsRemoteSession
        {
            get { try { return GetSystemMetrics(SM_REMOTESESSION) != 0; } catch { return false; } }
        }

        public const string RdpMicSteps = "在遠端桌面連線的「本機資源 → 遠端音訊 → 設定」把遠端錄音改成「從這台電腦錄製」，再重新連線";

        public static string NoMicHint() => IsRemoteSession
            ? "遠端桌面沒有把麥克風帶過來：" + RdpMicSteps
            : "找不到麥克風：確認麥克風有接好，或到 Windows 設定 → 系統 → 音效 → 輸入 選一個麥克風";

        public static string SilentHint(string device)
        {
            string dev = string.IsNullOrWhiteSpace(device) ? "麥克風" : "「" + device + "」";
            return IsRemoteSession
                ? dev + "收不到聲音：" + RdpMicSteps
                : dev + "收不到聲音：麥克風可能被靜音，或 Windows 擋住了桌面程式用麥克風（點這裡開隱私權設定）";
        }

        public static float Peak(float[] samples)
        {
            float p = 0;
            if (samples == null) return 0;
            foreach (var v in samples) { float a = Math.Abs(v); if (a > p) p = a; }
            return p;
        }

        /// <summary>檢查結果的一行：ok＝true 綠勾、false 紅叉、null 只是說明</summary>
        public sealed class Line
        {
            public string Text { get; set; }
            public bool? Ok { get; set; }
            public Line(string text, bool? ok) { Text = text; Ok = ok; }
        }

        /// <summary>這幾句應該不用 AI 就能做到（自我檢查用，不會真的打開）</summary>
        public static readonly (string say, string expect)[] OpenSamples =
        {
            ("呼叫瀏覽器開啟 YouTube", "youtube.com"),
            ("用 Chrome 打開 YouTube", "youtube.com"),
            ("打開記事本", "notepad"),
            ("在瀏覽器上搜尋 IslandBar", "google.com/search"),
        };

        /// <summary>
        /// 從頭到尾檢查一次：開關、說話鍵、辨識模型、麥克風（錄 2 秒）、AI 大腦、常用說法。
        /// 每檢查完一項就呼叫 report（在呼叫端的執行緒上）。
        /// </summary>
        public static async Task<List<Line>> RunAsync(AssistantService a, AppSettings s, Action<Line> report, CancellationToken ct)
        {
            var lines = new List<Line>();
            void Add(string text, bool? ok) { var l = new Line(text, ok); lines.Add(l); report?.Invoke(l); }

            if (a == null) { Add("語音助理沒有啟動（重新開啟 IslandBar 再試）", false); return lines; }
            bool remote = IsRemoteSession;

            // 1. 開關、說話鍵
            Add(s.AssistantEnabled ? "語音助理已開啟" : "語音助理關著：勾上面的「啟用語音助理」", s.AssistantEnabled);
            string key = PushToTalkKey.KeyName(s.AssistantKey);
            if (!a.Key.HookOk) Add($"說話鍵（{key}）沒有掛上：重新開啟 IslandBar 試試", false);
            else if (a.Key.LastKeySeen != default)
                Add($"說話鍵（{key}）正常：{a.Key.LastKeySeen:HH:mm:ss} 收到過", true);
            else
                Add($"說話鍵（{key}）這次開機還沒收到過：按住它說話一次，再回來檢查" +
                    (remote ? "（遠端桌面：確認遠端連線的「套用 Windows 按鍵組合」是「在遠端電腦上」）" : ""), null);

            // 2. 辨識模型
            Add(ModelStore.SttReady ? "語音辨識模型已下載" : $"還沒下載語音辨識模型（{ModelStore.SttSizeText}）：按下面「語音辨識」的下載", ModelStore.SttReady);

            // 3. 麥克風
            if (remote) Add("現在是遠端桌面連線：麥克風要從你這邊的電腦帶過去（" + RdpMicSteps + "）", null);
            var mics = MicRecorder.Devices();
            if (mics.Count == 0) Add(NoMicHint(), false);
            else
            {
                string chosen = mics.FirstOrDefault(m => m.id == s.AssistantMic).name;
                Add($"找到 {mics.Count} 個麥克風：" + string.Join("、", mics.Select(m => m.name).Take(4)) + (chosen != null ? $"（用：{chosen}）" : "（用系統預設）"), true);
                if (a.State == VoiceState.Listening) Add("現在正在聽，略過錄音測試", null);
                else
                {
                    var rec = new MicRecorder();
                    try
                    {
                        rec.Start(s.AssistantMic, 5);
                        await Task.Delay(2200, ct);
                        var samples = rec.Stop();
                        float peak = Peak(samples);
                        double secs = samples.Length / (double)Math.Max(1, rec.SampleRate);
                        if (samples.Length == 0) Add($"「{rec.DeviceName}」打開了，但 2 秒內沒有送來任何聲音資料", false);
                        else if (peak < SilentPeak) Add(SilentHint(rec.DeviceName), false);
                        else Add($"「{rec.DeviceName}」收得到聲音（{secs:0.0} 秒，最大音量 {peak * 100:0.0}%）", true);
                    }
                    catch (OperationCanceledException) { rec.Stop(); throw; }
                    catch (Exception ex)
                    {
                        rec.Stop();
                        Log.Error("VoiceDiag.Mic", ex);
                        Add("麥克風打不開：" + ex.Message, false);
                    }
                }
            }

            // 4. AI 大腦（聽不懂的說法才需要）
            try
            {
                var brain = await a.Brain.FindAsync(true, ct, fresh: true);
                Add(brain != null ? "AI 大腦：" + brain.Label : "沒有找到 AI 大腦：只能用基本指令（打開程式、放音樂、計時…），其他說法需要 PAIR／Ollama 或本機小模型", brain != null ? true : (bool?)null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Add("找 AI 大腦時出錯：" + ex.Message, null); }

            // 5. 常用說法（不會真的打開）
            bool dry = Commands.DryRun;
            Commands.DryRun = true;
            try
            {
                foreach (var (say, expect) in OpenSamples)
                {
                    Commands.LastLaunched = null;
                    var r = await a.Commands.TryRulesAsync(say);
                    string target = Commands.LastLaunched ?? "";
                    bool ok = r != null && r.Ok && target.Contains(expect, StringComparison.OrdinalIgnoreCase);
                    Add($"「{say}」→ " + (ok ? (r.Actions.FirstOrDefault() ?? r.Reply) : r?.Reply ?? "基本指令聽不懂"), ok);
                }
            }
            finally { Commands.DryRun = dry; }
            return lines;
        }
    }
}
