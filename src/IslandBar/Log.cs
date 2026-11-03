using System;
using System.IO;

namespace IslandBar
{
    /// <summary>簡易記錄檔：%AppData%\IslandBar\log.txt（超過 512KB 自動清空）</summary>
    public static class Log
    {
        private static readonly object Lock = new object();

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar", "log.txt");

        public static void Write(string message)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        // 保留上一份，方便回報問題時看得到出事前的紀錄
                        string old = Path.Combine(Path.GetDirectoryName(FilePath), "log.old.txt");
                        try { File.Copy(FilePath, old, true); } catch { }
                        fi.Delete();
                    }
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
                }
            }
            catch { }
        }

        public static void Error(string where, Exception ex)
        {
            Write($"[ERROR] {where}: {ex.GetType().Name}: {ex.Message}");
        }

        /// <summary>嚴重錯誤：連同呼叫堆疊整個寫下來</summary>
        public static void Fatal(string where, Exception ex)
        {
            Write($"[FATAL] {where}: {ex}");
        }
    }
}
