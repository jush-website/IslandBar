using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar.Conversion
{
    /// <summary>圖片轉換（WPF / WIC：PNG、JPG、BMP、TIFF、GIF 可讀寫；WebP、HEIC 等裝了 Windows 擴充功能就能讀）</summary>
    public static class ImageOps
    {
        private static readonly HashSet<string> Writable = new HashSet<string> { "png", "jpg", "jpeg", "bmp", "tif", "tiff", "gif" };

        // ---------------- 讀取 / 寫入 ----------------

        public static BitmapSource Load(string path)
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            BitmapSource img = ApplyOrientation(dec.Frames[0]);
            if (img.CanFreeze) img.Freeze();
            return img;
        }

        public static BitmapSource LoadBytes(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            BitmapSource img = dec.Frames[0];
            if (img.CanFreeze) img.Freeze();
            return img;
        }

        /// <summary>依 EXIF 方向轉正（之後重新編碼會拿掉 EXIF，所以要先轉）</summary>
        private static BitmapSource ApplyOrientation(BitmapFrame frame)
        {
            int orientation = 1;
            try
            {
                if (frame.Metadata is BitmapMetadata md && md.ContainsQuery("/app1/ifd/{ushort=274}"))
                {
                    object v = md.GetQuery("/app1/ifd/{ushort=274}");
                    if (v is ushort u) orientation = u;
                }
            }
            catch { }

            double angle = orientation switch { 3 => 180, 4 => 180, 5 => 90, 6 => 90, 7 => 270, 8 => 270, _ => 0 };
            return angle == 0 ? (BitmapSource)frame : new TransformedBitmap(frame, new RotateTransform(angle));
        }

        private static bool HasAlpha(BitmapSource s)
        {
            var f = s.Format;
            return f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Prgba64 ||
                   f == PixelFormats.Rgba64 || f == PixelFormats.Prgba128Float || f == PixelFormats.Rgba128Float ||
                   f == PixelFormats.Indexed8 || f == PixelFormats.Indexed4 || f == PixelFormats.Indexed2 || f == PixelFormats.Indexed1;
        }

        /// <summary>透明的地方鋪上白底（JPG / BMP 不支援透明）</summary>
        public static BitmapSource Flatten(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.DrawImage(src, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var bgr = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
            bgr.Freeze();
            return bgr;
        }

        public static byte[] Encode(BitmapSource src, string fmt, int jpegQuality = 92)
        {
            fmt = fmt.ToLowerInvariant();
            BitmapEncoder enc;
            switch (fmt)
            {
                case "jpg":
                case "jpeg":
                    if (HasAlpha(src)) src = Flatten(src);
                    enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) };
                    break;
                case "bmp":
                    if (HasAlpha(src)) src = Flatten(src);
                    enc = new BmpBitmapEncoder();
                    break;
                case "tif":
                case "tiff":
                    enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
                    break;
                case "gif":
                    enc = new GifBitmapEncoder();
                    break;
                default:
                    enc = new PngBitmapEncoder();
                    break;
            }
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        private static string SameOrPng(string path)
        {
            string ext = FileOps.Ext(path);
            if (ext == "jpeg") ext = "jpg";
            if (ext == "tif") ext = "tiff";
            return Writable.Contains(ext) ? ext : "png";
        }

        // ---------------- 動作 ----------------

        private static Task<ConvertResult> PerFile(IReadOnlyList<string> files, IProgress<double> progress,
            Func<string, string> work, string verb)
        {
            return Sta.Run(() =>
            {
                string last = null;
                for (int i = 0; i < files.Count; i++)
                {
                    last = work(files[i]);
                    progress?.Report((i + 1.0) / files.Count);
                }
                return new ConvertResult
                {
                    OutputPath = last,
                    Message = files.Count == 1 ? $"{verb}：{Path.GetFileName(last)}" : $"{verb} {files.Count} 張圖片"
                };
            });
        }

        public static Task<ConvertResult> ConvertTo(IReadOnlyList<string> files, string fmt, IProgress<double> progress) =>
            PerFile(files, progress, f =>
            {
                string outPath = FileOps.OutputPath(f, fmt);
                File.WriteAllBytes(outPath, Encode(Load(f), fmt));
                return outPath;
            }, "已轉成 " + fmt.ToUpperInvariant());

        public static Task<ConvertResult> Compress(IReadOnlyList<string> files, IProgress<double> progress) =>
            PerFile(files, progress, f =>
            {
                BitmapSource img = Load(f);
                const int maxSide = 2560;
                int longSide = Math.Max(img.PixelWidth, img.PixelHeight);
                if (longSide > maxSide)
                {
                    double s = (double)maxSide / longSide;
                    img = new TransformedBitmap(img, new ScaleTransform(s, s));
                }
                string outPath = FileOps.OutputPath(f, "jpg", "_壓縮");
                File.WriteAllBytes(outPath, Encode(img, "jpg", 75));
                return outPath;
            }, "已壓縮");

        public static Task<ConvertResult> HalfSize(IReadOnlyList<string> files, IProgress<double> progress) =>
            PerFile(files, progress, f =>
            {
                var img = new TransformedBitmap(Load(f), new ScaleTransform(0.5, 0.5));
                string fmt = SameOrPng(f);
                string outPath = FileOps.OutputPath(f, fmt, "_縮小");
                File.WriteAllBytes(outPath, Encode(img, fmt));
                return outPath;
            }, "已縮小一半");

        public static Task<ConvertResult> Grayscale(IReadOnlyList<string> files, IProgress<double> progress) =>
            PerFile(files, progress, f =>
            {
                BitmapSource img = Load(f);
                if (HasAlpha(img)) img = Flatten(img);
                var gray = new FormatConvertedBitmap(img, PixelFormats.Gray8, null, 0);
                string fmt = SameOrPng(f);
                string outPath = FileOps.OutputPath(f, fmt, "_黑白");
                File.WriteAllBytes(outPath, Encode(gray, fmt));
                return outPath;
            }, "已轉黑白");

        public static Task<ConvertResult> Rotate(IReadOnlyList<string> files, IProgress<double> progress) =>
            PerFile(files, progress, f =>
            {
                var img = new TransformedBitmap(Load(f), new RotateTransform(90));
                string fmt = SameOrPng(f);
                string outPath = FileOps.OutputPath(f, fmt, "_旋轉");
                File.WriteAllBytes(outPath, Encode(img, fmt, 95));
                return outPath;
            }, "已旋轉 90°");

        /// <summary>重新編碼、不帶任何 EXIF（GPS、相機型號、拍攝時間都會拿掉）</summary>
        public static Task<ConvertResult> StripMetadata(IReadOnlyList<string> files, IProgress<double> progress) =>
            PerFile(files, progress, f =>
            {
                string fmt = SameOrPng(f);
                string outPath = FileOps.OutputPath(f, fmt, "_去隱私");
                File.WriteAllBytes(outPath, Encode(Load(f), fmt, 95));
                return outPath;
            }, "已移除隱私資訊");

        public static Task<ConvertResult> ToIco(IReadOnlyList<string> files, IProgress<double> progress) =>
            PerFile(files, progress, f =>
            {
                BitmapSource img = Load(f);
                int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
                var pngs = new List<byte[]>();
                foreach (int size in sizes)
                {
                    double s = Math.Min((double)size / img.PixelWidth, (double)size / img.PixelHeight);
                    double w = img.PixelWidth * s, h = img.PixelHeight * s;
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
                        dc.DrawImage(img, new Rect((size - w) / 2, (size - h) / 2, w, h));
                    }
                    var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    pngs.Add(Encode(rtb, "png"));
                }

                string outPath = FileOps.OutputPath(f, "ico");
                using (var fs = File.Create(outPath))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
                    int offset = 6 + 16 * sizes.Length;
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                        bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                        bw.Write((byte)0); bw.Write((byte)0);
                        bw.Write((ushort)1); bw.Write((ushort)32);
                        bw.Write(pngs[i].Length); bw.Write(offset);
                        offset += pngs[i].Length;
                    }
                    foreach (var p in pngs) bw.Write(p);
                }
                return outPath;
            }, "已轉成 ICO");

        /// <summary>多張圖片（或 PDF 各頁）由上到下拼成一張長圖</summary>
        public static BitmapSource Stack(IList<BitmapSource> images)
        {
            int width = Math.Min(2000, images.Max(i => i.PixelWidth));
            var scaled = images.Select(i => (img: i, h: (int)Math.Round(i.PixelHeight * (double)width / i.PixelWidth))).ToList();
            int total = scaled.Sum(x => x.h);
            const int maxHeight = 30000;
            double shrink = total > maxHeight ? (double)maxHeight / total : 1.0;
            int outW = Math.Max(1, (int)(width * shrink));
            int outH = Math.Max(1, (int)(total * shrink));

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, outW, outH));
                double y = 0;
                foreach (var (img, h) in scaled)
                {
                    double hh = h * shrink;
                    dc.DrawImage(img, new Rect(0, y, outW, hh));
                    y += hh;
                }
            }
            var rtb = new RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        public static Task<ConvertResult> LongImage(IReadOnlyList<string> files, IProgress<double> progress) =>
            Sta.Run(() =>
            {
                var ordered = files.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
                var imgs = new List<BitmapSource>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    imgs.Add(Load(ordered[i]));
                    progress?.Report((i + 1.0) / (ordered.Count + 1));
                }
                string outPath = FileOps.OutputPath(ordered[0], "jpg", "_長圖");
                File.WriteAllBytes(outPath, Encode(Stack(imgs), "jpg", 90));
                progress?.Report(1);
                return new ConvertResult { OutputPath = outPath, Message = $"已拼成長圖（{ordered.Count} 張）：{Path.GetFileName(outPath)}" };
            });

        public static Task<ConvertResult> ToPdf(IReadOnlyList<string> files, IProgress<double> progress) =>
            Sta.Run(() =>
            {
                var ordered = files.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
                var pages = new List<(byte[] jpeg, double widthPt, double heightPt)>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var img = Load(ordered[i]);
                    pages.Add((Encode(img, "jpg", 92), img.PixelWidth * 72.0 / 96, img.PixelHeight * 72.0 / 96));
                    progress?.Report((i + 1.0) / (ordered.Count + 1));
                }
                string suffix = ordered.Count > 1 ? $"_等{ordered.Count}張" : null;
                string outPath = FileOps.OutputPath(ordered[0], "pdf", suffix);
                PdfOps.BuildPdfFromJpegs(pages, outPath);
                progress?.Report(1);
                return new ConvertResult { OutputPath = outPath, Message = $"已轉成 PDF：{Path.GetFileName(outPath)}" };
            });

        public static Task<ConvertResult> CopyImage(IReadOnlyList<string> files)
        {
            string f = files[0];
            FileOps.OnUi(() =>
            {
                var img = Load(f);
                System.Windows.Clipboard.SetImage(img);
            });
            return Task.FromResult(new ConvertResult { Message = "圖片已複製到剪貼簿" });
        }

        public static async Task<ConvertResult> Ocr(IReadOnlyList<string> files, IProgress<double> progress)
        {
            var all = new System.Text.StringBuilder();
            string lastOut = null;
            for (int i = 0; i < files.Count; i++)
            {
                byte[] bytes = await Sta.Run(() => Encode(Load(files[i]), "png"));
                string text = await OcrOps.RecognizeAsync(bytes);
                lastOut = FileOps.OutputPath(files[i], "txt", "_文字");
                File.WriteAllText(lastOut, text, System.Text.Encoding.UTF8);
                if (files.Count > 1) all.AppendLine($"=== {Path.GetFileName(files[i])} ===");
                all.AppendLine(text);
                progress?.Report((i + 1.0) / files.Count);
            }
            string result = all.ToString().Trim();
            if (result.Length == 0) throw new InvalidOperationException("沒有辨識到文字");
            FileOps.SetClipboardText(result);
            return new ConvertResult { OutputPath = lastOut, Message = $"已辨識 {result.Length} 字，並複製到剪貼簿" };
        }
    }
}
