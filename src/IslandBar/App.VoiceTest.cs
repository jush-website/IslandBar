using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IslandBar.Services.Voice;

namespace IslandBar
{
    /// <summary>
    /// 語音助理的自我測試（CI 用）：IslandBar.exe --assistant-test 資料夾
    /// 真的下載辨識模型、辨識範例音檔、跑固定指令、下載本機小模型問幾個問題（含工具呼叫），結果寫到 assistant-test.txt。
    /// </summary>
    public partial class App
    {
        private sealed class FakeHost : IAssistantHost
        {
            public readonly List<string> Calls = new List<string>();
            public Task<string> MediaAsync(string action) { Calls.Add("media:" + action); return Task.FromResult("media " + action); }
            public string NowPlaying() { Calls.Add("nowplaying"); return "現在播放：測試歌曲"; }
            public string SetVolume(int? percent, string change) { Calls.Add($"volume:{percent?.ToString() ?? change}"); return $"音量 {percent?.ToString() ?? change}"; }
            public string StartIslandTimer(TimeSpan d) { Calls.Add("timer:" + d); return "計時 " + Commands.Describe(d); }
            public string Status(string what) { Calls.Add("status:" + what); return what == "weather" ? "台北現在多雲，22 度，明天降雨機率 60%" : what == "quota" ? "Claude 5小時 用了 42%，2時10分後重置" : "status " + what; }
            public Task<string> BoostAsync() { Calls.Add("boost"); return Task.FromResult("已加速"); }
        }

        private static readonly string[] RulePhrases =
        {
            "打開記事本", "幫我開 Spotify", "開啟下載資料夾", "打開藍牙設定", "打開 youtube", "打開 github.com", "開啟小算盤",
            "下一首", "暫停", "繼續播放", "播放周杰倫", "我想聽 AC/DC 的歌", "這首歌叫什麼",
            "音量調到三十", "音量 50", "大聲一點", "小聲一點", "靜音", "取消靜音",
            "計時五分鐘", "倒數一個半小時", "10分鐘後提醒我關火", "提醒我三十秒後喝水", "取消提醒",
            "現在幾點", "今天星期幾", "明天會下雨嗎", "我的 AI 額度還剩多少", "電腦狀態", "顯卡溫度", "電腦好卡", "鎖定電腦",
            "YouTube 搜尋 lofi", "搜尋台北美食", "台灣最高的山是哪一座", "幫我寫一首詩"
        };

        private List<string> RuleSamples()
        {
            var lines = new List<string>();
            var host = new FakeHost();
            var reminders = new List<string>();
            var cmd = new Commands(host, (t, x) => { reminders.Add($"{t}:{x}"); return $"提醒 {Commands.Describe(t)}：{x}"; }, () => reminders.Count);
            bool dry = Commands.DryRun;
            Commands.DryRun = true;
            foreach (var p in RulePhrases)
            {
                host.Calls.Clear();
                var r = cmd.TryRulesAsync(p).GetAwaiter().GetResult();
                lines.Add(r == null ? $"{p} → （交給 AI）" : $"{p} → {r.Reply}" + (host.Calls.Count > 0 ? $"  [{string.Join(",", host.Calls)}]" : "") +
                    (r.Actions.Count > 0 ? $"  <{string.Join(",", r.Actions)}>" : "") + (Commands.LastLaunched != null && r.Reply.StartsWith("已開啟") ? "  ⇒ " + Commands.LastLaunched : ""));
            }
            Commands.DryRun = dry;
            return lines;
        }

