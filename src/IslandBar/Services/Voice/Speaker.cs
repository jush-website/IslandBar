using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.SpeechSynthesis;

namespace IslandBar.Services.Voice
{
    /// <summary>用 Windows 內建的語音念出回覆（優先找中文（台灣）語音：Hanhan、Yating、Zhiwei…）</summary>
    public sealed class Speaker : IDisposable
    {
        private SpeechSynthesizer _synth;
        private SoundPlayer _player;
        private int _seq;

        public string VoiceName { get; set; } = "";
        public double Rate { get; set; } = 1.0;
        public long LastBytes { get; private set; }

        public static List<(string name, string lang)> Voices()
        {
            try { return SpeechSynthesizer.AllVoices.Select(v => (v.DisplayName, v.Language)).ToList(); }
            catch (Exception ex) { Log.Error("Speaker.Voices", ex); return new List<(string, string)>(); }
        }

        private VoiceInformation PickVoice()
        {
            var all = SpeechSynthesizer.AllVoices;
            if (!string.IsNullOrEmpty(VoiceName))
            {
                var v = all.FirstOrDefault(x => x.DisplayName == VoiceName);
                if (v != null) return v;
            }
            return all.FirstOrDefault(x => x.Language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(x => x.Language.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(x => x.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                ?? SpeechSynthesizer.DefaultVoice;
        }

        /// <summary>念出來；新的一句會打斷上一句。傳回大約要念多久。</summary>
        public async Task<TimeSpan> SpeakAsync(string text)
        {
            Stop();
            text = Clean(text);
            if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
            int seq = ++_seq;
            try
            {
                _synth ??= new SpeechSynthesizer();
                var voice = PickVoice();
                if (voice != null && _synth.Voice?.Id != voice.Id) _synth.Voice = voice;
                try { _synth.Options.SpeakingRate = Math.Clamp(Rate, 0.5, 3.0); } catch { }
                using var stream = await _synth.SynthesizeTextToStreamAsync(text);
                var ms = new MemoryStream();
                using (var s = stream.AsStreamForRead()) await s.CopyToAsync(ms);
                if (seq != _seq) return TimeSpan.Zero;   // 已經被下一句取代
                ms.Position = 0;
                var dur = WavDuration(ms);
                LastBytes = ms.Length;
                ms.Position = 0;
                _player = new SoundPlayer(ms);
                _player.Play();   // 非同步播放
                return dur;
            }
            catch (Exception ex)
            {
                Log.Error("Speaker", ex);
                return TimeSpan.Zero;
            }
        }

        public void Stop()
        {
            _seq++;
            try { _player?.Stop(); } catch { }
            _player = null;
        }

        /// <summary>拿掉不適合念出來的東西（網址、表情符號、Markdown 符號）</summary>
        private static string Clean(string t)
        {
            if (t == null) return "";
            t = Regex.Replace(t, @"https?://\S+", "");
            t = Regex.Replace(t, @"[*_#`>|]", "");
            t = Regex.Replace(t, @"\p{Cs}", "");   // emoji（代理字元）
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t.Length > 400 ? t.Substring(0, 400) : t;
        }

        private static TimeSpan WavDuration(Stream s)
        {
            try
            {
                using var r = new BinaryReader(s, System.Text.Encoding.ASCII, leaveOpen: true);
                if (new string(r.ReadChars(4)) != "RIFF") return TimeSpan.Zero;
                r.ReadInt32();
                r.ReadChars(4);
                int byteRate = 0;
                while (s.Position < s.Length - 8)
                {
                    string id = new string(r.ReadChars(4));
                    int size = r.ReadInt32();
                    if (id == "fmt ")
                    {
                        r.ReadInt16(); r.ReadInt16(); r.ReadInt32();
                        byteRate = r.ReadInt32();
                        s.Position += size - 12;
                    }
                    else if (id == "data") return byteRate > 0 ? TimeSpan.FromSeconds(size / (double)byteRate) : TimeSpan.Zero;
                    else s.Position += size;
                }
            }
            catch { }
            return TimeSpan.Zero;
        }

        public void Dispose()
        {
            Stop();
            try { _synth?.Dispose(); } catch { }
            _synth = null;
        }
    }
}
