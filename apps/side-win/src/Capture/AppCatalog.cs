using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Side.Win.Core;

namespace Side.Win.Capture;

/// <summary>
/// Installed + running applications for the deny-list picker (port of the applications()/icons()
/// parts of CommandRouter.swift). Installed apps come from Start Menu shortcuts (.lnk resolved via
/// WScript.Shell); running apps from processes that own a main window.
/// </summary>
internal static class AppCatalog
{
    private static readonly ConcurrentDictionary<string, string> PathsByBundle = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<RouterApplication> Applications(Func<string, bool> isDenied)
    {
        var found = new Dictionary<string, RouterApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var (bundleId, name, path) in StartMenuApps())
        {
            PathsByBundle[bundleId] = path;
            found.TryAdd(bundleId, new RouterApplication(bundleId, name, isDenied(bundleId)));
        }
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero) continue;
                var identity = ProcessIdentity.Of((uint)process.Id);
                if (identity is null || found.ContainsKey(identity.BundleId)) continue;
                PathsByBundle[identity.BundleId] = identity.Path;
                found[identity.BundleId] = new RouterApplication(identity.BundleId, identity.AppName, isDenied(identity.BundleId));
            }
            catch (Exception) { }
            finally { process.Dispose(); }
        }
        return found.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<(string BundleId, string Name, string Path)> StartMenuApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        dynamic? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is not null) shell = Activator.CreateInstance(type);
        }
        catch (Exception) { }
        if (shell is null) yield break;
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch (Exception) { continue; }
            foreach (var link in links)
            {
                string? target = null;
                try { target = (string)shell.CreateShortcut(link).TargetPath; }
                catch (Exception) { }
                if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var bundleId = Path.GetFileName(target).ToLowerInvariant();
                // Uninstallers and helpers are noise in the picker.
                if (bundleId.StartsWith("unins", StringComparison.Ordinal) || bundleId.Contains("uninstall")) continue;
                yield return (bundleId, Path.GetFileNameWithoutExtension(link), target);
            }
        }
    }

    public static IReadOnlyList<RouterIcon> Icons(IReadOnlyList<string> bundleIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var icons = new List<RouterIcon>();
        foreach (var bundleId in bundleIds)
        {
            if (!seen.Add(bundleId) || ResolvePath(bundleId) is not { } path) continue;
            if (IconPngBase64(path) is { } encoded) icons.Add(new RouterIcon(bundleId, encoded));
        }
        return icons;
    }

    private static string? ResolvePath(string bundleId)
    {
        if (PathsByBundle.TryGetValue(bundleId, out var path) && File.Exists(path)) return path;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var identity = ProcessIdentity.Of((uint)process.Id);
                if (identity is not null && identity.BundleId.Equals(bundleId, StringComparison.OrdinalIgnoreCase))
                {
                    PathsByBundle[bundleId] = identity.Path;
                    return identity.Path;
                }
            }
            catch (Exception) { }
            finally { process.Dispose(); }
        }
        return null;
    }

    public static string? IconPngBase64(string path)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            using var source = icon.ToBitmap();
            using var canvas = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(0, 0, 64, 64));
            }
            using var stream = new MemoryStream();
            canvas.Save(stream, ImageFormat.Png);
            return Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
