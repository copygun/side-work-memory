using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Side.Win.Core;

namespace Side.Win.Capture;

/// <summary>pid -> (lowercase exe name, display name, full path), validated by process creation time.</summary>
internal static class ProcessIdentity
{
    public sealed record Identity(string BundleId, string AppName, string Path, long CreatedAt);

    private static readonly ConcurrentDictionary<uint, Identity> Cache = new();

    public static Identity? Of(uint pid)
    {
        if (pid == 0) return null;
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            Native.GetProcessTimes(handle, out var created, out _, out _, out _);
            if (Cache.TryGetValue(pid, out var cached) && cached.CreatedAt == created) return cached;
            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            if (!Native.QueryFullProcessImageName(handle, 0, builder, ref size)) return null;
            var path = builder.ToString();
            var identity = new Identity(System.IO.Path.GetFileName(path).ToLowerInvariant(), DisplayName(path), path, created);
            Cache[pid] = identity;
            return identity;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    public static string DisplayName(string path) => Names.GetOrAdd(path, p =>
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(p);
            var name = string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch (Exception) { }
        return System.IO.Path.GetFileNameWithoutExtension(p);
    });
}

/// <summary>Current foreground app/window (AXSnapshotForeground analogue).</summary>
internal static class ForegroundReader
{
    public sealed record Snapshot(ForegroundInfo Info, IntPtr Hwnd);

    public static Snapshot? Current()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var identity = ProcessIdentity.Of(pid);
        if (identity is null) return null;
        // UWP apps are hosted by ApplicationFrameHost; the real app owns a child CoreWindow.
        if (identity.BundleId == "applicationframehost.exe" && HostedApp(hwnd, pid) is { } hosted)
        {
            identity = hosted.Item1;
            pid = hosted.Item2;
        }
        return new Snapshot(
            new ForegroundInfo(identity.BundleId, identity.AppName, (int)pid, Native.WindowId(hwnd), Native.WindowTitle(hwnd)),
            hwnd);
    }

    private static (ProcessIdentity.Identity, uint)? HostedApp(IntPtr frame, uint framePid)
    {
        (ProcessIdentity.Identity, uint)? found = null;
        Native.EnumChildWindows(frame, (child, _) =>
        {
            Native.GetWindowThreadProcessId(child, out var childPid);
            if (childPid != framePid && ProcessIdentity.Of(childPid) is { } identity)
            {
                found = (identity, childPid);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
