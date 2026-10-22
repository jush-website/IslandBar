using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace IslandBar.Conversion
{
    public enum FileKind { Image, Pdf, Video, Audio, Document, Other }

    public sealed class WheelAction
    {
        public string Label { get; set; } = "";
        public string Sub { get; set; } = "";
        public string Glyph { get; set; } = "";
        public Func<IReadOnlyList<string>, IProgress<double>, Task<ConvertResult>> Run { get; set; }
    }

    /// <summary>依檔案類型決定輪盤上的格子（第 1 頁：格式轉換；第 2 頁：工具）</summary>
    public static class ConvertCatalog
    {
        private static readonly HashSet<string> ImageExt = new HashSet<string>
            { "png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "webp", "heic", "heif", "ico", "jxr", "wdp", "avif" };
        private static readonly HashSet<string> PdfExt = new HashSet<string> { "pdf" };
        private static readonly HashSet<string> VideoExt = new HashSet<string>
            { "mp4", "mov", "m4v", "mkv", "avi", "wmv", "webm", "3gp", "mpg", "mpeg", "ts", "mts", "m2ts" };
        private static readonly HashSet<string> DocExt = new HashSet<string> { "doc", "docx", "ppt", "pptx" };
        private static readonly HashSet<string> AudioExt = new HashSet<string>
            { "mp3", "m4a", "aac", "wav", "wma", "flac", "ogg", "opus", "aiff", "aif", "alac", "amr" };

        public static FileKind KindOf(IReadOnlyList<string> files)
        {
            if (files.Count == 0 || files.Any(Directory.Exists)) return FileKind.Other;
            var kinds = files.Select(f =>
            {
                string e = FileOps.Ext(f);
                if (ImageExt.Contains(e)) return FileKind.Image;
                if (PdfExt.Contains(e)) return FileKind.Pdf;
                if (VideoExt.Contains(e)) return FileKind.Video;
                if (AudioExt.Contains(e)) return FileKind.Audio;
                if (DocExt.Contains(e)) return FileKind.Document;
                return FileKind.Other;
            }).Distinct().ToList();
            return kinds.Count == 1 ? kinds[0] : FileKind.Other;
        }

        public static string KindName(FileKind k) => k switch
        {
            FileKind.Image => "圖片",
            FileKind.Pdf => "PDF",
            FileKind.Video => "影片",
            FileKind.Audio => "音訊",
            FileKind.Document => "文件",
            _ => "檔案"
        };

        public static string KindGlyph(FileKind k) => k switch
        {
            FileKind.Image => G(0xEB9F),
            FileKind.Pdf => G(0xE8A5),
            FileKind.Video => G(0xE714),
            FileKind.Audio => G(0xE8D6),
            FileKind.Document => G(0xE8A5),
            _ => G(0xE8B7)
        };

        private static string G(int code) => char.ConvertFromUtf32(code);

        private static WheelAction A(string label, string sub, int glyph,
            Func<IReadOnlyList<string>, IProgress<double>, Task<ConvertResult>> run) =>
            new WheelAction { Label = label, Sub = sub, Glyph = G(glyph), Run = run };

        // 通用工具
        private static WheelAction Zip => A("ZIP", "壓縮檔", 0xE7B8, (f, p) => FileOps.Zip(f));
        private static WheelAction CopyPath => A("複製路徑", "到剪貼簿", 0xE8C8, (f, p) => FileOps.CopyPaths(f));
        private static WheelAction CopyName => A("複製檔名", "到剪貼簿", 0xE8AC, (f, p) => FileOps.CopyNames(f));
        private static WheelAction Hash => A("SHA-256", "雜湊值", 0xE8D7, (f, p) => FileOps.Sha256(f));

        public static (List<WheelAction> page1, List<WheelAction> page2) For(IReadOnlyList<string> files)
        {
            switch (KindOf(files))
            {
                case FileKind.Image:
                    return (new List<WheelAction>
                    {
                        A("PNG", "無損 · 透明", 0xEB9F, (f, p) => ImageOps.ConvertTo(f, "png", p)),
                        A("JPG", "體積小", 0xEB9F, (f, p) => ImageOps.ConvertTo(f, "jpg", p)),
                        A("PDF", files.Count > 1 ? "多張合一" : "文件", 0xE8A5, (f, p) => ImageOps.ToPdf(f, p)),
                        A("長圖", "上下拼接", 0xE8A9, (f, p) => ImageOps.LongImage(f, p)),
                        A("ICO", "程式圖示", 0xE91B, (f, p) => ImageOps.ToIco(f, p)),
                        A("GIF", "相容性高", 0xEB9F, (f, p) => ImageOps.ConvertTo(f, "gif", p)),
                        A("TIFF", "印刷用", 0xE749, (f, p) => ImageOps.ConvertTo(f, "tiff", p)),
                        A("BMP", "不壓縮", 0xEB9F, (f, p) => ImageOps.ConvertTo(f, "bmp", p)),
                    }, new List<WheelAction>
                    {
                        A("壓縮", "JPG 75%", 0xE73F, (f, p) => ImageOps.Compress(f, p)),
                        A("縮小一半", "長寬 50%", 0xE73F, (f, p) => ImageOps.HalfSize(f, p)),
                        A("識字", "OCR → 文字", 0xE8D2, (f, p) => ImageOps.Ocr(f, p)),
                        A("黑白", "灰階", 0xE790, (f, p) => ImageOps.Grayscale(f, p)),
                        A("旋轉", "順時針 90°", 0xE7AD, (f, p) => ImageOps.Rotate(f, p)),
                        A("去隱私", "移除 GPS / EXIF", 0xE72E, (f, p) => ImageOps.StripMetadata(f, p)),
                        A("複製圖片", "到剪貼簿", 0xE8C8, (f, p) => ImageOps.CopyImage(f)),
                        Zip,
                    });

                case FileKind.Pdf:
                    return (new List<WheelAction>
                    {
                        A("PNG", "每頁一張", 0xEB9F, (f, p) => PdfOps.ToImages(f, "png", p)),
                        A("JPG", "每頁一張", 0xEB9F, (f, p) => PdfOps.ToImages(f, "jpg", p)),
                        A("長圖", "全部頁接起來", 0xE8A9, (f, p) => PdfOps.LongImage(f, p)),
                        A("TXT", "擷取文字", 0xE8A5, (f, p) => PdfOps.ToText(f, p)),
                        A("識字", "掃描檔 OCR", 0xE8D2, (f, p) => PdfOps.Ocr(f, p)),
                        A("合併", files.Count > 1 ? $"{files.Count} 個合一" : "需多個 PDF", 0xE71D, (f, p) => PdfOps.Merge(f, p)),
                        A("拆頁", "每頁一個檔", 0xE8C6, (f, p) => PdfOps.Split(f, p)),
                        A("旋轉", "順時針 90°", 0xE7AD, (f, p) => PdfOps.Rotate(f, p)),
                    }, new List<WheelAction>
                    {
                        A("壓縮", "轉成圖片", 0xE73F, (f, p) => PdfOps.Rasterize(f, false, p)),
                        A("黑白", "灰階", 0xE790, (f, p) => PdfOps.Rasterize(f, true, p)),
                        A("去隱私", "移除作者資訊", 0xE72E, (f, p) => PdfOps.StripMetadata(f, p)),
                        Zip, CopyPath, Hash,
                    });

                case FileKind.Video:
                    return (new List<WheelAction>
                    {
                        A("MP4", "H.264", 0xE714, (f, p) => MediaOps.ToMp4(f, p)),
                        A("720P", "保持比例", 0xE7F4, (f, p) => MediaOps.ToHeight(f, 720, p)),
                        A("480P", "更小", 0xE7F4, (f, p) => MediaOps.ToHeight(f, 480, p)),
                        A("HEVC", "H.265", 0xE714, (f, p) => MediaOps.ToHevc(f, p)),
                        A("MP3", "只要聲音", 0xE8D6, (f, p) => MediaOps.ToMp3(f, p)),
                        A("M4A", "只要聲音", 0xE8D6, (f, p) => MediaOps.ToM4a(f, p)),
                        A("WAV", "無損聲音", 0xE8D6, (f, p) => MediaOps.ToWav(f, p)),
                        A("截封面", "JPG", 0xE722, (f, p) => MediaOps.Thumbnail(f, p)),
                    }, new List<WheelAction>
                    {
                        A("壓縮", "較小檔案", 0xE73F, (f, p) => MediaOps.CompressVideo(f, p)),
                        A("靜音", "移除聲音", 0xE74F, (f, p) => MediaOps.Mute(f, p)),
                        A("WMV", "舊相容", 0xE714, (f, p) => MediaOps.ToWmv(f, p)),
                        Zip, CopyPath, Hash,
                    });

                case FileKind.Audio:
                    return (new List<WheelAction>
                    {
                        A("MP3", "最通用", 0xE8D6, (f, p) => MediaOps.ToMp3(f, p)),
                        A("M4A", "AAC", 0xE8D6, (f, p) => MediaOps.ToM4a(f, p)),
                        A("WAV", "無損", 0xE8D6, (f, p) => MediaOps.ToWav(f, p)),
                        A("FLAC", "無損壓縮", 0xE8D6, (f, p) => MediaOps.ToFlac(f, p)),
                        A("ALAC", "Apple 無損", 0xE8D6, (f, p) => MediaOps.ToAlac(f, p)),
                        A("WMA", "Windows", 0xE8D6, (f, p) => MediaOps.ToWma(f, p)),
                    }, new List<WheelAction>
                    {
                        A("壓縮", "MP3 128k", 0xE73F, (f, p) => MediaOps.CompressAudio(f, p)),
                        A("單聲道", "MP3", 0xE767, (f, p) => MediaOps.Mono(f, p)),
                        Zip, CopyPath, Hash,
                    });

                case FileKind.Document:
                    return (new List<WheelAction>
                    {
                        A("PDF", "保留版面", 0xE8A5, (f, p) => OfficeOps.ToPdf(f, p)),
                        Zip, CopyPath, CopyName, Hash,
                    }, null);

                default:
                    return (new List<WheelAction> { Zip, CopyPath, CopyName, Hash }, null);
            }
        }
    }
}
