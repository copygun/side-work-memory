using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Side.Win.UI;

/// <summary>
/// Tray icon = Side mark + status badge (the macOS menu bar shows the mark plus an SF Symbol).
/// Green dot = capturing, amber bars = paused, red "!" = not running / needs attention.
/// </summary>
public static class TrayIcons
{
    private static readonly Dictionary<(MenuGlyph, int), Icon> Cache = [];
    private static Icon? _appIcon;

    public static Icon AppIcon()
    {
        if (_appIcon is not null) return _appIcon;
        try
        {
            var path = UiPaths.AssetPath("Side.ico");
            if (File.Exists(path)) return _appIcon = new Icon(path);
        }
        catch (Exception) { }
        return _appIcon = Tray(MenuGlyph.Capturing, 32);
    }

    public static Icon Tray(MenuGlyph glyph, int size = 0)
    {
        if (size <= 0) size = SystemInformation.SmallIconSize.Width;
        if (Cache.TryGetValue((glyph, size), out var cached)) return cached;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            using var mark = LoadMark(size);
            if (mark is not null) g.DrawImage(mark, new Rectangle(0, 0, size, size));
            else DrawFallbackMark(g, size);
            DrawBadge(g, glyph, size);
        }
        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        Cache[(glyph, size)] = icon;
        return icon;
    }

    private static Image? LoadMark(int size)
    {
        foreach (var name in size > 32 ? new[] { "SideTray@2x.png", "SideTray.png" } : ["SideTray.png", "SideTray@2x.png"])
        {
            try
            {
                var path = UiPaths.AssetPath(name);
                if (File.Exists(path)) return Image.FromFile(path);
            }
            catch (Exception) { }
        }
        return null;
    }

    private static void DrawFallbackMark(Graphics g, int size)
    {
        using var brush = new SolidBrush(Color.FromArgb(255, 40, 40, 48));
        g.FillEllipse(brush, 0, 0, size - 1, size - 1);
        using var font = new Font("Segoe UI", size * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, "S", font, new Rectangle(0, 0, size, size), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static void DrawBadge(Graphics g, MenuGlyph glyph, int size)
    {
        var d = Math.Max(6, size * 7 / 16);
        var rect = new Rectangle(size - d, size - d, d - 1, d - 1);
        var fill = glyph switch
        {
            MenuGlyph.Capturing => Color.FromArgb(52, 199, 89),
            MenuGlyph.Paused => Color.FromArgb(255, 159, 10),
            _ => Color.FromArgb(255, 59, 48),
        };
        using (var outline = new Pen(Color.White, Math.Max(1f, size / 16f)))
        using (var brush = new SolidBrush(fill))
        {
            g.FillEllipse(brush, rect);
            g.DrawEllipse(outline, rect);
        }
        using var white = new SolidBrush(Color.White);
        if (glyph == MenuGlyph.Paused)
        {
            var w = Math.Max(1, d / 6);
            var h = d / 2;
            var top = rect.Top + (rect.Height - h) / 2;
            g.FillRectangle(white, rect.Left + d / 2 - w - w / 2 - 1, top, w, h);
            g.FillRectangle(white, rect.Left + d / 2 + w / 2, top, w, h);
        }
        else if (glyph == MenuGlyph.Warning)
        {
            var w = Math.Max(1, d / 6);
            g.FillRectangle(white, rect.Left + (rect.Width - w) / 2, rect.Top + d / 5, w, d / 2 - 1);
            g.FillRectangle(white, rect.Left + (rect.Width - w) / 2, rect.Bottom - d / 4, w, w);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
