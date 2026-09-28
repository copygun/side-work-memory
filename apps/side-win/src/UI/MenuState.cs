using System.Text.Json.Nodes;

namespace Side.Win.UI;

public enum MenuCaptureState { Starting, Running, Paused, Stopped }

public enum MenuBanner { None, Starting, NotRunning, PermissionsNeeded, SomeUnavailable }

/// <summary>Result of the daemon "status" RPC fields the tray uses.</summary>
public sealed record MenuCaptureStatus(bool Enabled, MenuCaptureState State, long? PausedUntil, MenuBanner Banner)
{
    public static MenuCaptureStatus Parse(JsonNode? result)
    {
        if (result is not JsonObject value) throw new DaemonRpcException("invalid status");
        var state = value["state"]?.GetValue<string>() switch
        {
            "starting" => MenuCaptureState.Starting,
            "running" => MenuCaptureState.Running,
            "paused" => MenuCaptureState.Paused,
            "stopped" => MenuCaptureState.Stopped,
            _ => throw new DaemonRpcException("invalid status"),
        };
        var banner = value["banner"]?.GetValue<string>() switch
        {
            "none" => MenuBanner.None,
            "starting" => MenuBanner.Starting,
            "not_running" => MenuBanner.NotRunning,
            "permissions_needed" => MenuBanner.PermissionsNeeded,
            "some_unavailable" => MenuBanner.SomeUnavailable,
            _ => throw new DaemonRpcException("invalid status"),
        };
        var paused = value["paused_until"] is JsonValue p && p.TryGetValue<long>(out var until) ? until : (long?)null;
        return new MenuCaptureStatus(value["enabled"]?.GetValue<bool>() ?? false, state, paused, banner);
    }
}

/// <summary>Tray icon badge, mirrors the SF Symbol shown next to the macOS menu bar mark.</summary>
public enum MenuGlyph { Capturing, Paused, Warning }

/// <summary>Status line + badge (port of MenuDisplay).</summary>
public sealed record MenuDisplay(string Text, MenuGlyph Glyph, bool IsPaused)
{
    public const long PauseIndefinite = 9_007_199_254_740_991;

    public static MenuDisplay NotRunning(SideLanguage language) =>
        new(language.L("Capture is not running", "캡처가 실행 중이지 않습니다"), MenuGlyph.Warning, false);

    public static MenuDisplay From(MenuCaptureStatus status, SideLanguage language)
    {
        if (status.Banner == MenuBanner.PermissionsNeeded)
            return new(language.L("Permissions needed", "권한이 필요합니다"), MenuGlyph.Warning, false);
        if (!status.Enabled || status.Banner == MenuBanner.NotRunning) return NotRunning(language);
        switch (status.State)
        {
            case MenuCaptureState.Paused:
                if (status.PausedUntil is not { } until) return NotRunning(language);
                if (until == PauseIndefinite)
                    return new(language.L("Paused until you resume", "직접 재개할 때까지 일시정지됨"), MenuGlyph.Paused, true);
                var time = DateTimeOffset.FromUnixTimeMilliseconds(until).ToLocalTime().ToString("HH:mm");
                return new(language.L($"Paused until {time}", $"{time}까지 일시 중지"), MenuGlyph.Paused, true);
            case MenuCaptureState.Running:
                return new(language.L("Capturing", "캡처 중"),
                    status.Banner == MenuBanner.SomeUnavailable ? MenuGlyph.Warning : MenuGlyph.Capturing, false);
            default:
                return NotRunning(language);
        }
    }
}

public enum MenuPauseOption { FifteenMinutes, ThirtyMinutes, OneHour, UntilIResume }

public static class MenuPauseOptionExtensions
{
    public static string Title(this MenuPauseOption option, SideLanguage language) => option switch
    {
        MenuPauseOption.FifteenMinutes => language.L("15 minutes", "15분"),
        MenuPauseOption.ThirtyMinutes => language.L("30 minutes", "30분"),
        MenuPauseOption.OneHour => language.L("1 hour", "1시간"),
        _ => language.L("Until I resume", "직접 재개할 때까지"),
    };

    public static JsonObject RpcParams(this MenuPauseOption option) => option switch
    {
        MenuPauseOption.FifteenMinutes => new JsonObject { ["durationMs"] = 900_000 },
        MenuPauseOption.ThirtyMinutes => new JsonObject { ["durationMs"] = 1_800_000 },
        MenuPauseOption.OneHour => new JsonObject { ["durationMs"] = 3_600_000 },
        _ => new JsonObject { ["until"] = MenuDisplay.PauseIndefinite },
    };
}

/// <summary>Status cache + pause/resume (port of MenuBarState). Methods run on the UI thread.</summary>
public sealed class MenuState(DaemonRpcClient rpc)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private MenuCaptureStatus? _lastStatus;
    private DateTimeOffset? _lastRefresh;
    private int _refreshGeneration;
    private int _languageGeneration;

    public SideLanguage Language { get; private set; } = SideLanguage.Ko;
    public MenuDisplay Display { get; private set; } = MenuDisplay.NotRunning(SideLanguage.Ko);
    public bool IsWorking { get; private set; }
    public event Action? Changed;

    public void SetLanguage(SideLanguage next)
    {
        if (next == Language) return;
        Language = next;
        Display = _lastStatus is { } status ? MenuDisplay.From(status, next) : MenuDisplay.NotRunning(next);
        Changed?.Invoke();
    }

    /// <summary>Refresh language from settings, then status (menu opened).</summary>
    public async Task MenuDidOpenAsync()
    {
        var generation = ++_languageGeneration;
        try
        {
            var settings = await rpc.CallAsync("settings.get");
            if (generation == _languageGeneration &&
                SideLanguageExtensions.Parse(settings?["ui_language"]?.GetValue<string>()) is { } language)
                SetLanguage(language);
        }
        catch (Exception) { /* keep current language */ }
        await RefreshAsync(force: true);
    }

    public async Task RefreshAsync(bool force = false)
    {
        var startedAt = DateTimeOffset.Now;
        if (!force && _lastRefresh is { } last && startedAt - last < CacheDuration) return;
        var generation = ++_refreshGeneration;
        try
        {
            var status = MenuCaptureStatus.Parse(await rpc.CallAsync("status"));
            if (generation != _refreshGeneration) return;
            _lastStatus = status;
            Display = MenuDisplay.From(status, Language);
        }
        catch (Exception)
        {
            if (generation != _refreshGeneration) return;
            _lastStatus = null;
            Display = MenuDisplay.NotRunning(Language);
        }
        _lastRefresh = startedAt;
        Changed?.Invoke();
    }

    public Task PauseAsync(MenuPauseOption option) => RunAsync(async () =>
    {
        var result = await rpc.CallAsync("pause", option.RpcParams());
        if (result?["paused_until"] is not JsonValue value || !value.TryGetValue<long>(out _))
            throw new DaemonRpcException("invalid pause reply");
    });

    public Task ResumeAsync() => RunAsync(() => rpc.CallAsync("resume"));

    private async Task RunAsync(Func<Task> action)
    {
        if (IsWorking) return;
        IsWorking = true;
        Changed?.Invoke();
        try
        {
            await action();
            IsWorking = false;
            await RefreshAsync(force: true);
        }
        catch (Exception)
        {
            _lastStatus = null;
            _lastRefresh = null;
            Display = MenuDisplay.NotRunning(Language);
        }
        finally
        {
            IsWorking = false;
            Changed?.Invoke();
        }
    }
}
