using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using SharpDoc = PdfSharp.Pdf.PdfDocument;
using WinPdfDoc = Windows.Data.Pdf.PdfDocument;

namespace IslandBar.Conversion
{
    /// <summary>
    /// PDF：用 Windows 內建的 PDF 繪製（Windows.Data.Pdf）轉圖片，
    /// 用 PDFsharp 合併 / 拆頁 / 旋轉 / 去隱私 / 圖片轉 PDF，用 PdfPig 擷取文字。
    /// </summary>
    public static class PdfOps
    {
        public sealed class RenderedPage
        {
            public byte[] Png { get; set; }
            public double WidthDip { get; set; }
            public double HeightDip { get; set; }
        }

        public static async Task<List<RenderedPage>> RenderPagesAsync(string path, double scale, IProgress<double> progress, double progressSpan = 1.0)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            WinPdfDoc doc;
            try { doc = await WinPdfDoc.LoadFromFileAsync(file); }
            catch (Exception ex) { throw new InvalidOperationException("無法開啟 PDF（可能有密碼保護）：" + ex.Message); }

            var pages = new List<RenderedPage>();
            for (uint i = 0; i < doc.PageCount; i++)
            {
                using var page = doc.GetPage(i);
                double w = page.Size.Width, h = page.Size.Height;
                double s = Math.Min(scale, 2600.0 / Math.Max(1, w)); // 寬度上限 2600 px
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, Math.Round(w * s)),
                    DestinationHeight = (uint)Math.Max(1, Math.Round(h * s))
                };
                using var ras = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(ras, options); // 預設輸出 PNG
                ras.Seek(0);
                using var ms = new MemoryStream();
                using (var rs = ras.AsStreamForRead()) await rs.CopyToAsync(ms);
                pages.Add(new RenderedPage { Png = ms.ToArray(), WidthDip = w, HeightDip = h });
                progress?.Report(progressSpan * (i + 1.0) / doc.PageCount);
            }
            return pages;
        }

        private static IReadOnlyList<string> Pdfs(IReadOnlyList<string> files) =>
            files.Where(f => FileOps.Ext(f) == "pdf").ToList();

        // ---------------- 轉圖片 ----------------

        public static async Task<ConvertResult> ToImages(IReadOnlyList<string> files, string fmt, IProgress<double> progress)
        {
            string last = null;
            int count = 0;
            var pdfs = Pdfs(files);
            for (int fi = 0; fi < pdfs.Count; fi++)
            {
                string f = pdfs[fi];
                var pages = await RenderPagesAsync(f, 2.0, null);
                string name = Path.GetFileNameWithoutExtension(f);
                string dir = Path.GetDirectoryName(f);
                if (pages.Count == 1)
                {
                    last = FileOps.OutputPath(f, fmt);
                    await WriteImage(pages[0].Png, last, fmt);
                }
                else
                {
                    string folder = FileOps.UniqueFolder(dir, name + "_頁面");
                    for (int i = 0; i < pages.Count; i++)
                        await WriteImage(pages[i].Png, Path.Combine(folder, $"{name}_p{i + 1:00}.{fmt}"), fmt);
                    last = folder;
                }
                count += pages.Count;
                progress?.Report((fi + 1.0) / pdfs.Count);
            }
            return new ConvertResult { OutputPath = last, Message = $"已轉成 {fmt.ToUpperInvariant()}（{count} 頁）" };
        }

        private static Task WriteImage(byte[] png, string path, string fmt)
        {
            if (fmt == "png") { File.WriteAllBytes(path, png); return Task.CompletedTask; }
            return Sta.Run(() => { File.WriteAllBytes(path, ImageOps.Encode(ImageOps.LoadBytes(png), fmt, 90)); return true; });
        }

        public static async Task<ConvertResult> LongImage(IReadOnlyList<string> files, IProgress<double> progress)
        {
            string f = Pdfs(files)[0];
            var pages = await RenderPagesAsync(f, 1.5, progress, 0.8);
            string outPath = FileOps.OutputPath(f, "jpg", "_長圖");
            await Sta.Run(() =>
            {
                var imgs = pages.Select(p => ImageOps.LoadBytes(p.Png)).ToList();
                File.WriteAllBytes(outPath, ImageOps.Encode(ImageOps.Stack(imgs), "jpg", 88));
                return true;
            });
            progress?.Report(1);
            return new ConvertResult { OutputPath = outPath, Message = $"已轉成長圖（{pages.Count} 頁）：{Path.GetFileName(outPath)}" };
        }

        // ---------------- 文字 ----------------

        public static Task<ConvertResult> ToText(IReadOnlyList<string> files, IProgress<double> progress)
        {
            return Task.Run(() =>
            {
                string last = null;
                var pdfs = Pdfs(files);
                int chars = 0;
                for (int fi = 0; fi < pdfs.Count; fi++)
                {
                    string f = pdfs[fi];
                    var sb = new StringBuilder();
                    using (var doc = UglyToad.PdfPig.PdfDocument.Open(f))
                    {
                        foreach (var page in doc.GetPages())
                        {
                            sb.AppendLine(PageText(page));
                            sb.AppendLine();
                        }
                    }
                    string text = sb.ToString().Trim();
                    if (text.Length == 0)
                        throw new InvalidOperationException("這份 PDF 沒有文字層（可能是掃描檔），請改用第 1 頁的「識字 OCR」");
                    last = FileOps.OutputPath(f, "txt");
                    File.WriteAllText(last, text, Encoding.UTF8);
                    chars += text.Length;
                    progress?.Report((fi + 1.0) / pdfs.Count);
                }
                return new ConvertResult { OutputPath = last, Message = $"已擷取文字（{chars} 字）：{Path.GetFileName(last)}" };
            });
        }

        /// <summary>把字依照座標排成一行一行</summary>
        private static string PageText(UglyToad.PdfPig.Content.Page page)
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0) return "";
            var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
            foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
            {
                var line = lines.LastOrDefault();
                if (line != null)
                {
                    var refWord = line[0];
                    double tol = Math.Max(2, Math.Min(refWord.BoundingBox.Height, w.BoundingBox.Height) * 0.5);
                    if (Math.Abs(refWord.BoundingBox.Bottom - w.BoundingBox.Bottom) <= tol) { line.Add(w); continue; }
                }
                lines.Add(new List<UglyToad.PdfPig.Content.Word> { w });
            }
            var sb = new StringBuilder();
            foreach (var line in lines)
                sb.AppendLine(string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            return sb.ToString().TrimEnd();
        }

        public static async Task<ConvertResult> Ocr(IReadOnlyList<string> files, IProgress<double> progress)
        {
            string f = Pdfs(files)[0];
            var pages = await RenderPagesAsync(f, 2.0, progress, 0.4);
            var sb = new StringBuilder();
            for (int i = 0; i < pages.Count; i++)
            {
                if (pages.Count > 1) sb.AppendLine($"=== 第 {i + 1} 頁 ===");
                sb.AppendLine(await OcrOps.RecognizeAsync(pages[i].Png));
                sb.AppendLine();
                progress?.Report(0.4 + 0.6 * (i + 1.0) / pages.Count);
            }
            string text = sb.ToString().Trim();
            string outPath = FileOps.OutputPath(f, "txt", "_OCR");
            File.WriteAllText(outPath, text, Encoding.UTF8);
            FileOps.SetClipboardText(text);
            return new ConvertResult { OutputPath = outPath, Message = $"已辨識 {pages.Count} 頁，文字也複製到剪貼簿了" };
        }

        // ---------------- PDFsharp：合併 / 拆頁 / 旋轉 / 去隱私 ----------------

        public static Task<ConvertResult> Merge(IReadOnlyList<string> files, IProgress<double> progress)
        {
            return Task.Run(() =>
            {
                var pdfs = Pdfs(files).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (pdfs.Count < 2) throw new InvalidOperationException("請一次拖兩個以上的 PDF 來合併");
                using var output = new SharpDoc();
                for (int i = 0; i < pdfs.Count; i++)
                {
                    using var input = PdfReader.Open(pdfs[i], PdfDocumentOpenMode.Import);
                    foreach (var page in input.Pages) output.AddPage(page);
                    progress?.Report((i + 1.0) / (pdfs.Count + 1));
                }
                string outPath = FileOps.OutputPath(pdfs[0], "pdf", "_合併");
                output.Save(outPath);
                progress?.Report(1);
                return new ConvertResult { OutputPath = outPath, Message = $"已合併 {pdfs.Count} 個 PDF：{Path.GetFileName(outPath)}" };
            });
        }

        public static Task<ConvertResult> Split(IReadOnlyList<string> files, IProgress<double> progress)
        {
            return Task.Run(() =>
            {
                string f = Pdfs(files)[0];
                using var input = PdfReader.Open(f, PdfDocumentOpenMode.Import);
                string name = Path.GetFileNameWithoutExtension(f);
                string folder = FileOps.UniqueFolder(Path.GetDirectoryName(f), name + "_拆頁");
                for (int i = 0; i < input.PageCount; i++)
                {
                    using var one = new SharpDoc();
                    one.AddPage(input.Pages[i]);
                    one.Save(Path.Combine(folder, $"{name}_p{i + 1:00}.pdf"));
                    progress?.Report((i + 1.0) / input.PageCount);
                }
                return new ConvertResult { OutputPath = folder, Message = $"已拆成 {input.PageCount} 個檔案" };
            });
        }

        public static Task<ConvertResult> Rotate(IReadOnlyList<string> files, IProgress<double> progress)
        {
            return Task.Run(() =>
            {
                string last = null;
                var pdfs = Pdfs(files);
                for (int i = 0; i < pdfs.Count; i++)
                {
                    using var doc = PdfReader.Open(pdfs[i], PdfDocumentOpenMode.Modify);
                    foreach (var page in doc.Pages) page.Rotate = (page.Rotate + 90) % 360;
                    last = FileOps.OutputPath(pdfs[i], "pdf", "_旋轉");
                    doc.Save(last);
                    progress?.Report((i + 1.0) / pdfs.Count);
                }
                return new ConvertResult { OutputPath = last, Message = "已旋轉 90°：" + Path.GetFileName(last) };
            });
        }

        public static Task<ConvertResult> StripMetadata(IReadOnlyList<string> files, IProgress<double> progress)
        {
            return Task.Run(() =>
            {
                string last = null;
                var pdfs = Pdfs(files);
                for (int i = 0; i < pdfs.Count; i++)
                {
                    using var doc = PdfReader.Open(pdfs[i], PdfDocumentOpenMode.Modify);
                    doc.Info.Author = "";
                    doc.Info.Creator = "";
                    doc.Info.Subject = "";
                    doc.Info.Keywords = "";
                    doc.Info.Title = "";
                    last = FileOps.OutputPath(pdfs[i], "pdf", "_去隱私");
                    doc.Save(last);
                    progress?.Report((i + 1.0) / pdfs.Count);
                }
                return new ConvertResult { OutputPath = last, Message = "已移除作者等資訊：" + Path.GetFileName(last) };
            });
        }

        /// <summary>每頁轉成圖片再重組（壓縮 / 黑白），文字會變成不能選取</summary>
        public static async Task<ConvertResult> Rasterize(IReadOnlyList<string> files, bool grayscale, IProgress<double> progress)
        {
            string f = Pdfs(files)[0];
            var pages = await RenderPagesAsync(f, grayscale ? 1.6 : 1.3, progress, 0.7);
            string outPath = FileOps.OutputPath(f, "pdf", grayscale ? "_黑白" : "_壓縮");
            await Sta.Run(() =>
            {
                var list = new List<(byte[] jpeg, double widthPt, double heightPt)>();
                foreach (var p in pages)
                {
                    BitmapSource img = ImageOps.Flatten(ImageOps.LoadBytes(p.Png));
                    if (grayscale) img = new FormatConvertedBitmap(img, PixelFormats.Gray8, null, 0);
                    list.Add((ImageOps.Encode(img, "jpg", grayscale ? 80 : 65), p.WidthDip * 72 / 96, p.HeightDip * 72 / 96));
                }
                BuildPdfFromJpegs(list, outPath);
                return true;
            });
            progress?.Report(1);
            long before = new FileInfo(f).Length, after = new FileInfo(outPath).Length;
            string size = grayscale ? "" : $"（{before / 1024} KB → {after / 1024} KB）";
            return new ConvertResult { OutputPath = outPath, Message = (grayscale ? "已轉黑白" : "已壓縮") + size };
        }

        public static void BuildPdfFromJpegs(IList<(byte[] jpeg, double widthPt, double heightPt)> pages, string outPath)
        {
            using var doc = new SharpDoc();
            foreach (var (jpeg, w, h) in pages)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(w);
                page.Height = XUnit.FromPoint(h);
                using var gfx = XGraphics.FromPdfPage(page);
                using var ms = new MemoryStream(jpeg);
                using var img = XImage.FromStream(ms);
                gfx.DrawImage(img, 0, 0, w, h);
            }
            doc.Save(outPath);
        }
    }
}
