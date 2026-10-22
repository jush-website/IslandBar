using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace IslandBar.Conversion
{
    /// <summary>
    /// Word / PowerPoint 轉 PDF：
    /// 依序嘗試 Microsoft Office → WPS Office → LibreOffice（哪個有裝就用哪個），版面最接近原檔。
    /// </summary>
    public static class OfficeOps
    {
        private static readonly HashSet<string> WordExt = new HashSet<string> { "doc", "docx" };
        private static readonly HashSet<string> SlideExt = new HashSet<string> { "ppt", "pptx" };

        public static Task<ConvertResult> ToPdf(IReadOnlyList<string> files, IProgress<double> progress)
        {
            return Sta.Run(() =>
            {
                string last = null;
                var engines = new HashSet<string>();
                for (int i = 0; i < files.Count; i++)
                {
                    string src = files[i];
                    string outPath = FileOps.OutputPath(src, "pdf");
                    string ext = FileOps.Ext(src);
                    string used = null;
                    var errors = new List<string>();

                    if (WordExt.Contains(ext))
                    {
                        used = TryCom(() => WordToPdf("Word.Application", src, outPath), "Microsoft Word", errors)
                            ?? TryCom(() => WordToPdf("KWps.Application", src, outPath), "WPS 文字", errors);
                    }
                    else if (SlideExt.Contains(ext))
                    {
                        used = TryCom(() => SlidesToPdf("PowerPoint.Application", src, outPath), "Microsoft PowerPoint", errors)
                            ?? TryCom(() => SlidesToPdf("KWpp.Application", src, outPath), "WPS 簡報", errors);
                    }
                    else throw new InvalidOperationException("不支援的檔案：" + Path.GetFileName(src));

                    if (used == null && LibreOfficeToPdf(src, outPath, errors)) used = "LibreOffice";

                    if (used == null || !File.Exists(outPath))
                    {
                        Log.Write("Office→PDF failed: " + string.Join(" | ", errors));
                        if (errors.All(e => e.EndsWith("未安裝")))
                            throw new InvalidOperationException("要轉 PDF 需要安裝 Microsoft Office、WPS Office 或 LibreOffice 其中一個");
                        throw new InvalidOperationException("轉換失敗：" + errors.LastOrDefault());
                    }

                    engines.Add(used);
                    last = outPath;
                    progress?.Report((i + 1.0) / files.Count);
                }
                string by = string.Join("、", engines);
                return new ConvertResult
                {
                    OutputPath = last,
                    Message = files.Count == 1 ? $"已轉成 PDF（{by}）：{Path.GetFileName(last)}" : $"已轉成 {files.Count} 個 PDF（{by}）"
                };
            });
        }

        private sealed class NotInstalledException : Exception { }

        private static string TryCom(Action convert, string name, List<string> errors)
        {
            try
            {
                convert();
                return name;
            }
            catch (NotInstalledException)
            {
                errors.Add(name + " 未安裝");
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                errors.Add($"{name}：{inner.Message}");
                Log.Error("Office " + name, inner);
            }
            return null;
        }

        private static dynamic CreateApp(string progId)
        {
            var type = Type.GetTypeFromProgID(progId, false);
            if (type == null) throw new NotInstalledException();
            return Activator.CreateInstance(type);
        }

        private static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
        }

        private static void WordToPdf(string progId, string src, string outPath)
        {
            dynamic app = null, docs = null, doc = null;
            bool quit = true;
            try
            {
                app = CreateApp(progId);
                try { app.DisplayAlerts = 0; } catch { }
                docs = app.Documents;
                // 使用者本來就開著 Word 的話，轉完不要把他的 Word 關掉
                try { quit = (int)docs.Count == 0; } catch { }
                // Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles)
                doc = docs.Open(src, false, true, false);
                doc.ExportAsFixedFormat(outPath, 17); // wdExportFormatPDF
            }
            finally
            {
                try { doc?.Close(false); } catch { }
                Release(doc);
                Release(docs);
                if (quit) { try { app?.Quit(false); } catch { } }
                Release(app);
            }
        }

        private static void SlidesToPdf(string progId, string src, string outPath)
        {
            dynamic app = null, presentations = null, pres = null;
            bool quit = true;
            try
            {
                app = CreateApp(progId);
                presentations = app.Presentations;
                try { quit = (int)presentations.Count == 0; } catch { }
                // Open(FileName, ReadOnly = msoTrue, Untitled = msoFalse, WithWindow = msoFalse)
                pres = presentations.Open(src, -1, 0, 0);
                pres.SaveAs(outPath, 32); // ppSaveAsPDF
            }
            finally
            {
                try { pres?.Close(); } catch { }
                Release(pres);
                Release(presentations);
                if (quit) { try { app?.Quit(); } catch { } }
                Release(app);
            }
        }

        private static bool LibreOfficeToPdf(string src, string outPath, List<string> errors)
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string soffice = new[]
            {
                Path.Combine(pf, "LibreOffice", "program", "soffice.exe"),
                Path.Combine(pf86, "LibreOffice", "program", "soffice.exe")
            }.FirstOrDefault(File.Exists);
            if (soffice == null) { errors.Add("LibreOffice 未安裝"); return false; }

            string tmp = Path.Combine(Path.GetTempPath(), "IslandBar-office-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var psi = new ProcessStartInfo(soffice, $"--headless --norestore --convert-to pdf --outdir \"{tmp}\" \"{src}\"")
                {
                    CreateNoWindow = true, UseShellExecute = false
                };
                using var p = Process.Start(psi);
                if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } errors.Add("LibreOffice：逾時"); return false; }
                string produced = Directory.GetFiles(tmp, "*.pdf").FirstOrDefault();
                if (produced == null) { errors.Add("LibreOffice：沒有產生 PDF"); return false; }
                File.Move(produced, outPath);
                return true;
            }
            catch (Exception ex)
            {
                errors.Add("LibreOffice：" + ex.Message);
                return false;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
    }
}
