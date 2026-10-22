using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Conversion
{
    public sealed class ConvertResult
    {
        public string OutputPath { get; set; }   // 產生的檔案或資料夾（沒有就 null）
        public string Message { get; set; } = "";
    }

    /// <summary>輸出檔名、ZIP、SHA-256、剪貼簿等通用工具</summary>
    public static class FileOps
    {
        /// <summary>原檔旁邊的輸出路徑；同名就加 (2)、(3)…</summary>
        public static string OutputPath(string src, string ext, string suffix = null)
        {
            string dir = Path.GetDirectoryName(src) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string name = Path.GetFileNameWithoutExtension(src);
            string srcExt = Path.GetExtension(src).TrimStart('.').ToLowerInvariant();
            ext = ext.TrimStart('.').ToLowerInvariant();
            if (suffix == null && (srcExt == ext || (srcExt == "jpeg" && ext == "jpg"))) suffix = "_轉換";
            string baseName = name + (suffix ?? "");
            string path = Path.Combine(dir, baseName + "." + ext);
            int n = 2;
            while (File.Exists(path) || Directory.Exists(path))
                path = Path.Combine(dir, $"{baseName} ({n++}).{ext}");
            return path;
        }

        public static string UniqueFolder(string parentDir, string name)
        {
            string path = Path.Combine(parentDir, name);
            int n = 2;
            while (Directory.Exists(path) || File.Exists(path)) path = Path.Combine(parentDir, $"{name} ({n++})");
            Directory.CreateDirectory(path);
            return path;
        }

        public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        public static Task<ConvertResult> Zip(IReadOnlyList<string> items)
        {
            return Task.Run(() =>
            {
                string first = items[0];
                string dir = Path.GetDirectoryName(first.TrimEnd('\\', '/')) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string zipPath;
                if (items.Count == 1)
                {
                    string name = Directory.Exists(first) ? new DirectoryInfo(first).Name : Path.GetFileNameWithoutExtension(first);
                    zipPath = OutputPath(Path.Combine(dir, name + ".tmp"), "zip", "");
                }
                else
                {
                    zipPath = OutputPath(Path.Combine(dir, $"封存_{DateTime.Now:yyyyMMdd_HHmmss}.tmp"), "zip", "");
                }

                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    foreach (var item in items)
                    {
                        if (Directory.Exists(item))
                        {
                            var root = new DirectoryInfo(item);
                            foreach (var f in root.EnumerateFiles("*", SearchOption.AllDirectories))
                            {
                                string rel = Path.Combine(root.Name, Path.GetRelativePath(root.FullName, f.FullName));
                                zip.CreateEntryFromFile(f.FullName, rel.Replace('\\', '/'), CompressionLevel.Optimal);
                            }
                        }
                        else if (File.Exists(item))
                        {
                            zip.CreateEntryFromFile(item, Path.GetFileName(item), CompressionLevel.Optimal);
                        }
                    }
                }
                return new ConvertResult { OutputPath = zipPath, Message = "已壓縮：" + Path.GetFileName(zipPath) };
            });
        }

        public static async Task<ConvertResult> Sha256(IReadOnlyList<string> files)
        {
            var lines = new List<string>();
            foreach (var f in files.Where(File.Exists))
            {
                using var fs = File.OpenRead(f);
                using var sha = SHA256.Create();
                byte[] hash = await sha.ComputeHashAsync(fs);
                string hex = Convert.ToHexString(hash).ToLowerInvariant();
                lines.Add(files.Count == 1 ? hex : $"{hex}  {Path.GetFileName(f)}");
            }
            if (lines.Count == 0) throw new InvalidOperationException("資料夾不能算 SHA-256");
            SetClipboardText(string.Join(Environment.NewLine, lines));
            string shown = lines[0].Length > 16 ? lines[0].Substring(0, 16) + "…" : lines[0];
            return new ConvertResult { Message = $"SHA-256 已複製：{shown}" };
        }

        public static Task<ConvertResult> CopyPaths(IReadOnlyList<string> files)
        {
            SetClipboardText(string.Join(Environment.NewLine, files));
            return Task.FromResult(new ConvertResult { Message = files.Count == 1 ? "已複製路徑" : $"已複製 {files.Count} 個路徑" });
        }

        public static Task<ConvertResult> CopyNames(IReadOnlyList<string> files)
        {
            SetClipboardText(string.Join(Environment.NewLine, files.Select(f => Path.GetFileName(f.TrimEnd('\\', '/')))));
            return Task.FromResult(new ConvertResult { Message = files.Count == 1 ? "已複製檔名" : $"已複製 {files.Count} 個檔名" });
        }

        public static void SetClipboardText(string text)
        {
            OnUi(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    try { System.Windows.Clipboard.SetText(text); return; }
                    catch { Thread.Sleep(60); } // 剪貼簿被其他程式佔用時重試
                }
            });
        }

        public static void OnUi(Action action)
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) action();
            else d.Invoke(action);
        }
    }

    /// <summary>在獨立的 STA 執行緒上執行（WPF 影像處理需要 STA）</summary>
    internal static class Sta
    {
        public static Task<T> Run<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t = new Thread(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            return tcs.Task;
        }
    }
}
