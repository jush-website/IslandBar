using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace IslandBar.Conversion
{
    /// <summary>影片 / 音訊：用 Windows 內建的 Media Foundation 轉檔（MediaTranscoder）</summary>
    public static class MediaOps
    {
        public static async Task<ConvertResult> Transcode(IReadOnlyList<string> files, string ext, string suffix,
            Func<MediaEncodingProfile, MediaEncodingProfile, MediaEncodingProfile> makeProfile, string verb, IProgress<double> progress)
        {
            string last = null;
            for (int i = 0; i < files.Count; i++)
            {
                int index = i;
                var p = new Progress<double>(v => progress?.Report((index + v) / files.Count));
                last = await TranscodeOne(files[i], ext, suffix, makeProfile, p);
            }
            return new ConvertResult
            {
                OutputPath = last,
                Message = files.Count == 1 ? $"{verb}：{Path.GetFileName(last)}" : $"{verb} {files.Count} 個檔案"
            };
        }

        private static async Task<string> TranscodeOne(string src, string ext, string suffix,
            Func<MediaEncodingProfile, MediaEncodingProfile, MediaEncodingProfile> makeProfile, IProgress<double> progress)
        {
            var srcFile = await StorageFile.GetFileFromPathAsync(src);
            MediaEncodingProfile srcProfile = null;
            try { srcProfile = await MediaEncodingProfile.CreateFromFileAsync(srcFile); } catch { }
            var profile = makeProfile(srcProfile, null);

            string outPath = FileOps.OutputPath(src, ext, suffix);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outPath));
            var dst = await folder.CreateFileAsync(Path.GetFileName(outPath), CreationCollisionOption.GenerateUniqueName);

            try
            {
                var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
                var prep = await transcoder.PrepareFileTranscodeAsync(srcFile, dst, profile);
                if (!prep.CanTranscode)
                    throw new InvalidOperationException(Reason(prep.FailureReason));
                await prep.TranscodeAsync().AsTask(new Progress<double>(v => progress?.Report(v / 100.0)));
                return dst.Path;
            }
            catch
            {
                try { await dst.DeleteAsync(); } catch { }
                throw;
            }
        }

        private static string Reason(TranscodeFailureReason r) => r switch
        {
            TranscodeFailureReason.CodecNotFound => "Windows 沒有這個格式的編碼器（HEVC / WebM 等可能需要從 Microsoft Store 安裝擴充功能）",
            TranscodeFailureReason.InvalidProfile => "這個檔案不支援轉成此格式",
            _ => "Windows 無法轉換這個檔案"
        };

        /// <summary>保持長寬比，把高度縮到 targetHeight（原本比較小就不放大）</summary>
        private static void FitHeight(MediaEncodingProfile profile, MediaEncodingProfile src, uint targetHeight)
        {
            if (profile.Video == null) return;
            if (src?.Video == null || src.Video.Height == 0)
            {
                profile.Video.Height = targetHeight;
                profile.Video.Width = targetHeight * 16 / 9;
                return;
            }
            uint h = Math.Min(targetHeight, src.Video.Height);
            uint w = (uint)Math.Round(src.Video.Width * (double)h / src.Video.Height);
            profile.Video.Width = w / 2 * 2;
            profile.Video.Height = h / 2 * 2;
        }

        // ---------------- 影片 ----------------

        public static Task<ConvertResult> ToMp4(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "mp4", null, (src, _) => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto), "已轉成 MP4", p);

        public static Task<ConvertResult> ToHeight(IReadOnlyList<string> f, uint height, IProgress<double> p) =>
            Transcode(f, "mp4", $"_{height}p", (src, _) =>
            {
                var prof = MediaEncodingProfile.CreateMp4(height >= 720 ? VideoEncodingQuality.HD720p : VideoEncodingQuality.Wvga);
                FitHeight(prof, src, height);
                return prof;
            }, $"已轉成 {height}P", p);

        public static Task<ConvertResult> ToHevc(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "mp4", "_HEVC", (src, _) => MediaEncodingProfile.CreateHevc(VideoEncodingQuality.Auto), "已轉成 HEVC", p);

        public static Task<ConvertResult> ToWmv(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "wmv", null, (src, _) => MediaEncodingProfile.CreateWmv(VideoEncodingQuality.Auto), "已轉成 WMV", p);

        public static Task<ConvertResult> CompressVideo(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "mp4", "_壓縮", (src, _) =>
            {
                var prof = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
                if (src?.Video != null)
                {
                    FitHeight(prof, src, 1080);
                    uint srcRate = src.Video.Bitrate > 0 ? src.Video.Bitrate : 8_000_000;
                    prof.Video.Bitrate = Math.Max(800_000u, (uint)(srcRate * 0.45));
                }
                if (prof.Audio != null) prof.Audio.Bitrate = 128_000;
                return prof;
            }, "已壓縮", p);

        public static Task<ConvertResult> Mute(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "mp4", "_靜音", (src, _) =>
            {
                var prof = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
                prof.Audio = null;
                return prof;
            }, "已移除聲音", p);

        public static async Task<ConvertResult> Thumbnail(IReadOnlyList<string> files, IProgress<double> progress)
        {
            string last = null;
            for (int i = 0; i < files.Count; i++)
            {
                var file = await StorageFile.GetFileFromPathAsync(files[i]);
                var clip = await MediaClip.CreateFromFileAsync(file);
                var comp = new MediaComposition();
                comp.Clips.Add(clip);
                var props = clip.GetVideoEncodingProperties();
                var at = clip.OriginalDuration.TotalSeconds > 3 ? TimeSpan.FromSeconds(1) : TimeSpan.Zero;
                using var img = await comp.GetThumbnailAsync(at, (int)props.Width, (int)props.Height, VideoFramePrecision.NearestFrame);
                last = FileOps.OutputPath(files[i], "jpg", "_封面");
                using (var outFs = File.Create(last))
                using (var src = img.AsStreamForRead())
                    await src.CopyToAsync(outFs);
                progress?.Report((i + 1.0) / files.Count);
            }
            return new ConvertResult { OutputPath = last, Message = "已截取封面：" + Path.GetFileName(last) };
        }

        // ---------------- 音訊（影片也可以，會只取聲音） ----------------

        public static Task<ConvertResult> ToMp3(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "mp3", null, (s, _) => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High), "已轉成 MP3", p);

        public static Task<ConvertResult> ToM4a(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "m4a", null, (s, _) => MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High), "已轉成 M4A", p);

        public static Task<ConvertResult> ToWav(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "wav", null, (s, _) => MediaEncodingProfile.CreateWav(AudioEncodingQuality.High), "已轉成 WAV", p);

        public static Task<ConvertResult> ToWma(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "wma", null, (s, _) => MediaEncodingProfile.CreateWma(AudioEncodingQuality.High), "已轉成 WMA", p);

        public static Task<ConvertResult> ToFlac(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "flac", null, (s, _) => MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High), "已轉成 FLAC", p);

        public static Task<ConvertResult> ToAlac(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "m4a", "_ALAC", (s, _) => MediaEncodingProfile.CreateAlac(AudioEncodingQuality.High), "已轉成 ALAC", p);

        public static Task<ConvertResult> CompressAudio(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "mp3", "_壓縮", (s, _) => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.Medium), "已壓縮", p);

        public static Task<ConvertResult> Mono(IReadOnlyList<string> f, IProgress<double> p) =>
            Transcode(f, "mp3", "_單聲道", (s, _) =>
            {
                var prof = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High);
                prof.Audio.ChannelCount = 1;
                return prof;
            }, "已轉成單聲道", p);
    }
}
