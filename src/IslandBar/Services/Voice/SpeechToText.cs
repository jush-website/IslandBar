using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SherpaOnnx;

namespace IslandBar.Services.Voice
{
    /// <summary>
    /// 本機語音辨識：sherpa-onnx ＋ SenseVoice Small（int8），只用 CPU，幾秒的話通常零點幾秒就辨識完。
    /// 第一次用才載入（約 1~2 秒）；閒置一段時間自動釋放記憶體。辨識結果轉成繁體中文。
    /// </summary>
    public sealed class SpeechToText : IDisposable
    {
        private OfflineRecognizer _rec;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private DateTime _lastUsed = DateTime.UtcNow;
        private Timer _idleTimer;

        /// <summary>閒置多久釋放模型（0＝一直留著）</summary>
        public TimeSpan IdleUnload { get; set; } = TimeSpan.FromMinutes(10);
        public bool Loaded => _rec != null;

        public SpeechToText()
        {
            _idleTimer = new Timer(_ =>
            {
                if (_rec == null || IdleUnload <= TimeSpan.Zero) return;
                if (DateTime.UtcNow - _lastUsed < IdleUnload) return;
                if (!_gate.Wait(0)) return;
                try { _rec?.Dispose(); _rec = null; Log.Write("Voice: 閒置，釋放辨識模型"); }
                catch { }
                finally { _gate.Release(); }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        /// <summary>預先載入（按下說話鍵時就開始載，放開時多半已經好了）</summary>
        public Task PreloadAsync() => Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { EnsureLoaded(); }
            catch (Exception ex) { Log.Error("Voice.Preload", ex); }
            finally { _gate.Release(); }
        });

        private void EnsureLoaded()
        {
            _lastUsed = DateTime.UtcNow;
            if (_rec != null) return;
            if (!ModelStore.SttReady) throw new InvalidOperationException("還沒下載語音辨識模型");
            var sw = Stopwatch.StartNew();
            var cfg = new OfflineRecognizerConfig();
            cfg.FeatConfig.SampleRate = 16000;
            cfg.FeatConfig.FeatureDim = 80;
            cfg.ModelConfig.SenseVoice.Model = ModelStore.SttModel;
            cfg.ModelConfig.SenseVoice.Language = "auto";
            cfg.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;   // 「五分鐘」→「5分鐘」、加標點
            cfg.ModelConfig.Tokens = ModelStore.SttTokens;
            cfg.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            cfg.ModelConfig.Provider = "cpu";
            cfg.ModelConfig.Debug = 0;
            cfg.DecodingMethod = "greedy_search";
            _rec = new OfflineRecognizer(cfg);
            Log.Write($"Voice: 辨識模型載入 {sw.ElapsedMilliseconds} ms（{cfg.ModelConfig.NumThreads} 執行緒）");
        }

        /// <summary>辨識一段聲音（單聲道 float），傳回繁體中文文字</summary>
        public Task<string> RecognizeAsync(float[] samples, int sampleRate) => Task.Run(async () =>
        {
            if (samples == null || samples.Length < sampleRate / 5) return "";   // 不到 0.2 秒
            await _gate.WaitAsync();
            try
            {
                EnsureLoaded();
                var sw = Stopwatch.StartNew();
                Normalize(samples);
                using var stream = _rec.CreateStream();
                stream.AcceptWaveform(sampleRate, samples);
                _rec.Decode(stream);
                string text = (stream.Result.Text ?? "").Trim();
                text = ToTraditional(text);
                _lastUsed = DateTime.UtcNow;
                Log.Write($"Voice: 辨識 {samples.Length / (double)sampleRate:0.0} 秒聲音 → {sw.ElapsedMilliseconds} ms，{text.Length} 字");
                return text;
            }
            finally { _gate.Release(); }
        });

        /// <summary>聲音太小聲時放大一點（最多 8 倍），辨識比較準</summary>
        private static void Normalize(float[] s)
        {
            float peak = 0;
            foreach (var v in s) { float a = Math.Abs(v); if (a > peak) peak = a; }
            if (peak <= 0.0001f || peak >= 0.5f) return;
            float gain = Math.Min(8f, 0.7f / peak);
            for (int i = 0; i < s.Length; i++) s[i] *= gain;
        }

        public void Dispose()
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            try { _rec?.Dispose(); } catch { }
            _rec = null;
        }

        // ---------------- 簡體 → 繁體（Windows 內建的 LCMapStringEx，不用額外字典） ----------------

        private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int LCMapStringEx(string locale, uint flags, string src, int srcLen, StringBuilder dest, int destLen, IntPtr ver, IntPtr reserved, IntPtr param);

        public static string ToTraditional(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            try
            {
                var sb = new StringBuilder(s.Length + 16);
                int n = LCMapStringEx("zh-TW", LCMAP_TRADITIONAL_CHINESE, s, s.Length, sb, sb.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (n <= 0) return s;
                string t = sb.ToString(0, Math.Min(n, sb.Length));
                // 常見用語（字對字轉換不會處理的）
                return t.Replace("軟件", "軟體").Replace("視頻", "影片").Replace("屏幕", "螢幕").Replace("鼠標", "滑鼠")
                        .Replace("網絡", "網路").Replace("信息", "訊息").Replace("內存", "記憶體").Replace("音頻", "音訊")
                        .Replace("文件夾", "資料夾").Replace("程序", "程式");
            }
            catch { return s; }
        }
    }
}
