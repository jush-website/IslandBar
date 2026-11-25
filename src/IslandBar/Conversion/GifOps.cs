using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace IslandBar.Conversion
{
    /// <summary>
    /// 影片 → GIF 動圖（螢幕錄影錄完最常用）：每秒 10 張、寬 640，最長 30 秒。
    /// 用 Windows 的影片合成取畫面，WPF 的 GIF 編碼器做每一張，再自己組成會循環播放的動圖。
    /// </summary>
    public static class GifOps
    {
        public const int Fps = 10, MaxWidth = 640;
        public static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(30);

        public static async Task<ConvertResult> ToGif(IReadOnlyList<string> files, IProgress<double> progress)
        {
            string src = files[0];
            var comp = new MediaComposition();
            var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(src));
            comp.Clips.Add(clip);
            var dur = clip.OriginalDuration;
            bool cut = dur > MaxLength;
            if (cut) dur = MaxLength;
            var props = clip.GetVideoEncodingProperties();
            double ratio = props.Width > 0 ? (double)props.Height / props.Width : 9.0 / 16;
            int w = (int)Math.Min(MaxWidth, props.Width > 0 ? props.Width : MaxWidth);
            int h = Math.Max(2, (int)Math.Round(w * ratio));

            var times = new List<TimeSpan>();
            for (var t = TimeSpan.Zero; t < dur; t += TimeSpan.FromSeconds(1.0 / Fps)) times.Add(t);
            var frames = new List<byte[]>();
            for (int i = 0; i < times.Count; i += 20)
            {
                var batch = times.Skip(i).Take(20).ToList();
                var thumbs = await comp.GetThumbnailsAsync(batch, w, h, VideoFramePrecision.NearestFrame);
                foreach (var th in thumbs)
                {
                    using var s = th.AsStreamForRead();
                    var dec = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    frames.Add(EncodeFrame(dec.Frames[0]));
                }
                progress?.Report(Math.Min(0.95, (double)(i + batch.Count) / times.Count));
            }
            if (frames.Count == 0) throw new InvalidOperationException("影片裡讀不到畫面");
            string outPath = FileOps.OutputPath(src, "gif", "");
            File.WriteAllBytes(outPath, Animate(frames, 100 / Fps));
            progress?.Report(1);
            return new ConvertResult { OutputPath = outPath, Message = $"GIF 動圖：{Path.GetFileName(outPath)}" + (cut ? "（只轉前 30 秒）" : "") };
        }

        private static byte[] EncodeFrame(BitmapSource bmp)
        {
            var enc = new GifBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        /// <summary>把好幾張單張 GIF 組成循環播放的動圖（每張的調色盤改放在自己的區塊，顏色才不會亂）</summary>
        public static byte[] Animate(List<byte[]> gifs, int delayCs)
        {
            using var o = new MemoryStream();
            var first = gifs[0];
            o.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
            o.Write(first, 6, 4);                    // 寬、高
            o.WriteByte((byte)(first[10] & 0x70));   // 不用全域調色盤
            o.WriteByte(0); o.WriteByte(0);
            o.Write(new byte[] { 0x21, 0xFF, 0x0B }); // NETSCAPE2.0：無限循環
            o.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            o.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
            foreach (var g in gifs)
            {
                int packed = g[10];
                int pos = 13;
                byte[] gct = null;
                if ((packed & 0x80) != 0)
                {
                    int size = 3 << ((packed & 7) + 1);
                    gct = new byte[size];
                    Array.Copy(g, pos, gct, 0, size);
                    pos += size;
                }
                // 跳過擴充區塊，找到圖片
                while (pos < g.Length && g[pos] == 0x21)
                {
                    pos += 2;
                    while (pos < g.Length && g[pos] != 0) pos += g[pos] + 1;
                    pos++;
                }
                if (pos >= g.Length || g[pos] != 0x2C) continue;
                // 每張的停留時間
                o.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04, (byte)(delayCs & 0xFF), (byte)(delayCs >> 8), 0x00, 0x00 });
                var desc = new byte[10];
                Array.Copy(g, pos, desc, 0, 10);
                pos += 10;
                byte[] lct = null;
                if ((desc[9] & 0x80) != 0)
                {
                    int size = 3 << ((desc[9] & 7) + 1);
                    lct = new byte[size];
                    Array.Copy(g, pos, lct, 0, size);
                    pos += size;
                }
                else if (gct != null)
                {
                    lct = gct;
                    desc[9] = (byte)((desc[9] & 0x40) | 0x80 | (packed & 7));
                }
                o.Write(desc);
                if (lct != null) o.Write(lct);
                int start = pos;
                pos++;                                   // LZW 最小碼長
                while (pos < g.Length && g[pos] != 0) pos += g[pos] + 1;
                pos++;                                   // 結尾的 0
                o.Write(g, start, Math.Min(pos, g.Length) - start);
            }
            o.WriteByte(0x3B);
            return o.ToArray();
        }
    }
}
