using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace IslandBar.Services
{
    /// <summary>
    /// 螢幕錄影：一秒抓 30（或 60）張畫面（GDI，含滑鼠游標），交給 Windows 內建的硬體 H.264 編碼器存成 MP4。
    /// 可以順便錄電腦播出來的聲音（WASAPI loopback），停止後用 Windows 的影片合成把聲音合進去。
    /// 錄影時動態島自己不會被錄進去（SetWindowDisplayAffinity）。
    /// </summary>
    public sealed class ScreenRecorder : IDisposable
    {
        public sealed class Options
        {
            public int X, Y, Width, Height;      // 實際像素
            public int Fps = 30;
            public bool Cursor = true;
            public bool SystemAudio = true;
            public int MaxWidth = 1920;          // 太大的螢幕縮到這個寬度（0＝不縮）
            public string Folder = "";
        }

        private readonly Options _o;
        private readonly int _outW, _outH;
        private readonly double _scale;
        private readonly BlockingCollection<Frame> _frames = new BlockingCollection<Frame>(8);
        private readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Thread _captureThread;
        private Task _encodeTask;
        private string _videoPath, _wavPath;
        private WasapiLoopbackCapture _loop;
        private WaveFileWriter _wav;
        private WasapiOut _silence;
        private long _dropped, _captured;

        public TimeSpan Elapsed => _clock.Elapsed;
        public string FinalPath { get; private set; }
        public bool AudioRecorded { get; private set; }
        public string AudioError { get; private set; }
        public long DroppedFrames => _dropped;
        public long CapturedFrames => _captured;
        public int OutputWidth => _outW;
        public int OutputHeight => _outH;

        private sealed class Frame { public byte[] Data; public TimeSpan Time; }

        public ScreenRecorder(Options o)
        {
            _o = o;
            _scale = o.MaxWidth > 0 && o.Width > o.MaxWidth ? (double)o.MaxWidth / o.Width : 1.0;
            _outW = Even((int)Math.Round(o.Width * _scale));
            _outH = Even((int)Math.Round(o.Height * _scale));
        }

        private static int Even(int v) => Math.Max(2, v - v % 2);

        public static string DefaultFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "IslandBar 錄影");

        public async Task StartAsync()
        {
            string folder = string.IsNullOrWhiteSpace(_o.Folder) ? DefaultFolder() : _o.Folder;
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            FinalPath = PhoneShare.UniquePath(folder, $"錄影_{stamp}.mp4");
            _videoPath = _o.SystemAudio ? Path.Combine(Path.GetTempPath(), $"islandbar_rec_{stamp}.mp4") : FinalPath;

            // 編碼器：MediaStreamSource（未壓縮 BGRA）→ MediaTranscoder（硬體 H.264）
            var vprops = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_outW, (uint)_outH);
            vprops.FrameRate.Numerator = (uint)_o.Fps;
            vprops.FrameRate.Denominator = 1;
            var mss = new MediaStreamSource(new VideoStreamDescriptor(vprops)) { BufferTime = TimeSpan.Zero, CanSeek = false };
            mss.Starting += (s, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
            mss.SampleRequested += OnSampleRequested;

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Audio = null;
            profile.Video.Width = (uint)_outW;
            profile.Video.Height = (uint)_outH;
            profile.Video.FrameRate.Numerator = (uint)_o.Fps;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = (uint)Math.Clamp(_outW * _outH * _o.Fps / 10, 2_000_000, 20_000_000);

            var file = await (await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(_videoPath))).CreateFileAsync(Path.GetFileName(_videoPath), CreationCollisionOption.ReplaceExisting);
            var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, stream, profile);
            if (!prep.CanTranscode) { stream.Dispose(); throw new InvalidOperationException("Windows 的影片編碼器不能用（" + prep.FailureReason + "）"); }

            if (_o.SystemAudio) StartAudio();
            _clock.Start();
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "IslandBar 錄影", Priority = ThreadPriority.AboveNormal };
            _captureThread.Start();
            _encodeTask = Task.Run(async () =>
            {
                try { await prep.TranscodeAsync(); }
                finally { stream.Dispose(); }
            });
            Log.Write($"Recorder: 開始 {_o.Width}x{_o.Height} → {_outW}x{_outH} @{_o.Fps}fps，聲音 {(_loop != null ? "有" : "沒有")}");
        }

        /// <summary>停止並存檔；傳回最後的檔案路徑</summary>
        public async Task<string> StopAsync()
        {
            _stop.Cancel();
            _captureThread?.Join(2000);
            _frames.CompleteAdding();
            _clock.Stop();
            StopAudio();
            if (_encodeTask != null) await _encodeTask;
            Log.Write($"Recorder: 停止，{_captured} 張、掉 {_dropped} 張，{_clock.Elapsed.TotalSeconds:0.0} 秒");

            if (_o.SystemAudio)
            {
                bool hasAudio = _wavPath != null && File.Exists(_wavPath) && new FileInfo(_wavPath).Length > 4096;
                if (hasAudio)
                {
                    try
                    {
                        await MuxAsync(_videoPath, _wavPath, FinalPath);
                        AudioRecorded = true;
                    }
                    catch (Exception ex)
                    {
                        AudioError = "合併聲音失敗：" + ex.Message;
                        Log.Write("Recorder: " + AudioError);
                        File.Copy(_videoPath, FinalPath, true);
                    }
                }
                else File.Copy(_videoPath, FinalPath, true);
                TryDelete(_videoPath);
                TryDelete(_wavPath);
            }
            return FinalPath;
        }

        // ================================================================
        //  抓畫面
        // ================================================================

        private void CaptureLoop()
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            // 由下往上的點陣圖：Windows 的編碼器把未壓縮的 RGB 當成「最後一行在最前面」，這樣錄出來才不會上下顛倒
            var bmi = new BITMAPINFO { biSize = 40, biWidth = _outW, biHeight = _outH, biPlanes = 1, biBitCount = 32 };
            IntPtr dib = CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = SelectObject(mem, dib);
            if (_scale < 1) { SetStretchBltMode(mem, 4 /* HALFTONE */); SetBrushOrgEx(mem, 0, 0, IntPtr.Zero); }
            int size = _outW * _outH * 4;
            var interval = TimeSpan.FromSeconds(1.0 / _o.Fps);
            long n = 0;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var due = TimeSpan.FromTicks(interval.Ticks * n);
                    var wait = due - _clock.Elapsed;
                    if (wait > TimeSpan.FromMilliseconds(1)) Thread.Sleep(wait);
                    if (_stop.IsCancellationRequested) break;
                    var t = _clock.Elapsed;
                    n = (long)(t.Ticks / interval.Ticks) + 1;   // 落後了就跳過，不要越積越多

                    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
                    if (_scale >= 1) BitBlt(mem, 0, 0, _outW, _outH, screen, _o.X, _o.Y, SRCCOPY | CAPTUREBLT);
                    else StretchBlt(mem, 0, 0, _outW, _outH, screen, _o.X, _o.Y, _o.Width, _o.Height, SRCCOPY | CAPTUREBLT);
                    if (_o.Cursor) DrawCursor(mem);

                    if (!_pool.TryTake(out var buf) || buf.Length != size) buf = new byte[size];
                    Marshal.Copy(bits, buf, 0, size);
                    var frame = new Frame { Data = buf, Time = TimeSpan.FromTicks(interval.Ticks * (n - 1)) };
                    if (_frames.TryAdd(frame)) _captured++;
                    else { _dropped++; _pool.Add(buf); }   // 編碼器忙不過來：丟掉這一張
                }
            }
            catch (Exception ex) { Log.Write("Recorder: 抓畫面錯誤 " + ex.Message); }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(dib);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private void DrawCursor(IntPtr mem)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return;
            int hx = 0, hy = 0;
            if (GetIconInfo(ci.hCursor, out var ii))
            {
                hx = ii.xHotspot; hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            int x = (int)Math.Round((ci.ptScreenPos.X - _o.X) * _scale) - hx, y = (int)Math.Round((ci.ptScreenPos.Y - _o.Y) * _scale) - hy;
            if (x < -64 || y < -64 || x > _outW || y > _outH) return;
            DrawIconEx(mem, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
        }

        private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs e)
        {
            var req = e.Request;
            var def = req.GetDeferral();
            Task.Run(() =>
            {
                try
                {
                    if (_frames.TryTake(out var f, Timeout.Infinite))
                    {
                        var sample = MediaStreamSample.CreateFromBuffer(f.Data.AsBuffer(), f.Time);
                        sample.Duration = TimeSpan.FromSeconds(1.0 / _o.Fps);
                        var data = f.Data;
                        sample.Processed += (s, a) => _pool.Add(data);   // 編碼完才可以重複用這塊記憶體
                        req.Sample = sample;
                    }
                    else req.Sample = null;   // 停止了：結束影片
                }
                catch (InvalidOperationException) { req.Sample = null; }
                catch (Exception ex) { Log.Write("Recorder: 編碼錯誤 " + ex.Message); req.Sample = null; }
                finally { def.Complete(); }
            });
        }

        // ================================================================
        //  聲音（電腦播出來的聲音）
        // ================================================================

        private void StartAudio()
        {
            try
            {
                _wavPath = Path.ChangeExtension(_videoPath, ".wav");
                _loop = new WasapiLoopbackCapture();
                _wav = new WaveFileWriter(_wavPath, _loop.WaveFormat);
                _loop.DataAvailable += (s, a) => { try { _wav?.Write(a.Buffer, 0, a.BytesRecorded); } catch { } };
                // 沒有聲音時 loopback 不會給資料，影音會對不上：同時播一段「靜音」讓它一直有資料
                try
                {
                    _silence = new WasapiOut(AudioClientShareMode.Shared, 100);
                    _silence.Init(new SilenceProvider(_loop.WaveFormat));
                    _silence.Play();
                }
                catch (Exception ex) { Log.Write("Recorder: 靜音保持失敗 " + ex.Message); }
                _loop.StartRecording();
            }
            catch (Exception ex)
            {
                AudioError = "沒辦法錄電腦的聲音：" + ex.Message;
                Log.Write("Recorder: " + AudioError);
                StopAudio();
                _wavPath = null;
            }
        }

        private void StopAudio()
        {
            try { _loop?.StopRecording(); } catch { }
            Thread.Sleep(150);
            try { _wav?.Dispose(); } catch { }
            try { _loop?.Dispose(); } catch { }
            try { _silence?.Stop(); _silence?.Dispose(); } catch { }
            _wav = null; _loop = null; _silence = null;
        }

        private static async Task MuxAsync(string video, string wav, string output)
        {
            var comp = new MediaComposition();
            comp.Clips.Add(await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(video)));
            comp.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(wav)));
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output));
            var dst = await folder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.ReplaceExisting);
            var r = await comp.RenderToFileAsync(dst, MediaTrimmingPreference.Fast);
            if (r != Windows.Media.Transcoding.TranscodeFailureReason.None) throw new InvalidOperationException(r.ToString());
        }

        private static void TryDelete(string p) { try { if (p != null && File.Exists(p)) File.Delete(p); } catch { } }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
            StopAudio();
        }

        // ================================================================
        //  Win32
        // ================================================================

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            public int bmiColors;
        }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
        [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO ii);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    }
}
