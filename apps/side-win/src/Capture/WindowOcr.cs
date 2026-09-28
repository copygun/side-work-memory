using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Side.Win.Capture;

/// <summary>
/// Window OCR (port of WindowOCR.swift). Pixels come from PrintWindow(PW_RENDERFULLCONTENT) of the
/// target HWND instead of Windows.Graphics.Capture: it needs no D3D device/frame pool, shows no capture
/// border, and captures the window even when partly covered. Text comes from Windows.Media.Ocr,
/// preferring the Korean engine (it also reads Latin) and falling back to English / user languages.
/// </summary>
internal static class WindowOcr
{
    private static readonly Lazy<(OcrEngine? Engine, string[] Languages)> Engines = new(CreateEngine);

    public static bool Available => Engines.Value.Engine is not null;
    public static IReadOnlyList<string> Languages => Engines.Value.Languages;

    private static (OcrEngine?, string[]) CreateEngine()
    {
        try
        {
            var available = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
            var reported = new List<string>();
            if (available.Any(tag => tag.StartsWith("ko", StringComparison.OrdinalIgnoreCase))) reported.Add("ko-KR");
            if (available.Any(tag => tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))) reported.Add("en-US");
            OcrEngine? engine = null;
            foreach (var tag in new[] { "ko", "ko-KR", "en-US", "en" })
            {
                var language = new Language(tag);
                if (OcrEngine.IsLanguageSupported(language))
                {
                    engine = OcrEngine.TryCreateFromLanguage(language);
                    if (engine is not null) break;
                }
            }
            engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is not null && reported.Count == 0) reported.Add(engine.RecognizerLanguage.LanguageTag);
            return (engine, reported.ToArray());
        }
        catch (Exception)
        {
            return (null, []);
        }
    }

    public static async Task<string?> CaptureTextAsync(IntPtr hwnd, CancellationToken cancellationToken)
    {
        var engine = Engines.Value.Engine;
        if (engine is null || !Native.IsWindow(hwnd) || Native.IsIconic(hwnd)) return null;
        using var bitmap = Capture(hwnd);
        if (bitmap is null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        using var software = ToSoftwareBitmap(bitmap, (int)OcrEngine.MaxImageDimension);
        if (software is null) return null;
        var result = await engine.RecognizeAsync(software).AsTask(cancellationToken).ConfigureAwait(false);
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }

    private static Bitmap? Capture(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 16_384 || height > 16_384) return null;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            try
            {
                if (!Native.PrintWindow(hwnd, hdc, Native.PW_RENDERFULLCONTENT))
                {
                    graphics.ReleaseHdc(hdc);
                    hdc = IntPtr.Zero;
                    bitmap.Dispose();
                    return null;
                }
            }
            finally
            {
                if (hdc != IntPtr.Zero) graphics.ReleaseHdc(hdc);
            }
        }
        return bitmap;
    }

    private static SoftwareBitmap? ToSoftwareBitmap(Bitmap source, int maxDimension)
    {
        var bitmap = source;
        var scaled = false;
        if (maxDimension > 0 && (source.Width > maxDimension || source.Height > maxDimension))
        {
            var scale = Math.Min((double)maxDimension / source.Width, (double)maxDimension / source.Height);
            bitmap = new Bitmap(source, Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            scaled = true;
        }
        try
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = bitmap.Width * 4;
                var bytes = new byte[stride * bitmap.Height];
                var anyInk = false;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * stride, stride);
                }
                // Opaque alpha and detect an all-black capture (GPU surfaces PrintWindow could not render).
                for (var i = 0; i < bytes.Length; i += 4)
                {
                    bytes[i + 3] = 255;
                    if (!anyInk && (bytes[i] | bytes[i + 1] | bytes[i + 2]) != 0) anyInk = true;
                }
                if (!anyInk) return null;
                return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Premultiplied);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        finally
        {
            if (scaled) bitmap.Dispose();
        }
    }
}
