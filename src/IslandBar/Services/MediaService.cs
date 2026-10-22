using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace IslandBar.Services
{
    public sealed class MediaInfo
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Source { get; set; } = "";
        public bool IsPlaying { get; set; }
        public BitmapImage Art { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTimeOffset LastUpdated { get; set; }

        /// <summary>依照最後更新時間推算目前播放位置</summary>
        public TimeSpan LivePosition
        {
            get
            {
                var pos = Position;
                if (IsPlaying && LastUpdated > DateTimeOffset.MinValue)
                {
                    var elapsed = DateTimeOffset.Now - LastUpdated;
                    if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(6))
                        pos += elapsed;
                }
                if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                return pos;
            }
        }
    }

    /// <summary>
    /// 透過 Windows 的「系統媒體傳輸控制」(SMTC) 讀取正在播放的媒體
    /// （Spotify、YouTube（瀏覽器）、Apple Music、VLC… 只要有接 SMTC 都能用）
    /// </summary>
    public sealed class MediaService
    {
        private GlobalSystemMediaTransportControlsSessionManager _manager;
        private GlobalSystemMediaTransportControlsSession _session;
        private int _version;
        private string _lastArtKey;
        private BitmapImage _lastArt;

        public MediaInfo Current { get; private set; }

        /// <summary>媒體資訊變更（可能在背景執行緒觸發）</summary>
        public event Action<MediaInfo> Changed;

        public async Task InitAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                _manager.SessionsChanged += OnSessionsChanged;
                AttachSession(_manager.GetCurrentSession());
            }
            catch
            {
                _manager = null;
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            AttachSession(sender.GetCurrentSession());
        }

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            AttachSession(sender.GetCurrentSession());
        }

        private void AttachSession(GlobalSystemMediaTransportControlsSession session)
        {
            if (ReferenceEquals(session, _session) && session != null)
            {
                _ = RefreshAsync();
                return;
            }

            if (_session != null)
            {
                try
                {
                    _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                }
                catch { }
            }

            _session = session;

            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }

            _ = RefreshAsync();
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();
        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();
        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _ = RefreshAsync();

        public async Task RefreshAsync()
        {
            int version = Interlocked.Increment(ref _version);
            var session = _session;

            if (session == null)
            {
                Current = null;
                Changed?.Invoke(null);
                return;
            }

            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();

                var info = new MediaInfo
                {
                    Title = props?.Title ?? "",
                    Artist = props?.Artist ?? "",
                    Source = FriendlySource(session.SourceAppUserModelId),
                    IsPlaying = playback != null &&
                                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    Position = timeline?.Position ?? TimeSpan.Zero,
                    Duration = timeline != null ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero,
                    LastUpdated = timeline?.LastUpdatedTime ?? DateTimeOffset.MinValue
                };

                string artKey = info.Title + "|" + info.Artist + "|" + info.Source;
                if (artKey == _lastArtKey && _lastArt != null)
                {
                    info.Art = _lastArt;
                }
                else if (props?.Thumbnail != null)
                {
                    var art = await LoadThumbnailAsync(props.Thumbnail);
                    if (art != null)
                    {
                        _lastArt = art;
                        _lastArtKey = artKey;
                    }
                    info.Art = art;
                }

                if (version != _version) return; // 有更新的結果了，丟掉這次

                if (string.IsNullOrWhiteSpace(info.Title) && string.IsNullOrWhiteSpace(info.Artist))
                {
                    Current = null;
                    Changed?.Invoke(null);
                    return;
                }

                Current = info;
                Changed?.Invoke(info);
            }
            catch
            {
                // 有些 App 切歌時會短暫丟例外，忽略
            }
        }

        private static async Task<BitmapImage> LoadThumbnailAsync(IRandomAccessStreamReference thumbnail)
        {
            try
            {
                using var ras = await thumbnail.OpenReadAsync();
                using var stream = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 240;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static string FriendlySource(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return "";
            string id = appId.ToLowerInvariant();
            if (id.Contains("spotify")) return "Spotify";
            if (id.Contains("chrome")) return "Google Chrome";
            if (id.Contains("msedge") || id.Contains("edge")) return "Microsoft Edge";
            if (id.Contains("firefox")) return "Firefox";
            if (id.Contains("applemusic") || id.Contains("apple")) return "Apple Music";
            if (id.Contains("zunemusic") || id.Contains("media player")) return "媒體播放器";
            if (id.Contains("vlc")) return "VLC";
            if (id.Contains("youtube")) return "YouTube";
            string name = appId;
            int bang = name.IndexOf('!');
            if (bang > 0) name = name.Substring(0, bang);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            int us = name.IndexOf('_');
            if (us > 0) name = name.Substring(0, us);
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);
            return name;
        }

        public async Task TogglePlayPauseAsync()
        {
            try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
        }

        public async Task NextAsync()
        {
            try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
        }

        public async Task PreviousAsync()
        {
            try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
        }

        public async Task SeekAsync(TimeSpan position)
        {
            try { if (_session != null) await _session.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
        }
    }
}