        private async void RunAssistantTest(string dir)
        {
            var log = new StringBuilder();
            void W(string s) { log.AppendLine(s); try { File.WriteAllText(Path.Combine(dir, "assistant-test.txt"), log.ToString()); } catch { } }
            try
            {
                Directory.CreateDirectory(dir);
                Commands.DryRun = true;
                W("== 固定指令");
                foreach (var l in RuleSamples()) W(l);

                W("\n== 語音辨識");
                var sw = Stopwatch.StartNew();
                await ModelStore.DownloadSttAsync(new Progress<DownloadProgress>(_ => { }), CancellationToken.None);
                W($"下載模型 {sw.Elapsed.TotalSeconds:0.0} 秒，Ready={ModelStore.SttReady}");
                string wav = Path.Combine(dir, "zh.wav");
                await ModelStore.DownloadSttSampleAsync(wav, CancellationToken.None);
                var (samples, rate) = ReadWav(wav);
                W($"範例音檔 {samples.Length / (double)rate:0.0} 秒 @ {rate} Hz");
                using (var stt = new SpeechToText())
                {
                    sw.Restart();
                    await stt.PreloadAsync();
                    W($"載入 {sw.ElapsedMilliseconds} ms");
                    for (int i = 0; i < 2; i++)
                    {
                        sw.Restart();
                        string text = await stt.RecognizeAsync((float[])samples.Clone(), rate);
                        W($"辨識 #{i + 1}：{sw.ElapsedMilliseconds} ms →「{text}」");
                    }
                    // 48 kHz 雙聲道麥克風常見的取樣率：先重新取樣成 48k 再丟進去（確認引擎會自己轉）
                    var up = Resample(samples, rate, 48000);
                    sw.Restart();
                    W($"48 kHz：{sw.ElapsedMilliseconds} ms →「{await stt.RecognizeAsync(up, 48000)}」");
                }
                W("簡轉繁：" + SpeechToText.ToTraditional("打开软件，帮我设置一个计时器，网络视频"));

                W("\n== 語音");
                try
                {
                    foreach (var v in Speaker.Voices()) W($"  {v.name} {v.lang}");
                    using var sp = new Speaker();
                    var dur = await sp.SpeakAsync("測試語音");
                    W($"合成 OK，{dur.TotalSeconds:0.0} 秒");
                }
                catch (Exception ex) { W("語音失敗：" + ex.Message); }

                W("\n== 本機小模型（llama-server）");
                var model = ModelStore.LocalModel(Environment.GetEnvironmentVariable("ISLANDBAR_TEST_MODEL") ?? "qwen3.5-0.8b");
                sw.Restart();
                await ModelStore.DownloadLocalModelAsync(model, new Progress<DownloadProgress>(_ => { }), CancellationToken.None);
                W($"下載 {model.Name} {sw.Elapsed.TotalSeconds:0.0} 秒，llama={ModelStore.LlamaReady}");
                var host = new FakeHost();
                var rem = new List<string>();
                var cmd = new Commands(host, (t, x) => { rem.Add(x); return $"好，{Commands.Describe(t)}後提醒你：{x}"; }, () => 0);
                using (var local = new LocalLlm { ModelId = model.Id })
                {
                    var brain = new Brain(cmd, local) { Mode = () => "local", Ports = () => Array.Empty<int>() };
                    var info = await brain.FindAsync(true, CancellationToken.None, fresh: true);
                    W("大腦：" + (info?.Label ?? "（沒有）"));
                    if (info != null)
                    {
                        sw.Restart();
                        await local.EnsureAsync(CancellationToken.None);
                        W($"啟動 {sw.ElapsedMilliseconds} ms");
                        var history = new List<ChatTurn>();
                        foreach (var q in new[]
                        {
                            "台灣最高的山是哪一座？",
                            "我覺得有點吵，幫我把電腦聲音關掉",
                            "幫我用 YouTube 找貓咪影片",
                            "等一下二十分鐘後要提醒我去收衣服",
                            "外面天氣怎麼樣？",
                            "用一句話介紹你自己"
                        })
                        {
                            host.Calls.Clear();
                            sw.Restart();
                            try
                            {
                                var r = await brain.AskAsync(q, history, info, CancellationToken.None);
                                W($"問：{q}\n答（{sw.ElapsedMilliseconds} ms）：{r.Reply}" + (host.Calls.Count > 0 ? $"  [{string.Join(",", host.Calls)}]" : "") +
                                  (r.Actions.Count > 0 ? $"  <{string.Join(",", r.Actions)}>" : "") + (rem.Count > 0 ? $"  提醒={string.Join("|", rem)}" : ""));
                                history.Add(new ChatTurn { Role = "user", Text = q });
                                history.Add(new ChatTurn { Role = "assistant", Text = r.Reply });
                                rem.Clear();
                            }
                            catch (Exception ex) { W($"問：{q}\n失敗：{ex.Message}"); }
                        }
                    }
                }
            }
            catch (Exception ex) { W("錯誤：" + ex); }
            try { File.Copy(Log.FilePath, Path.Combine(dir, "log.txt"), true); } catch { }
            ExitApp();
        }

        private static (float[] samples, int rate) ReadWav(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            r.ReadBytes(12);
            int rate = 16000, ch = 1, bits = 16;
            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                string id = Encoding.ASCII.GetString(r.ReadBytes(4));
                int size = r.ReadInt32();
                if (id == "fmt ")
                {
                    r.ReadInt16(); ch = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                    r.ReadBytes(size - 16);
                }
                else if (id == "data")
                {
                    var bytes = r.ReadBytes(size);
                    int frames = bytes.Length / (ch * bits / 8);
                    var s = new float[frames];
                    for (int i = 0; i < frames; i++) s[i] = BitConverter.ToInt16(bytes, i * ch * 2) / 32768f;
                    return (s, rate);
                }
                else r.ReadBytes(size);
            }
            return (Array.Empty<float>(), rate);
        }

        private static float[] Resample(float[] s, int from, int to)
        {
            int n = (int)((long)s.Length * to / from);
            var o = new float[n];
            for (int i = 0; i < n; i++)
            {
                double x = i * (double)from / to;
                int a = (int)x;
                double f = x - a;
                float v0 = s[Math.Min(a, s.Length - 1)], v1 = s[Math.Min(a + 1, s.Length - 1)];
                o[i] = (float)(v0 + (v1 - v0) * f);
            }
            return o;
        }
    }
}
