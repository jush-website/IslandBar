using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace IslandBar.Conversion
{
    /// <summary>Windows 內建 OCR（依使用者語言；中文需在 Windows 設定安裝該語言的「光學字元辨識」）</summary>
    public static class OcrOps
    {
        public static async Task<string> RecognizeAsync(byte[] imageBytes)
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
                throw new InvalidOperationException("找不到 OCR 語言：請到「設定 → 時間與語言 → 語言」為你的語言安裝「光學字元辨識」");

            using var ras = new InMemoryRandomAccessStream();
            await ras.WriteAsync(imageBytes.AsBuffer());
            ras.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ras);
            uint max = OcrEngine.MaxImageDimension;
            uint w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
            var transform = new BitmapTransform();
            if (w > max || h > max)
            {
                double s = Math.Min((double)max / w, (double)max / h);
                transform.ScaledWidth = (uint)Math.Max(1, w * s);
                transform.ScaledHeight = (uint)Math.Max(1, h * s);
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }

            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            var result = await engine.RecognizeAsync(bitmap);

            var sb = new StringBuilder();
            foreach (var line in result.Lines)
            {
                // 中日韓文字之間不要插空白
                var words = line.Words.Select(x => x.Text).ToList();
                var lineSb = new StringBuilder();
                for (int i = 0; i < words.Count; i++)
                {
                    if (i > 0 && !(IsCjk(words[i - 1]) || IsCjk(words[i]))) lineSb.Append(' ');
                    lineSb.Append(words[i]);
                }
                sb.AppendLine(lineSb.ToString());
            }
            return sb.ToString().TrimEnd();
        }

        private static bool IsCjk(string s) =>
            !string.IsNullOrEmpty(s) && Regex.IsMatch(s, @"[　-〿぀-ヿ㐀-䶿一-鿿가-힯＀-￯]");
    }
}
