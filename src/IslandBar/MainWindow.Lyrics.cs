using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// 同步歌詞：放音樂時，收合的動態島（和音樂分頁）跟著唱到哪一句顯示歌詞。歌詞從 LRCLIB 抓，找不到就照舊顯示歌名。
    /// </summary>
    public partial class MainWindow
    {
        private List<LrcLine> _lyrics;
        private string _lyricsKey;
        private CancellationTokenSource _lyricsCts;
        private int _lyricIndex = -2;
        private readonly DispatcherTimer _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private const double CompactTitleWidth = 140, CompactLyricWidth = 220;

        private bool LyricsActive => _lyrics != null && _settings.ShowLyrics && _currentMedia != null;

        /// <summary>換歌、暫停／播放、設定改了時呼叫</summary>
        private void UpdateLyricsTrack(MediaInfo info)
        {
            if (_lyricTimer.Tag == null) { _lyricTimer.Tag = 1; _lyricTimer.Tick += (s, e) => LyricTick(); }
            if (info == null || !_settings.ShowLyrics || string.IsNullOrWhiteSpace(info.Title))
            {
                ClearLyrics();
                return;
            }
            string key = info.Title + "|" + info.Artist;
            if (key != _lyricsKey)
            {
                ClearLyrics();
                _lyricsKey = key;
                var cts = _lyricsCts = new CancellationTokenSource();
                string title = info.Title, artist = info.Artist;
                var dur = info.Duration;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(600, cts.Token);   // 換歌時常常連續通知好幾次：等一下再查
                        var lines = await LyricsService.GetAsync(title, artist, dur, cts.Token);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (cts.IsCancellationRequested || _lyricsKey != key) return;
                            _lyrics = lines;
                            _lyricIndex = -2;
                            ApplyLyricLayout();
                            LyricTick();
                            if (_currentMedia?.IsPlaying == true && _lyrics != null) _lyricTimer.Start();
                        }));
                    }
                    catch { }
                });
                return;
            }
            if (info.IsPlaying && _lyrics != null) _lyricTimer.Start(); else _lyricTimer.Stop();
            LyricTick();
        }

        private void ClearLyrics()
        {
            _lyricsCts?.Cancel();
            _lyricsCts = null;
            _lyricsKey = null;
            _lyrics = null;
            _lyricIndex = -2;
            _lyricTimer.Stop();
            MediaLyric.Visibility = Visibility.Collapsed;
            ApplyLyricLayout();
            if (_currentMedia != null) CompactTitle.Text = TitleText(_currentMedia);
        }

        private static string TitleText(MediaInfo info) => string.IsNullOrEmpty(info.Artist) ? info.Title : info.Title + "  ·  " + info.Artist;

        /// <summary>有歌詞時收合的動態島歌詞區加寬一點（寬度固定，不會每句跳來跳去）</summary>
        private void ApplyLyricLayout()
        {
            double w = LyricsActive ? CompactLyricWidth : CompactTitleWidth;
            if (Math.Abs(CompactTitle.Width - w) > 0.5)
            {
                CompactTitle.Width = w;
                if (_mode == IslandMode.Compact) ApplyMode(true);
            }
        }

        private void LyricTick()
        {
            if (!LyricsActive) { _lyricTimer.Stop(); return; }
            var pos = _currentMedia.LivePosition + TimeSpan.FromMilliseconds(250);   // 稍微提早一點，看起來比較跟得上
            int i = LyricsService.IndexAt(_lyrics, pos);
            if (i == _lyricIndex) return;
            _lyricIndex = i;
            string line = i < 0 ? null : (string.IsNullOrWhiteSpace(_lyrics[i].Text) ? "♪" : _lyrics[i].Text);
            CompactTitle.Text = line ?? TitleText(_currentMedia);
            MediaLyric.Text = line ?? "";
            MediaLyric.Visibility = line != null ? Visibility.Visible : Visibility.Collapsed;
            if (line != null && _mode == IslandMode.Compact)
            {
                CompactTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(180)));
            }
        }

        public string DebugLyricState => $"active={LyricsActive} lines={_lyrics?.Count ?? 0} index={_lyricIndex} width={CompactTitle.Width}";

        /// <summary>測試用：假裝在放一首歌，直接給歌詞（不上網）</summary>
        public void DebugLyrics(List<LrcLine> lines, MediaInfo fake)
        {
            OnMediaChanged(fake);
            _lyricsCts?.Cancel();
            _lyricsKey = fake.Title + "|" + fake.Artist;
            _lyrics = lines;
            _lyricIndex = -2;
            ApplyLyricLayout();
            LyricTick();
        }

        public void DebugClearMedia() => OnMediaChanged(null);
    }
}
